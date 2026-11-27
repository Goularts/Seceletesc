using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Criptografia;

namespace Seceletesc.Infraestrutura.WhatsApp;

/// <summary>Credencial de envio de um número já decifrada. Existe só durante o envio; nunca vai para log.</summary>
public sealed class CredencialEnvio(string wabaId, string token)
{
    public string WabaId { get; } = wabaId;

    public string Token { get; } = token;

    public override string ToString() => "CredencialEnvio { WabaId = " + WabaId + ", Token = *** }";
}

/// <summary>
/// Busca a credencial de envio de um número da empresa (plano técnico, seção 4.13). O banco só devolve a cifra quando
/// as três etapas estão cumpridas (conta conectada com credencial vigente, operação autorizada, envio habilitado); a
/// decifragem é feita aqui, com a chave da configuração. Qualquer falta, inclusive chave trocada, devolve nulo: nada sai.
/// </summary>
public sealed class RepositorioCredenciaisWhatsApp(NpgsqlDataSource dataSourceWorker, CifradorCredenciais cifrador)
{
    private readonly ExecutorTenant _executor = new(dataSourceWorker);

    public Task<CredencialEnvio?> ObterParaEnvioAsync(Guid tenantId, string phoneNumberId, CancellationToken ct) =>
        _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            await using var cmd = new NpgsqlCommand("select waba_id, token_cifrado, chave_id from credencial_envio(@p)", c, tx);
            cmd.Parameters.AddWithValue("p", phoneNumberId);
            await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
            if (!await r.ReadAsync(t).ConfigureAwait(false))
            {
                return null;
            }

            var waba = r.GetString(0);
            var cifrado = r.GetFieldValue<byte[]>(1);
            if (!string.Equals(r.GetString(2), cifrador.ChaveId, StringComparison.Ordinal))
            {
                return null;
            }

            return cifrador.Decifrar(cifrado, tenantId, waba) is { } token ? new CredencialEnvio(waba, token) : null;
        }, ct);
}

/// <summary>
/// Gateway usado quando o envio está ligado mas a chave das credenciais falta ou é inválida (revisão da M1, S2): recusa
/// tudo de forma definitiva, sem chamar a Meta, para que cada mensagem seja cancelada e vá para a supervisão.
/// </summary>
public sealed class GatewaySemCredenciais : Aplicacao.Envio.IGatewayWhatsApp
{
    public Task<Aplicacao.Envio.ResultadoEnvio> EnviarTextoAsync(Guid tenantId, string? numeroOrigem, string waIdDestino, string texto, CancellationToken ct) =>
        Task.FromResult(new Aplicacao.Envio.ResultadoEnvio(Aplicacao.Envio.SituacaoEnvio.FalhaDefinitiva, null, "chave das credenciais ausente ou inválida"));
}
