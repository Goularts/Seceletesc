using Microsoft.EntityFrameworkCore;
using Npgsql;
using Seceletesc.Dominio.Clientes;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Persistencia;

namespace Seceletesc.Integracao.Testes;

/// <summary>Invariante 10: dados de empresas (tenants) permanecem isolados. Plano técnico, seção 4.5.</summary>
[Collection(ColecaoBanco.Nome)]
public sealed class IsolamentoTenantTestes(BancoFixture banco)
{
    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Sem_tenant_definido_a_consulta_falha_em_vez_de_devolver_tudo()
    {
        await using var ds = banco.CriarDataSource("app_teste", maxPool: 1);
        await using var conexao = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from clientes", conexao);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedObject, erro.SqlState); // app.tenant_id não definido
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Nem_o_dono_das_tabelas_le_dados_sem_tenant()
    {
        // Prova o FORCE ROW LEVEL SECURITY: sem ele, o dono veria todas as empresas.
        await using var conexao = new NpgsqlConnection(banco.ConnectionStringDono);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from clientes", conexao);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedObject, erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Dono_das_tabelas_com_tenant_A_so_ve_dados_de_A()
    {
        await using var ds = NpgsqlDataSource.Create(banco.ConnectionStringDono);
        var executor = new ExecutorTenant(ds);

        var clientes = await executor.ExecutarAsync(BancoFixture.TenantA, (c, tx, ct) => ListarAsync(c, tx, "select tenant_id from clientes", ct));

        Assert.NotEmpty(clientes);
        Assert.All(clientes, t => Assert.Equal(BancoFixture.TenantA, t));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Worker_com_tenant_A_so_le_dados_de_A()
    {
        var executor = new ExecutorTenant(banco.Worker);

        var (clientes, eventos) = await executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
            (await ListarAsync(c, tx, "select tenant_id from clientes", ct), await ListarAsync(c, tx, "select tenant_id from eventos_historico", ct)));

        Assert.NotEmpty(clientes);
        Assert.All(clientes, t => Assert.Equal(BancoFixture.TenantA, t));
        Assert.All(eventos, t => Assert.Equal(BancoFixture.TenantA, t));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Com_tenant_A_so_ve_dados_de_A()
    {
        var executor = new ExecutorTenant(banco.App);

        var (clientes, eventos, tenants) = await executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            var cl = await ListarAsync(c, tx, "select tenant_id from clientes", ct);
            var ev = await ListarAsync(c, tx, "select tenant_id from eventos_historico", ct);
            var tn = await ListarAsync(c, tx, "select id from tenants", ct);
            return (cl, ev, tn);
        });

        Assert.All(clientes, t => Assert.Equal(BancoFixture.TenantA, t));
        Assert.All(eventos, t => Assert.Equal(BancoFixture.TenantA, t));
        Assert.Equal([BancoFixture.TenantA], tenants);
        Assert.NotEmpty(clientes);
        Assert.NotEmpty(eventos);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Inserir_dado_com_tenant_de_outra_empresa_e_recusado()
    {
        var executor = new ExecutorTenant(banco.App);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(
                "insert into clientes (id, tenant_id, wa_id) values (@id, @t, '5511900009999')", c, tx);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("t", BancoFixture.TenantB);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Conexao_reutilizada_do_pool_nao_herda_o_tenant_da_transacao_anterior()
    {
        // Pool com uma única conexão física e sem limpeza do driver: a segunda operação reutiliza
        // exatamente a mesma sessão do PostgreSQL.
        await using var ds = banco.CriarDataSource("app_teste", maxPool: 1, semResetAoDevolver: true);
        var executor = new ExecutorTenant(ds);

        await executor.ExecutarAsync(BancoFixture.TenantA, (c, tx, ct) => ListarAsync(c, tx, "select tenant_id from clientes", ct));

        await using var conexao = await ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("select count(*) from clientes", conexao);
        var erro = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, erro.SqlState); // app.tenant_id voltou a '' ao fim da transação
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Transacoes_seguidas_na_mesma_conexao_veem_cada_uma_so_o_seu_tenant()
    {
        await using var ds = banco.CriarDataSource("app_teste", maxPool: 1, semResetAoDevolver: true);
        var executor = new ExecutorTenant(ds);

        var deA = await executor.ExecutarAsync(BancoFixture.TenantA, (c, tx, ct) => ListarAsync(c, tx, "select tenant_id from clientes", ct));
        var deB = await executor.ExecutarAsync(BancoFixture.TenantB, (c, tx, ct) => ListarAsync(c, tx, "select tenant_id from clientes", ct));

        Assert.All(deA, t => Assert.Equal(BancoFixture.TenantA, t));
        Assert.All(deB, t => Assert.Equal(BancoFixture.TenantB, t));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Resolucao_do_tenant_pelo_numero_funciona_sem_tenant_e_o_mapeamento_nao_e_legivel_diretamente()
    {
        await using var conexao = await banco.App.OpenConnectionAsync();

        await using (var resolver = new NpgsqlCommand("select resolver_tenant_por_phone_number_id('pn-b')", conexao))
        {
            Assert.Equal(BancoFixture.TenantB, await resolver.ExecuteScalarAsync());
        }

        await using var direto = new NpgsqlCommand("select * from tenant_numeros_whatsapp", conexao);
        var erro = await Assert.ThrowsAsync<PostgresException>(() => direto.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task EF_Core_na_unidade_de_trabalho_so_ve_e_grava_no_proprio_tenant()
    {
        var novo = new Cliente(Guid.NewGuid(), BancoFixture.TenantB, "5511900005555");
        await using (var uow = await UnidadeDeTrabalhoTenant.IniciarAsync(banco.App, BancoFixture.TenantB))
        {
            uow.Contexto.Clientes.Add(novo);
            await uow.ConfirmarAsync();
        }

        await using var leitura = await UnidadeDeTrabalhoTenant.IniciarAsync(banco.App, BancoFixture.TenantA);
        // Mesmo ignorando o filtro global do EF, o RLS do banco impede ver o tenant B.
        var semFiltro = await leitura.Contexto.Clientes.IgnoreQueryFilters().Select(c => c.TenantId).ToListAsync();

        Assert.All(semFiltro, t => Assert.Equal(BancoFixture.TenantA, t));
        Assert.DoesNotContain(semFiltro, t => t == BancoFixture.TenantB);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Nenhum_papel_da_aplicacao_e_superusuario_tem_bypassrls_ou_e_dono_das_tabelas()
    {
        var privilegiados = await banco.ConsultarComoAdminAsync("""
            select count(*) from pg_roles
            where rolname in ('seceletesc_dono', 'seceletesc_app', 'seceletesc_worker', 'app_teste', 'worker_teste')
              and (rolsuper or rolbypassrls)
            """);
        var donosErrados = await banco.ConsultarComoAdminAsync("""
            select count(*) from pg_tables
            where schemaname = 'public'
              and tablename in ('tenants', 'tenant_numeros_whatsapp', 'clientes', 'eventos_historico')
              and tableowner <> 'seceletesc_dono'
            """);

        Assert.Equal(0L, privilegiados);
        Assert.Equal(0L, donosErrados);
    }

    [Theory]
    [Trait("Regra", "INV-10")]
    [InlineData("seceletesc_app", "has_table_privilege('seceletesc_app', 'tenant_numeros_whatsapp', 'SELECT,INSERT,UPDATE,DELETE')")]
    [InlineData("seceletesc_worker", "has_table_privilege('seceletesc_worker', 'tenant_numeros_whatsapp', 'SELECT,INSERT,UPDATE,DELETE')")]
    [InlineData("seceletesc_worker", "has_function_privilege('seceletesc_worker', 'resolver_tenant_por_phone_number_id(text)', 'EXECUTE')")]
    [InlineData("public", "has_function_privilege('public', 'resolver_tenant_por_phone_number_id(text)', 'EXECUTE')")]
    [InlineData("seceletesc_app", "has_table_privilege('seceletesc_app', 'tenants', 'INSERT,UPDATE,DELETE')")]
    public async Task Papeis_nao_tem_acesso_alem_do_necessario(string papel, string verificacao)
    {
        var temAcesso = await banco.ConsultarComoAdminAsync("select " + verificacao);

        Assert.False((bool)temAcesso!, $"{papel} não deveria ter este acesso: {verificacao}");
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Papel_worker_nao_grava_clientes()
    {
        var executor = new ExecutorTenant(banco.Worker);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => executor.ExecutarAsync(BancoFixture.TenantA, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(
                "insert into clientes (id, tenant_id, wa_id) values (@id, @t, '5511900007777')", c, tx);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("t", BancoFixture.TenantA);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    private static async Task<List<Guid>> ListarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await using var leitor = await cmd.ExecuteReaderAsync(ct);
        var ids = new List<Guid>();
        while (await leitor.ReadAsync(ct))
        {
            ids.Add(leitor.GetGuid(0));
        }

        return ids;
    }
}
