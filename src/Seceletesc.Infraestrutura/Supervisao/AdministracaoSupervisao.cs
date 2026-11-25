using Npgsql;
using Seceletesc.Infraestrutura.Banco;

namespace Seceletesc.Infraestrutura.Supervisao;

/// <summary>
/// Cadastro de empresas e usuários da supervisão. Roda com a conexão do papel dono (a mesma das migrações),
/// por linha de comando, nunca pela API. Autenticação de desenvolvimento: decisão pendente para produção.
/// </summary>
public static class AdministracaoSupervisao
{
    public static async Task<Guid> CriarTenantAsync(string connectionStringDono, string nome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        var id = Guid.NewGuid();
        await using var ds = NpgsqlDataSource.Create(connectionStringDono);
        // tenants tem RLS forçado também para o dono: a inserção acontece com o tenant novo na transação.
        await new ExecutorTenant(ds).ExecutarAsync(id, async (c, tx, t) =>
        {
            await using var cmd = new NpgsqlCommand("insert into tenants (id, nome) values (@id, @n)", c, tx);
            cmd.Parameters.AddWithValue("id", id);
            cmd.Parameters.AddWithValue("n", nome.Trim());
            return await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>Cria o usuário e devolve a chave de API. A chave só aparece aqui: o banco guarda o hash.</summary>
    public static async Task<(Guid UsuarioId, string Chave)> CriarSupervisorAsync(string connectionStringDono, Guid tenantId, string nome, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        var id = Guid.NewGuid();
        var chave = ServicoSupervisao.GerarChave();
        await using var c = new NpgsqlConnection(connectionStringDono);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("insert into usuarios_supervisao (id, tenant_id, nome, chave_sha256) values (@id, @t, @n, @h)", c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("n", nome.Trim());
        cmd.Parameters.AddWithValue("h", ServicoSupervisao.HashChave(chave));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (id, chave);
    }

    public static async Task<bool> DesativarSupervisorAsync(string connectionStringDono, Guid usuarioId, CancellationToken ct = default)
    {
        await using var c = new NpgsqlConnection(connectionStringDono);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("update usuarios_supervisao set ativo = false where id = @id", c);
        cmd.Parameters.AddWithValue("id", usuarioId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }
}
