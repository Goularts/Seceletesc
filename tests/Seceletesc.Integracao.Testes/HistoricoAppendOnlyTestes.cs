using Npgsql;
using Seceletesc.Infraestrutura.Banco;

namespace Seceletesc.Integracao.Testes;

/// <summary>Invariante 15: histórico estruturado, auditável e persistente (append-only). Plano técnico, seção 4.7.</summary>
[Collection(ColecaoBanco.Nome)]
public sealed class HistoricoAppendOnlyTestes(BancoFixture banco)
{
    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Aplicacao_pode_inserir_evento_no_proprio_tenant()
    {
        var executor = new ExecutorTenant(banco.App);

        var linhas = await executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(
                "insert into eventos_historico (tenant_id, tipo, dados) values (@t, 'teste', '{}')", c, tx);
            cmd.Parameters.AddWithValue("t", BancoFixture.TenantA);
            return await cmd.ExecuteNonQueryAsync(ct);
        });

        Assert.Equal(1, linhas);
    }

    [Theory]
    [Trait("Regra", "INV-15")]
    [InlineData("app_teste", "update eventos_historico set tipo = 'alterado'")]
    [InlineData("app_teste", "delete from eventos_historico")]
    [InlineData("app_teste", "truncate eventos_historico")]
    [InlineData("worker_teste", "update eventos_historico set tipo = 'alterado'")]
    [InlineData("worker_teste", "delete from eventos_historico")]
    [InlineData("worker_teste", "truncate eventos_historico")]
    public async Task Papeis_da_aplicacao_nao_alteram_nem_apagam_historico(string usuario, string sql)
    {
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);
        var executor = new ExecutorTenant(ds);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-15")]
    [InlineData("update eventos_historico set tipo = 'alterado'")]
    [InlineData("delete from eventos_historico")]
    [InlineData("truncate eventos_historico")]
    public async Task Nem_o_dono_das_tabelas_altera_historico(string sql)
    {
        await using var ds = NpgsqlDataSource.Create(banco.ConnectionStringDono);
        var executor = new ExecutorTenant(ds);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Contains("append-only", erro.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Regra", "INV-15")]
    [InlineData("seceletesc_app", "UPDATE")]
    [InlineData("seceletesc_app", "DELETE")]
    [InlineData("seceletesc_app", "TRUNCATE")]
    [InlineData("seceletesc_worker", "UPDATE")]
    [InlineData("seceletesc_worker", "DELETE")]
    [InlineData("seceletesc_worker", "TRUNCATE")]
    public async Task Papeis_da_aplicacao_nao_tem_permissao_de_alterar_historico(string papel, string privilegio)
    {
        // Verifica os GRANTs diretamente, para que o trigger não mascare uma permissão concedida por engano.
        var tem = await banco.ConsultarComoAdminAsync(
            $"select has_table_privilege('{papel}', 'eventos_historico', '{privilegio}')");

        Assert.False((bool)tem!);
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Quem_insere_nao_escolhe_id_nem_horario_do_evento()
    {
        var executor = new ExecutorTenant(banco.App);

        var (id, ocorridoEm) = await executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("""
                insert into eventos_historico (id, tenant_id, tipo, ocorrido_em)
                overriding system value
                values (999999, @t, 'retroativo', '2000-01-01T00:00:00Z')
                returning id, ocorrido_em
                """, c, tx);
            cmd.Parameters.AddWithValue("t", BancoFixture.TenantA);
            await using var leitor = await cmd.ExecuteReaderAsync(ct);
            await leitor.ReadAsync(ct);
            return (leitor.GetInt64(0), leitor.GetFieldValue<DateTimeOffset>(1));
        });

        Assert.NotEqual(999999, id);
        Assert.True(ocorridoEm > DateTimeOffset.UtcNow.AddMinutes(-5), "o horário do evento deve ser o do banco, não o informado");
    }
}
