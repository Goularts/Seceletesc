using System.Security.Cryptography;
using System.Text;

namespace Seceletesc.Infraestrutura.Criptografia;

/// <summary>
/// Cifra as credenciais do WhatsApp de cada empresa (business token) para guardar no banco (plano técnico, seção 4.13).
/// AES-256-GCM com chave da configuração (nunca no banco nem no Git). Os dados associados ligam a cifra à empresa e à
/// WABA: copiar a linha para outra empresa ou conta não decifra. Formato: nonce (12) || tag (16) || cifra.
/// </summary>
public sealed class CifradorCredenciais
{
    private const int TamanhoNonce = 12;
    private const int TamanhoTag = 16;
    private readonly byte[] _chave;

    public CifradorCredenciais(byte[] chave)
    {
        ArgumentNullException.ThrowIfNull(chave);
        if (chave.Length != 32)
        {
            throw new ArgumentException("A chave das credenciais precisa ter 32 bytes (AES-256).", nameof(chave));
        }

        _chave = (byte[])chave.Clone();
        ChaveId = Convert.ToHexStringLower(SHA256.HashData(_chave))[..16];
    }

    /// <summary>Impressão da chave (não revela a chave): permite trocar a chave e saber qual cifrou cada credencial.</summary>
    public string ChaveId { get; }

    /// <summary>Lê a chave em base64 da configuração (WhatsApp:ChaveCredenciais). Nulo se ausente ou inválida.</summary>
    public static CifradorCredenciais? DaConfiguracao(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            var chave = Convert.FromBase64String(base64.Trim());
            return chave.Length == 32 ? new CifradorCredenciais(chave) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string GerarChaveBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public byte[] Cifrar(string token, Guid tenantId, string wabaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(wabaId);
        var claro = Encoding.UTF8.GetBytes(token);
        var saida = new byte[TamanhoNonce + TamanhoTag + claro.Length];
        var nonce = saida.AsSpan(0, TamanhoNonce);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_chave, TamanhoTag);
        aes.Encrypt(nonce, claro, saida.AsSpan(TamanhoNonce + TamanhoTag), saida.AsSpan(TamanhoNonce, TamanhoTag), Associados(tenantId, wabaId));
        CryptographicOperations.ZeroMemory(claro);
        return saida;
    }

    /// <summary>Decifra; nulo se a cifra foi alterada, é de outra empresa ou WABA, ou de outra chave (falha fechada).</summary>
    public string? Decifrar(byte[] cifrado, Guid tenantId, string wabaId)
    {
        ArgumentNullException.ThrowIfNull(cifrado);
        if (cifrado.Length <= TamanhoNonce + TamanhoTag || string.IsNullOrEmpty(wabaId))
        {
            return null;
        }

        var claro = new byte[cifrado.Length - TamanhoNonce - TamanhoTag];
        try
        {
            using var aes = new AesGcm(_chave, TamanhoTag);
            aes.Decrypt(cifrado.AsSpan(0, TamanhoNonce), cifrado.AsSpan(TamanhoNonce + TamanhoTag), cifrado.AsSpan(TamanhoNonce, TamanhoTag), claro, Associados(tenantId, wabaId));
            return Encoding.UTF8.GetString(claro);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(claro);
        }
    }

    private static byte[] Associados(Guid tenantId, string wabaId) => Encoding.UTF8.GetBytes("seceletesc-credencial-whatsapp|" + tenantId.ToString("D") + "|" + wabaId);
}
