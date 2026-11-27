using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Criptografia;

namespace Seceletesc.Infraestrutura.Supervisao;

/// <summary>
/// Operações do operador da plataforma sobre as contas do WhatsApp de uma empresa (decisão 34; plano técnico, seção 4.13).
/// Cada uma chama uma função do banco que confere a empresa, o operador e o motivo e grava o evento no histórico na
/// mesma transação. Nesta etapa, rodam pela linha de comando com a conexão do dono; a API de cadastro vem na M3.
/// </summary>
public static class OperacoesPlataforma
{
    /// <summary>Formato dos identificadores da Meta (numéricos); "demo-" só para a demonstração local.</summary>
    public static bool IdentificadorValido(string? id) =>
        id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, @"^(\d{5,30}|demo-[a-f0-9]{4,32})$");

    /// <summary>Token com formato aceitável (caracteres visíveis, sem espaços); o conteúdo nunca é exibido.</summary>
    public static bool TokenValido(string? token) =>
        token is { Length: >= 20 and <= 4096 } && token.All(ch => ch is > ' ' and < (char)127);

    public static Task ConectarContaAsync(string conexaoDono, Guid tenant, string waba, string? businessId, string operador, CancellationToken ct = default) =>
        ExecutarAsync(conexaoDono, tenant, "select plataforma_conectar_conta(@t, @w, @b, @o)",
            p => { p.AddWithValue("w", waba); p.AddWithValue("b", (object?)businessId ?? DBNull.Value); p.AddWithValue("o", operador); }, ct);

    public static Task RegistrarCredencialAsync(string conexaoDono, CifradorCredenciais cifrador, Guid tenant, string waba, string token, string operador, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cifrador);
        if (!TokenValido(token))
        {
            throw new ArgumentException("Token em formato inesperado.", nameof(token));
        }

        var cifrado = cifrador.Cifrar(token, tenant, waba);
        return ExecutarAsync(conexaoDono, tenant, "select plataforma_registrar_credencial(@t, @w, @k, @c, @o)",
            p => { p.AddWithValue("w", waba); p.AddWithValue("k", cifrado); p.AddWithValue("c", cifrador.ChaveId); p.AddWithValue("o", operador); }, ct);
    }

    public static Task DesconectarContaAsync(string conexaoDono, Guid tenant, string waba, string operador, string motivo, CancellationToken ct = default) =>
        ExecutarAsync(conexaoDono, tenant, "select plataforma_desconectar_conta(@t, @w, @o, @m)",
            p => { p.AddWithValue("w", waba); p.AddWithValue("o", operador); p.AddWithValue("m", motivo); }, ct);

    public static Task RegistrarNumeroAsync(string conexaoDono, Guid tenant, string phoneNumberId, string? waba, string operador, CancellationToken ct = default) =>
        ExecutarAsync(conexaoDono, tenant, "select plataforma_registrar_numero(@t, @p, @w, @o)",
            p => { p.AddWithValue("p", phoneNumberId); p.AddWithValue("w", (object?)waba ?? DBNull.Value); p.AddWithValue("o", operador); }, ct);

    public static Task AutorizarOperacaoAsync(string conexaoDono, Guid tenant, string phoneNumberId, string operador, string motivo, bool autorizar, CancellationToken ct = default) =>
        ExecutarAsync(conexaoDono, tenant, "select plataforma_autorizar_operacao(@t, @p, @o, @m, @x)",
            p => { p.AddWithValue("p", phoneNumberId); p.AddWithValue("o", operador); p.AddWithValue("m", motivo); p.AddWithValue("x", autorizar); }, ct);

    public static Task AlterarEnvioAsync(string conexaoDono, Guid tenant, string phoneNumberId, string operador, string motivo, bool habilitar, CancellationToken ct = default) =>
        ExecutarAsync(conexaoDono, tenant, "select plataforma_alterar_envio(@t, @p, @o, @m, @x)",
            p => { p.AddWithValue("p", phoneNumberId); p.AddWithValue("o", operador); p.AddWithValue("m", motivo); p.AddWithValue("x", habilitar); }, ct);

    private static async Task ExecutarAsync(string conexaoDono, Guid tenant, string sql, Action<NpgsqlParameterCollection> parametros, CancellationToken ct)
    {
        await using var ds = NpgsqlDataSource.Create(conexaoDono);
        await new ExecutorTenant(ds).ExecutarAsync(tenant, async (c, tx, t) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", tenant);
            parametros(cmd.Parameters);
            return await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
