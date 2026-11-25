using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Integracao.Testes.Pipeline;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.Conhecimento;

/// <summary>
/// Travas do banco da F3 (migração 0004). Valem mesmo que alguém contorne a API: o papel de quem aprova, revoga,
/// publica a política ou encerra a pendência é conferido a partir do usuário gravado na própria linha.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class ConhecimentoTravasTestes(BancoFixture banco)
{
    private const string InserirAprovada = """
        insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em)
        values (@i, 2, @t, 'texto', @cl, @e, @er, @v, 'contornando a API', @u, now())
        """;

    [Fact]
    [Trait("Regra", "INV-16")]
    [Trait("Regra", "CTX-012")]
    public async Task Aplicacao_nao_grava_versao_aprovada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecutarAsync(amb.App, amb.TenantId, InserirAprovada, item.ItemId, amb.Supervisor.Id, ["Publico"]));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-17")]
    [Trait("Regra", "INV-18")]
    [InlineData(PapelSupervisao.Atendente, "Publico", "Empresa", false)]
    [InlineData(PapelSupervisao.Supervisor, "Negociacao", "Cliente", true)]
    [InlineData(PapelSupervisao.Supervisor, "Restrito", "Empresa", true)]
    [InlineData(PapelSupervisao.Administrador, "Negociacao", "Empresa", true)]   // NEG para a empresa toda
    [InlineData(PapelSupervisao.Administrador, "Excecao", "Cliente", false)]      // EXC sem validade
    public async Task Banco_recusa_aprovacao_por_papel_insuficiente_ou_condicao_comercial_sem_cliente_e_validade(
        PapelSupervisao papel, string classificacao, string escopo, bool comValidade)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("precos", "sugestão", status: "Rev");
        var (usuario, _) = await amb.CriarUsuarioAsync(papel);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirAprovada, item.ItemId, usuario.Id,
            [classificacao], escopo, escopo == "Empresa" ? null : Guid.NewGuid(), comValidade ? DateTimeOffset.UtcNow.AddDays(30) : null));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-17")]
    public async Task Banco_recusa_aprovacao_sem_classificacao_e_por_usuario_desativado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");
        var (supervisor, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);

        var semClassificacao = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirAprovada, item.ItemId, supervisor.Id, []));
        await AdministracaoSupervisao.DesativarSupervisorAsync(banco.ConnectionStringDono, supervisor.Id);
        var desativado = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirAprovada, item.ItemId, supervisor.Id, ["Publico"]));

        Assert.Equal("P0001", semClassificacao.SqlState);
        Assert.Equal("28000", desativado.SqlState); // desativado nem comprova identidade na transação
    }

    [Theory]
    [Trait("Regra", "INV-18")]
    [Trait("Regra", "CTX-012")]
    [InlineData("insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (gen_random_uuid(), @t, 'horario', 'Aprovado', 1)")]
    [InlineData("update itens_conhecimento set status = 'Aprovado', alterado_por = @u where id = @i")] // aponta para a proposta
    public async Task Nem_a_supervisao_publica_sem_versao_aprovada(string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, sql, item.ItemId, amb.Supervisor.Id, []));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item.ItemId}'"));
    }

    [Theory]
    [Trait("Regra", "INV-17")]
    [InlineData("Aprovado", PapelSupervisao.Supervisor, "Revogado")]   // revogar exige administrador
    [InlineData("Rev", PapelSupervisao.Atendente, "Rejeitado")]        // rejeitar exige supervisor
    [InlineData("Revogado", PapelSupervisao.Administrador, "Aprovado")] // revogado é final
    [InlineData("Rejeitado", PapelSupervisao.Administrador, "Aprovado")] // rejeitado é final
    public async Task Banco_confere_papel_e_ciclo_de_vida_nas_mudancas_de_status(string inicial, PapelSupervisao papel, string novo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "texto", status: inicial);
        var (usuario, _) = await amb.CriarUsuarioAsync(papel);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId,
            $"update itens_conhecimento set status = '{novo}', alterado_por = @u where id = @i", item.ItemId, usuario.Id, []));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal(inicial, await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item.ItemId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Data_da_aprovacao_e_carimbada_pelo_banco()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");

        await ExecutarAsync(amb.SupervisaoDs, amb.TenantId, InserirAprovada.Replace("@u, now())", "@u, '2000-01-01')", StringComparison.Ordinal),
            item.ItemId, amb.Supervisor.Id, ["Publico"]);

        Assert.Equal(0L, await amb.ContarAsync($"versoes_conhecimento where item_id = '{item.ItemId}' and aprovada_em < now() - interval '1 day'"));
    }

    [Theory]
    [Trait("Regra", "INV-11")]
    [InlineData(PapelSupervisao.Supervisor)]
    [InlineData(PapelSupervisao.Atendente)]
    public async Task Banco_so_aceita_politica_publicada_por_administrador(PapelSupervisao papel)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (usuario, _) = await amb.CriarUsuarioAsync(papel);
        const string sql = "insert into politicas_tenant (tenant_id, versao, conteudo, publicada_por) values (@t, 2, '{\"assuntosAutonomos\":[\"horario\"],\"classificacoesPermitidas\":[\"Publico\",\"Negociacao\"]}'::jsonb, @u)";

        var porPapel = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, sql, Guid.Empty, usuario.Id, []));
        var semAutor = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.SupervisaoDs, amb.TenantId, sql, Guid.Empty, null, []));
        var porAdmin = await ExecutarAsync(amb.SupervisaoDs, amb.TenantId, sql, Guid.Empty, amb.Supervisor.Id, []);

        Assert.Equal("P0001", porPapel.SqlState);
        Assert.Equal("P0001", semAutor.SqlState);
        Assert.Equal(1, porAdmin);
    }

    [Theory]
    [Trait("Regra", "INV-24")]
    [InlineData("app_teste", "update conversas set marcada_retomada_humana = false where id = @i")]
    [InlineData("worker_teste", "update conversas set marcada_retomada_humana = false where id = @i")]
    [InlineData("supervisao_teste", "update conversas set marcada_retomada_humana = false, retomada_encerrada_por = @u, retomada_motivo = 'ok' where id = @i")]
    [InlineData("supervisao_teste", "update conversas set marcada_retomada_humana = false, retomada_encerrada_por = @adm, retomada_motivo = ' ' where id = @i")]
    [InlineData("supervisao_teste", "update conversas set retomada_encerrada_por = @adm, retomada_motivo = 'forjado' where id = @i")]
    public async Task Pendencia_de_retomada_so_e_encerrada_por_supervisor_com_motivo(string usuarioBanco, string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await banco.ConsultarComoAdminAsync($"update conversas set marcada_retomada_humana = true where id = '{conversa}'");
        var (atendente, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        await using var ds = banco.CriarDataSource(usuarioBanco, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(ds, amb.TenantId,
            sql.Replace("@adm", $"'{amb.Supervisor.Id}'", StringComparison.Ordinal), conversa, atendente.Id, []));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal(1, await amb.ContarAsync("conversas where tenant_id = @tenant and marcada_retomada_humana"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Worker_nao_troca_o_texto_de_mensagem_que_ainda_vai_ser_enviada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", SupervisaoApiTestes.Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, SupervisaoApiTestes.Horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));
        var outbox = await amb.ConsultarAsync<Guid>("select id from outbox where tenant_id = @tenant");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ExecutarAsync(amb.WorkerDs, amb.TenantId,
            "update outbox set texto = 'texto trocado', texto_mascarado = true where id = @i", outbox, null, []));

        Assert.Equal("P0001", erro.SqlState);
    }

    private static Task<int> ExecutarAsync(
        NpgsqlDataSource ds, Guid tenant, string sql, Guid item, Guid? usuario, string[] classificacoes,
        string escopo = "Empresa", Guid? escopoRef = null, DateTimeOffset? validoAte = null) =>
        AmbientePipeline.ExecutarComPapelAsync(ds, tenant, usuario, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("i", item);
            cmd.Parameters.AddWithValue("u", (object?)usuario ?? DBNull.Value);
            cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = classificacoes });
            cmd.Parameters.AddWithValue("e", escopo);
            cmd.Parameters.AddWithValue("er", (object?)escopoRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("v", (object?)validoAte ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct);
        });
}
