using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Integracao.Testes.Pipeline;

namespace Seceletesc.Integracao.Testes.Supervisao;

/// <summary>
/// Travas do banco para a mensagem humana (migração 0003). Valem mesmo que alguém contorne a API:
/// sem decisão aprovada, só a supervisão enfileira texto, com autor da própria empresa e com a conversa sob controle humano.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class SupervisaoTravasTestes(BancoFixture banco)
{
    private const string InserirMensagemHumana = """
        insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
        values (gen_random_uuid(), @t, @c, null, @a, 'texto sem validação', 'pendente', now() + interval '5 minutes')
        """;

    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData("app_teste", "P0001")]
    [InlineData("worker_teste", "42501")]
    public async Task Aplicacao_e_worker_nao_enfileiram_mensagem_sem_decisao_nem_com_autor_valido(string usuario, string erroEsperado)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await amb.AssumirAsync();
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(ds, amb.TenantId, InserirMensagemHumana, conversa, amb.Supervisor.Id));

        Assert.Equal(erroEsperado, erro.SqlState);
        Assert.Equal(0, await amb.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null"));
    }

    [Fact]
    [Trait("Regra", "INV-4")]
    public async Task Supervisao_nao_enfileira_mensagem_humana_com_a_conversa_em_modo_automatico()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirMensagemHumana, conversa, amb.Supervisor.Id));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Supervisao_nao_enfileira_mensagem_sem_autor_nem_decisao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await amb.AssumirAsync();

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirMensagemHumana, conversa, null));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Mensagem_humana_nao_aceita_autor_de_outra_empresa()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var conversa = await a.ConversaAtendidaPelaIaAsync();
        await a.AssumirAsync();

        // Revisão pós-F3 (G5): identidade comprovada com a chave do administrador de A, autor = usuário de B.
        var erro = await Assert.ThrowsAsync<PostgresException>(() => new Infraestrutura.Banco.ExecutorTenant(a.SupervisaoDs).ExecutarAutenticadoAsync(a.TenantId, a.ChaveSupervisor, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(InserirMensagemHumana, c, tx);
            cmd.Parameters.AddWithValue("t", a.TenantId);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("a", b.Supervisor.Id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        // supervisor_ativo confere tenant e identidade do autor antes da FK composta.
        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal(0L, await a.ContarAsync($"outbox where tenant_id = '{a.TenantId}' and autor_id is not null"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Mensagem_com_decisao_e_autor_ao_mesmo_tempo_e_recusada()
    {
        // Tudo válido para uma mensagem automática (decisão aprovada, versão atual, mesmo texto, modo automático),
        // exceto o autor: só a regra "decisão e autor são exclusivos" pode barrar. O trigger e o CHECK cobrem o mesmo caso.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        var decisao = Guid.NewGuid();
        await ExecutarAsync(amb.App, amb.TenantId, $"""
            insert into decisoes_envio (id, tenant_id, conversa_id, versao_estado, tipo, resultado, texto_sha256)
            values ('{decisao}', @t, @c, 1, 'resposta', 'aprovada', encode(sha256(convert_to('x', 'UTF8')), 'hex'))
            """, conversa, null);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, $"""
            insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
            values (gen_random_uuid(), @t, @c, '{decisao}', @a, 'x', 'pendente', now() + interval '5 minutes')
            """, conversa, amb.Supervisor.Id));

        Assert.Contains(erro.SqlState, new[] { "P0001", "23514" });
        Assert.Equal(0, await amb.ContarAsync($"outbox where decisao_id = '{decisao}'"));
    }

    [Theory]
    [Trait("Regra", "INV-4")]
    [InlineData("expira_em", "now() + interval '100 years'")]
    [InlineData("autor desativado", "now() + interval '5 minutes'")]
    public async Task Mensagem_humana_exige_autor_ativo_e_validade_curta(string caso, string expiraEm)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await amb.AssumirAsync();
        if (caso == "autor desativado")
        {
            await Infraestrutura.Supervisao.AdministracaoSupervisao.DesativarSupervisorAsync(banco.ConnectionStringDono, amb.Supervisor.Id);
        }

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, $"""
            insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
            values (gen_random_uuid(), @t, @c, null, @a, 'x', 'pendente', {expiraEm})
            """, conversa, amb.Supervisor.Id));

        // Usuário desativado nem comprova identidade (28000); prazo longo cai no trigger (P0001).
        Assert.Equal(caso == "autor desativado" ? "28000" : "P0001", erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-5")]
    [InlineData("update conversas set ultima_mensagem_cliente_em = now() where id = @c", "42501")]
    [InlineData("update conversas set estado = 'Automatico', versao_estado = versao_estado + 1 where id = @c", "P0001")]
    public async Task Supervisao_nao_mexe_na_janela_nem_volta_ao_automatico_sem_registrar_a_liberacao(string sql, string erroEsperado)
    {
        // Adiantar a última mensagem reabriria a janela de 24h; voltar ao automático sem liberada_em faria a IA
        // tratar mensagens recebidas durante o controle humano.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await amb.AssumirAsync();

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, sql, conversa, null));

        Assert.Equal(erroEsperado, erro.SqlState);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Todas_as_tabelas_de_negocio_terminam_as_migracoes_com_RLS_forcado()
    {
        // Tabelas sem RLS de propósito: sem dados de negócio ou consultadas antes de existir tenant, só por função.
        var semRls = new[] { "tenant_numeros_whatsapp", "usuarios_supervisao", "fila_envio", "fila_ia", "schema_migracoes", "autenticacoes_transacao" };

        var sql = "select string_agg(relname, ',' order by relname) from pg_class c join pg_namespace n on n.oid = c.relnamespace "
            + "where n.nspname = 'public' and c.relkind = 'r' and not (c.relrowsecurity and c.relforcerowsecurity) "
            + "and relname <> all (array['" + string.Join("','", semRls) + "'])";
        var faltando = await banco.ConsultarComoAdminAsync(sql);

        Assert.True(faltando is DBNull or null, $"Tabelas sem RLS forçado: {faltando}");
    }

    [Theory]
    [Trait("Regra", "INV-10")]
    [InlineData("supervisao_teste", "select count(*) from usuarios_supervisao")]
    [InlineData("app_teste", "select * from autenticar_supervisor('00')")]
    [InlineData("worker_teste", "select * from autenticar_supervisor('00')")]
    public async Task Usuarios_da_supervisao_so_sao_consultados_pela_funcao_de_autenticacao(string usuario, string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(ds, amb.TenantId, sql, Guid.Empty, null));

        Assert.Equal("42501", erro.SqlState);
    }

    private static Task<int> ExecutarAsync(NpgsqlDataSource ds, Guid tenant, string sql, Guid conversa, Guid? autor) =>
        AmbientePipeline.ExecutarComPapelAsync(ds, tenant, autor, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("a", (object?)autor ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct);
        });
}
