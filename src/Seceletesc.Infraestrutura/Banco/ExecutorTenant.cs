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

    internal static async Task DefinirTenantAsync(NpgsqlConnection conexao, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var comando = new NpgsqlCommand("select set_config('app.tenant_id', @t, true)", conexao, tx);
        comando.Parameters.AddWithValue("t", tenantId.ToString());
        await comando.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
