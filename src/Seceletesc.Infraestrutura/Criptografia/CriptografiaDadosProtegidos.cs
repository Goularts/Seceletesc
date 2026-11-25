using System.Security.Cryptography;
using System.Text;

namespace Seceletesc.Infraestrutura.Criptografia;

/// <summary>
/// Valor cifrado: impressão digital da chave pública usada (rotação), chave AES por registro envolvida
/// por RSA-OAEP-SHA256, nonce, texto cifrado e tag.
/// </summary>
public sealed record DadoCifrado(string ChaveId, byte[] ChaveCifrada, byte[] Nonce, byte[] Cifrado, byte[] Tag);

/// <summary>A que registro o dado pertence. Entra como dado associado do AES-GCM.</summary>
public sealed record ContextoCifragem(Guid TenantId, Guid DadoId, Guid ConversaId, Guid MensagemId, string Categoria)
{
    internal byte[] DadosAssociados() =>
        [.. TenantId.ToByteArray(), .. DadoId.ToByteArray(), .. ConversaId.ToByteArray(), .. MensagemId.ToByteArray(), .. Encoding.UTF8.GetBytes(Categoria)];
}

/// <summary>
/// Cifra dados protegidos (invariante 23). Usa só a CHAVE PÚBLICA: o pipeline consegue cifrar, mas não
/// consegue decifrar. O texto cifrado fica amarrado ao tenant, à conversa, à mensagem, ao registro e à
/// categoria: copiar o valor para outra linha faz a decifragem falhar.
/// </summary>
public sealed class CifradorDadosProtegidos : IDisposable
{
    private readonly RSA _publica;

    public CifradorDadosProtegidos(string chavePublicaPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chavePublicaPem);
        if (chavePublicaPem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new ArgumentException("O cifrador recebe só a chave pública. A chave privada fica com a supervisão.", nameof(chavePublicaPem));
        }

        _publica = RSA.Create();
        _publica.ImportFromPem(chavePublicaPem);
        if (_publica.KeySize < 3072)
        {
            throw new ArgumentException("Chave RSA de pelo menos 3072 bits.", nameof(chavePublicaPem));
        }

        ChaveId = ImpressaoDigital(_publica);
    }

    /// <summary>SHA-256 da chave pública (SubjectPublicKeyInfo). Identifica qual par de chaves decifra o dado.</summary>
    public string ChaveId { get; }

    public DadoCifrado Cifrar(string valor, ContextoCifragem contexto)
    {
        ArgumentNullException.ThrowIfNull(valor);
        ArgumentNullException.ThrowIfNull(contexto);
        var chave = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var claro = Encoding.UTF8.GetBytes(valor);
            var cifrado = new byte[claro.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            using (var aes = new AesGcm(chave, tag.Length))
            {
                aes.Encrypt(nonce, claro, cifrado, tag, contexto.DadosAssociados());
            }

            CryptographicOperations.ZeroMemory(claro);
            return new DadoCifrado(ChaveId, _publica.Encrypt(chave, RSAEncryptionPadding.OaepSHA256), nonce, cifrado, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    public void Dispose() => _publica.Dispose();

    internal static string ImpressaoDigital(RSA rsa) => Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo()));
}

/// <summary>
/// Decifra dados protegidos. Exige a CHAVE PRIVADA, que só a API de supervisão (F2) carrega,
/// para mostrar o dado a um humano com permissão.
/// </summary>
public sealed class DecifradorDadosProtegidos : IDisposable
{
    private readonly RSA _privada;
    private readonly string _chaveId;

    public DecifradorDadosProtegidos(string chavePrivadaPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chavePrivadaPem);
        _privada = RSA.Create();
        _privada.ImportFromPem(chavePrivadaPem);
        _chaveId = CifradorDadosProtegidos.ImpressaoDigital(_privada);
    }

    public string Decifrar(DadoCifrado dado, ContextoCifragem contexto)
    {
        ArgumentNullException.ThrowIfNull(dado);
        ArgumentNullException.ThrowIfNull(contexto);
        if (!string.Equals(dado.ChaveId, _chaveId, StringComparison.Ordinal))
        {
            throw new CryptographicException("O dado foi cifrado com outra chave (ver rotação de chaves).");
        }

        var chave = _privada.Decrypt(dado.ChaveCifrada, RSAEncryptionPadding.OaepSHA256);
        try
        {
            var claro = new byte[dado.Cifrado.Length];
            using var aes = new AesGcm(chave, dado.Tag.Length);
            aes.Decrypt(dado.Nonce, dado.Cifrado, dado.Tag, claro, contexto.DadosAssociados());
            return Encoding.UTF8.GetString(claro);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    public void Dispose() => _privada.Dispose();
}
