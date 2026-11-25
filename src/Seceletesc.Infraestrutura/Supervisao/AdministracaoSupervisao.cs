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

    /// <summary>
    /// Liga um número do WhatsApp (phone_number_id da Meta) a uma empresa: é por ele que o webhook descobre a empresa
    /// de cada mensagem. Um número pertence a uma única empresa.
    /// </summary>
    public static async Task RegistrarNumeroAsync(string connectionStringDono, Guid tenantId, string phoneNumberId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumberId);
        await using var ds = NpgsqlDataSource.Create(connectionStringDono);
        // Com registro no histórico da empresa (revisão da F4.5, S6): quem liga um número a uma empresa fica auditado.
        await new ExecutorTenant(ds).ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            await using var cmd = new NpgsqlCommand("""
                insert into tenant_numeros_whatsapp (phone_number_id, tenant_id) values (@p, @t);
                insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
                values (@t, null, 'numero_whatsapp_registrado', jsonb_build_object('phone_number_id', @p::text, 'origem', 'linha de comando'));
                """, c, tx);
            cmd.Parameters.AddWithValue("p", phoneNumberId.Trim());
            cmd.Parameters.AddWithValue("t", tenantId);
            return await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Cria o usuário e devolve a chave de API. A chave só aparece aqui: o banco guarda o hash.</summary>
    public static async Task<(Guid UsuarioId, string Chave)> CriarSupervisorAsync(
        string connectionStringDono, Guid tenantId, string nome, PapelSupervisao papel = PapelSupervisao.Atendente, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        var id = Guid.NewGuid();
        var chave = ServicoSupervisao.GerarChave();
        await using var c = new NpgsqlConnection(connectionStringDono);
        await c.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("insert into usuarios_supervisao (id, tenant_id, nome, chave_sha256, papel) values (@id, @t, @n, @h, @p)", c);
        cmd.Parameters.AddWithValue("p", papel.ToString());
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("n", nome.Trim());
        cmd.Parameters.AddWithValue("h", ServicoSupervisao.HashChave(chave));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return (id, chave);
    }

    /// <summary>Troca o papel. Vale na próxima requisição: o papel é lido a cada autenticação. Fica no histórico da empresa.</summary>
    public static Task<bool> AlterarPapelAsync(string connectionStringDono, Guid usuarioId, PapelSupervisao papel, CancellationToken ct = default) =>
        AlterarUsuarioAsync(connectionStringDono, usuarioId, "papel = @p", "papel_alterado", papel.ToString(), ct);

    public static Task<bool> DesativarSupervisorAsync(string connectionStringDono, Guid usuarioId, CancellationToken ct = default) =>
        AlterarUsuarioAsync(connectionStringDono, usuarioId, "ativo = false", "usuario_desativado", null, ct);

    /// <summary>Altera o usuário e registra o evento no histórico da empresa dele, na mesma transação.</summary>
    private static async Task<bool> AlterarUsuarioAsync(string connectionStringDono, Guid usuarioId, string alteracao, string evento, string? papel, CancellationToken ct)
    {
        // Pool próprio: a conexão recebe o tenant (para o evento) e não deve voltar ao pool compartilhado com isso.
        await using var ds = NpgsqlDataSource.Create(connectionStringDono);
        await using var c = await ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        string? papelAnterior;
        Guid tenant;
        await using (var sel = new NpgsqlCommand("select tenant_id, papel from usuarios_supervisao where id = @id for update", c, tx))
        {
            sel.Parameters.AddWithValue("id", usuarioId);
            await using var r = await sel.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return false;
            }

            tenant = r.GetGuid(0);
            papelAnterior = r.GetString(1);
        }

        await using (var upd = new NpgsqlCommand($"update usuarios_supervisao set {alteracao} where id = @id", c, tx))
        {
            upd.Parameters.AddWithValue("id", usuarioId);
            upd.Parameters.AddWithValue("p", (object?)papel ?? DBNull.Value);
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await ExecutorTenant.DefinirTenantAsync(c, tx, tenant, ct).ConfigureAwait(false);
        await using (var ev = new NpgsqlCommand("insert into eventos_historico (tenant_id, tipo, dados) values (@t, @tipo, @d::jsonb)", c, tx))
        {
            ev.Parameters.AddWithValue("t", tenant);
            ev.Parameters.AddWithValue("tipo", evento);
            ev.Parameters.AddWithValue("d", System.Text.Json.JsonSerializer.Serialize(new { usuario = usuarioId, de = papelAnterior, para = papel, origem = "linha de comando" }));
            await ev.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
