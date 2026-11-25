using System.Security.Cryptography;
using System.Text;

namespace Seceletesc.Infraestrutura.Criptografia;

/// <summary>Valor cifrado: chave AES por registro (envolvida por RSA-OAEP-SHA256), nonce, texto cifrado e tag.</summary>
public sealed record DadoCifrado(byte[] ChaveCifrada, byte[] Nonce, byte[] Cifrado, byte[] Tag);

/// <summary>
/// Cifra dados protegidos (invariante 23). Usa só a CHAVE PÚBLICA: o pipeline consegue cifrar, mas não
/// consegue decifrar. O texto cifrado fica amarrado ao tenant e ao registro (dados associados do AES-GCM):
/// copiar o valor para outra linha ou outro tenant faz a decifragem falhar.
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
    }

    public DadoCifrado Cifrar(string valor, Guid tenantId, Guid dadoId)
    {
        ArgumentNullException.ThrowIfNull(valor);
        var chave = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
            var claro = Encoding.UTF8.GetBytes(valor);
            var cifrado = new byte[claro.Length];
            var tag = new byte[AesGcm.TagByteSizes.MaxSize];
            using (var aes = new AesGcm(chave, tag.Length))
            {
                aes.Encrypt(nonce, claro, cifrado, tag, DadosAssociados(tenantId, dadoId));
            }

            CryptographicOperations.ZeroMemory(claro);
            return new DadoCifrado(_publica.Encrypt(chave, RSAEncryptionPadding.OaepSHA256), nonce, cifrado, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    public void Dispose() => _publica.Dispose();

    internal static byte[] DadosAssociados(Guid tenantId, Guid dadoId) => [.. tenantId.ToByteArray(), .. dadoId.ToByteArray()];
}

/// <summary>
/// Decifra dados protegidos. Exige a CHAVE PRIVADA, que só a API de supervisão (F2) carrega,
/// para mostrar o dado a um humano com permissão.
/// </summary>
public sealed class DecifradorDadosProtegidos : IDisposable
{
    private readonly RSA _privada;

    public DecifradorDadosProtegidos(string chavePrivadaPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chavePrivadaPem);
        _privada = RSA.Create();
        _privada.ImportFromPem(chavePrivadaPem);
    }

    public string Decifrar(DadoCifrado dado, Guid tenantId, Guid dadoId)
    {
        ArgumentNullException.ThrowIfNull(dado);
        var chave = _privada.Decrypt(dado.ChaveCifrada, RSAEncryptionPadding.OaepSHA256);
        try
        {
            var claro = new byte[dado.Cifrado.Length];
            using var aes = new AesGcm(chave, dado.Tag.Length);
            aes.Decrypt(dado.Nonce, dado.Cifrado, dado.Tag, claro, CifradorDadosProtegidos.DadosAssociados(tenantId, dadoId));
            return Encoding.UTF8.GetString(claro);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(chave);
        }
    }

    public void Dispose() => _privada.Dispose();
}
