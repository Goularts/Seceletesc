using Npgsql;

namespace Seceletesc.Infraestrutura.Banco;

/// <summary>
/// Executa trabalho dentro de uma transação vinculada a um tenant. O tenant é definido com
/// set_config('app.tenant_id', ..., true), equivalente a SET LOCAL: vale só para a transação
/// e nunca fica preso à conexão devolvida ao pool. Ver plano técnico, seção 4.5.
/// </summary>
public sealed class ExecutorTenant(NpgsqlDataSource dataSource)
{
    public async Task<T> ExecutarAsync<T>(
        Guid tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> trabalho,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trabalho);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant não pode ser vazio.", nameof(tenantId));
        }

        await using var conexao = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conexao.BeginTransactionAsync(ct).ConfigureAwait(false);

        await DefinirTenantAsync(conexao, tx, tenantId, ct).ConfigureAwait(false);
        var resultado = await trabalho(conexao, tx, ct).ConfigureAwait(false);

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return resultado;
    }

    /// <summary>
    /// Igual a <see cref="ExecutarAsync{T}"/>, mas antes comprova no banco a identidade do usuário humano
    /// (autenticar_transacao, migração 0005). Toda autoria gravada na transação precisa ser a desse usuário:
    /// o banco não confia no id que a aplicação informa. Sem credencial, falha antes de abrir a conexão.
    /// </summary>
    public Task<T> ExecutarAutenticadoAsync<T>(
        Guid tenantId,
        string? credencial,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> trabalho,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trabalho);
        if (string.IsNullOrEmpty(credencial))
        {
            throw new InvalidOperationException("Operação da supervisão sem credencial do usuário.");
        }

        return ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            await using (var comando = new NpgsqlCommand("select 1 from autenticar_transacao(@k)", c, tx))
            {
                comando.Parameters.AddWithValue("k", credencial);
                await comando.ExecuteScalarAsync(t).ConfigureAwait(false);
            }

            return await trabalho(c, tx, t).ConfigureAwait(false);
        }, ct);
    }

    internal static async Task DefinirTenantAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var comando = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conexao, tx);
        comando.Parameters.AddWithValue("t", tenantId.ToString());
        await comando.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
