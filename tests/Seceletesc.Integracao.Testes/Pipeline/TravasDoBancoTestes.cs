using Npgsql;
using Seceletesc.Infraestrutura.Banco;

namespace Seceletesc.Integracao.Testes.Pipeline;

/// <summary>
/// As garantias que o banco impõe mesmo que o código da aplicação tente contorná-las
/// (plano técnico, seções 4.4 a 4.7). Executado com os papéis reais da aplicação e do worker.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class TravasDoBancoTestes(BancoFixture banco)
{
    [Theory]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-14")]
    [InlineData("bloqueada", 0)]  // decisão de bloqueio
    [InlineData("aprovada", -1)]  // decisão aprovada sobre versão antiga do estado
    public async Task Aplicacao_nao_consegue_enfileirar_mensagem_sem_decisao_aprovada_sobre_a_versao_atual(string resultado, int deslocamentoVersao)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("inválido"))).ProcessarAsync(amb.Mensagem("oi"));
        var conversa = await amb.ConsultarAsync<Guid>("select id from conversas where tenant_id = @tenant");
        var versao = await amb.ConsultarAsync<long>("select versao_estado from conversas where tenant_id = @tenant");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.App).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            var decisao = Guid.NewGuid();
            await using var cmd = new NpgsqlCommand("""
                insert into decisoes_envio (id, tenant_id, conversa_id, versao_estado, tipo, resultado, motivo)
                values (@d, @t, @c, @v, 'resposta', @r, @m);
                insert into outbox (id, tenant_id, conversa_id, decisao_id, texto, status, expira_em)
                values (gen_random_uuid(), @t, @c, @d, 'texto que pulou o validador', 'pendente', now() + interval '5 minutes');
                """, c, tx);
            cmd.Parameters.AddWithValue("d", decisao);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("v", versao + deslocamentoVersao);
            cmd.Parameters.AddWithValue("r", resultado);
            cmd.Parameters.AddWithValue("m", resultado == "aprovada" ? DBNull.Value : "teste");
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Contains("decisão aprovada", erro.MessageText, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Regra", "INV-16")]
    [Trait("Regra", "INV-17")]
    [Trait("Regra", "CTX-012")]
    [InlineData("insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (gen_random_uuid(), @t, 'horario', 'Aprovado', 1)")]
    [InlineData("update itens_conhecimento set status = 'Aprovado'")]
    public async Task Aplicacao_nao_publica_nem_aprova_conhecimento(string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");

        var erro = await Assert.ThrowsAnyAsync<PostgresException>(() => new ExecutorTenant(amb.App).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.True(erro.SqlState is "P0001" or PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-16")]
    public async Task Aplicacao_pode_criar_sugestao_de_conhecimento_pendente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);

        var linhas = await new ExecutorTenant(amb.App).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(
                "insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (gen_random_uuid(), @t, 'horario', 'Rev', 1)", c, tx);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            return await cmd.ExecuteNonQueryAsync(ct);
        });

        Assert.Equal(1, linhas);
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public async Task Worker_nao_altera_o_estado_da_conversa()
    {
        var pode = await banco.ConsultarComoAdminAsync("select has_column_privilege('seceletesc_worker', 'conversas', 'estado', 'UPDATE')");
        var podeVersao = await banco.ConsultarComoAdminAsync("select has_column_privilege('seceletesc_worker', 'conversas', 'versao_estado', 'UPDATE')");

        Assert.False((bool)pode!);
        Assert.False((bool)podeVersao!);
    }

    [Theory]
    [Trait("Regra", "INV-15")]
    [InlineData("update decisoes_envio set resultado = 'aprovada', motivo = null")]
    [InlineData("update mensagens set texto_mascarado = 'alterado'")]
    [InlineData("update politicas_tenant set conteudo = '{}'::jsonb")]
    public async Task Decisoes_mensagens_e_politicas_sao_imutaveis_ate_para_o_dono(string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("inválido"))).ProcessarAsync(amb.Mensagem("oi"));

        await using var ds = NpgsqlDataSource.Create(banco.ConnectionStringDono);
        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(ds).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Contains("imutável", erro.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Fila_de_envio_so_e_acessivel_pelo_worker_por_funcao()
    {
        var appLeFila = await banco.ConsultarComoAdminAsync("select has_table_privilege('seceletesc_app', 'fila_envio', 'SELECT')");
        var workerLeFila = await banco.ConsultarComoAdminAsync("select has_table_privilege('seceletesc_worker', 'fila_envio', 'SELECT')");
        var appReserva = await banco.ConsultarComoAdminAsync("select has_function_privilege('seceletesc_app', 'reservar_envio(int)', 'EXECUTE')");

        Assert.False((bool)appLeFila!);
        Assert.False((bool)workerLeFila!);
        Assert.False((bool)appReserva!);
    }
}
