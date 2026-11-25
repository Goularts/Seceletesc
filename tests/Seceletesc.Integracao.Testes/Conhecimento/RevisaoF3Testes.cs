using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Integracao.Testes.Pipeline;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.Conhecimento;

/// <summary>
/// Achados das revisões da F3 (docs/desenvolvimento/revisoes/2026-11-25-f3.md), cada um com o teste que o fecha.
/// G = guardião de regras; S = segurança.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class RevisaoF3Testes(BancoFixture banco)
{
    private const string Horario = "Atendemos de segunda a sexta, das 9h às 18h.";
    private static readonly DateTimeOffset Futuro = AmbientePipeline.Inicio.AddDays(30);

    [Fact]
    [Trait("Regra", "INV-18")]
    [Trait("Regra", "INV-17")]
    public async Task G2_supervisor_nao_rebaixa_conhecimento_comercial_aprovado_pelo_administrador()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var cliente = await ClienteAsync(amb);
        var item = (await amb.Conhecimento.SugerirAsync(amb.Supervisor, "precos", "Para este cliente, 10% de desconto.", null, null)).Id!.Value;
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Conhecimento.AprovarAsync(amb.Supervisor, item, 1, new PermissoesConhecimento(["Negociacao"], "Cliente", cliente, Futuro))).Situacao);
        var (supervisor, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        // Desde a revisão pós-F3 (S6), o supervisor nem enxerga conhecimento NEG aprovado: editar e aprovar "não acham" o item.
        var editar = await amb.Conhecimento.EditarAsync(supervisor, item, 2, "Desconto de 10% para todos.", null);

        var pelaApi = await amb.Conhecimento.AprovarAsync(supervisor, item, 2, new PermissoesConhecimento(["Publico"], "Empresa", null, null));
        var direto = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId, """
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, origem, aprovada_por, aprovada_em)
            values (@i, 3, @t, 'Desconto de 10% para todos.', '{Publico}', 'Empresa', 'contornando', @u, now())
            """, item, supervisor.Id));

        Assert.Equal(SituacaoOperacao.NaoEncontrada, editar.Situacao);
        Assert.Equal(SituacaoOperacao.NaoEncontrada, pelaApi.Situacao);
        Assert.Equal("P0001", direto.SqlState);
        Assert.Equal(2, await amb.ConsultarAsync<int>($"select versao_vigente from itens_conhecimento where id = '{item}'"));
    }

    [Fact]
    [Trait("Regra", "INV-17")]
    [Trait("Regra", "INV-19")]
    public async Task G3_edicao_pendente_de_conhecimento_aprovado_pode_ser_rejeitada_e_nunca_e_aprovada_depois()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var item = await amb.ConhecimentoAsync("horario", Horario); // v2 aprovada
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item.ItemId}/editar", new { versaoBase = 2, conteudo = "Atendemos 24 horas." });

        var rejeitar = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item.ItemId}/rejeitar", new { motivo = "Não atendemos 24 horas." });
        var aprovarRejeitada = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item.ItemId}/aprovar",
            new { versaoProposta = 3, permissoes = new { classificacoes = new[] { "Publico" }, escopoTipo = "Empresa" } });
        var resumo = (await (await admin.GetAsync(new Uri($"/supervisao/conhecimento/{item.ItemId}", UriKind.Relative))).JsonAsync()).GetProperty("item");
        var (atendente, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item.ItemId}/editar", new { versaoBase = 3, conteudo = "Abrimos às 7h." });
        var porAtendente = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId,
            "insert into rejeicoes_versao (item_id, versao, tenant_id, rejeitada_por, motivo) values (@i, 4, @t, @u, 'não')", item.ItemId, atendente.Id));

        Assert.Equal(HttpStatusCode.OK, rejeitar.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, aprovarRejeitada.StatusCode);
        Assert.Equal("Aprovado", resumo.GetProperty("status").GetString());
        Assert.Equal(2, resumo.GetProperty("versaoVigente").GetInt32());
        Assert.False(resumo.GetProperty("temEdicaoPendente").GetBoolean());
        Assert.Equal("P0001", porAtendente.SqlState);
        Assert.Equal(1, await amb.ContarAsync("eventos_historico where tenant_id = @tenant and tipo = 'edicao_rejeitada'"));
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public async Task G4_orientacao_e_sugestao_ligadas_a_conversa_nao_guardam_nem_dado_comum()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco, p => p.Emails.Add("financeiro@empresa.com.br"));
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);

        var orientacao = await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar", new { texto = "O e-mail dele é maria@cliente.com.br", assunto = "cadastro" });
        var ligada = await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "cadastro", conteudo = "Contato: maria@cliente.com.br", conversaOrigem = conversa });
        var avulsa = await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "financeiro", conteudo = "Segunda via: financeiro@empresa.com.br" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, orientacao.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ligada.StatusCode);
        Assert.Equal(HttpStatusCode.Created, avulsa.StatusCode); // e-mail institucional da política: precisa estar na fonte para sair
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from versoes_conhecimento where tenant_id = '{amb.TenantId}' and conteudo like '%maria@%'"));
    }

    [Fact]
    [Trait("Regra", "ADV-16")]
    [Trait("Regra", "INV-11")]
    public async Task G6_negociacao_do_proprio_cliente_nao_chega_ao_modelo_se_a_politica_nao_libera()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var cliente = await ClienteAsync(amb);
        var neg = await amb.ConhecimentoAsync("precos", "Desconto negociado: 15%.", [Classificacao.Negociacao], TipoEscopo.Cliente, cliente, Futuro);

        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        var motor = new MotorFalso(_ => Task.FromResult("{}"));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Tem desconto para mim?"));

        Assert.DoesNotContain(motor.Contextos.Single().Conhecimentos, k => k.ItemId == neg.ItemId);
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    [Trait("Regra", "CTX-007")]
    public async Task S1_supervisor_nao_amplia_o_escopo_de_conhecimento_vindo_de_um_atendimento()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        var orientacao = (await amb.Supervisao.OrientarAsync(amb.Supervisor, conversa, "Este cliente prefere ser atendido à tarde.", "agenda")).Id!.Value;
        var (supervisor, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);

        var paraEmpresa = await amb.Conhecimento.AprovarAsync(supervisor, orientacao, 1, new PermissoesConhecimento(["Interno"], "Empresa", null, Futuro));
        var direto = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId, """
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, valido_ate, origem, aprovada_por, aprovada_em)
            values (@i, 2, @t, 'Este cliente prefere ser atendido à tarde.', '{Interno}', 'Empresa', now() + interval '30 days', 'contornando', @u, now())
            """, orientacao, supervisor.Id));
        var mesmoAtendimento = await amb.Conhecimento.AprovarAsync(supervisor, orientacao, 1, new PermissoesConhecimento(["Interno"], "Atendimento", conversa, Futuro));
        var sugestaoAmpla = await amb.Conhecimento.SugerirAsync(supervisor, "agenda", "Atendimento à tarde.", new PermissoesConhecimento(null, "Empresa", null, null), conversa);

        Assert.Equal(SituacaoOperacao.Proibida, paraEmpresa.Situacao);
        Assert.Equal("P0001", direto.SqlState);
        Assert.Equal(SituacaoOperacao.Ok, mesmoAtendimento.Situacao);
        Assert.Equal(SituacaoOperacao.Invalida, sugestaoAmpla.Situacao);
    }

    [Theory]
    [Trait("Regra", "INV-5")]
    [InlineData(PapelSupervisao.Atendente)]
    [InlineData(null)]
    public async Task S3_banco_so_aceita_liberacao_registrada_por_supervisor(PapelSupervisao? papel)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        await amb.AssumirAsync();
        Guid? quem = papel is { } p ? (await amb.CriarUsuarioAsync(p)).Usuario.Id : null;

        var erro = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId,
            "update conversas set estado = 'Automatico', versao_estado = versao_estado + 1, liberada_em = now(), liberada_por = @u where id = @i", conversa, quem));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "ADV-19")]
    public async Task S4_envio_espera_a_revogacao_em_andamento_e_nao_usa_a_fonte_revogada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));

        // Revogação em andamento: trava do item pega e status já trocado, sem confirmar ainda.
        await using var conexao = await amb.SupervisaoDs.OpenConnectionAsync();
        await using var tx = await conexao.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("""
            select set_config('app.tenant_id', @t, true);
            select * from autenticar_transacao(@k);
            select pg_advisory_xact_lock(hashtextextended(@i::text, 0));
            update itens_conhecimento set status = 'Revogado', alterado_por = @u where id = @i;
            """, conexao, tx))
        {
            cmd.Parameters.AddWithValue("t", amb.TenantId.ToString());
            cmd.Parameters.AddWithValue("i", item.ItemId);
            cmd.Parameters.AddWithValue("u", amb.Supervisor.Id);
            cmd.Parameters.AddWithValue("k", amb.ChaveSupervisor);
            await cmd.ExecuteNonQueryAsync();
        }

        var envio = amb.Worker().ProcessarProximoAsync();
        var esperou = await Task.WhenAny(envio, Task.Delay(TimeSpan.FromSeconds(1))) != envio;
        await tx.CommitAsync();
        var ciclo = await envio;

        Assert.True(esperou, "o worker não esperou a revogação em andamento");
        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Empty(amb.Gateway.Enviadas);
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public async Task S6_versao_vigente_nao_volta_para_uma_aprovacao_antiga()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", Horario); // v2 aprovada
        await amb.Conhecimento.EditarAsync(amb.Supervisor, item.ItemId, 2, "Atendemos das 8h às 20h.", null);
        await amb.Conhecimento.AprovarAsync(amb.Supervisor, item.ItemId, 3, new PermissoesConhecimento(["Publico"], "Empresa", null, null)); // v4
        var (supervisor, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId,
            "update itens_conhecimento set versao_vigente = 2, alterado_por = @u where id = @i", item.ItemId, supervisor.Id));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal(4, await amb.ConsultarAsync<int>($"select versao_vigente from itens_conhecimento where id = '{item.ItemId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    public async Task S7_pendencia_reaberta_nao_herda_o_encerramento_anterior()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        await banco.ConsultarComoAdminAsync($"update conversas set marcada_retomada_humana = true where id = '{conversa}'");
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Supervisao.EncerrarRetomadaAsync(amb.Supervisor, conversa, "Atendido pelo celular.")).Situacao);

        await banco.ConsultarComoAdminAsync($"update conversas set marcada_retomada_humana = true where id = '{conversa}'"); // reaberta (como o worker faz)
        var limpo = await amb.ContarAsync("conversas where tenant_id = @tenant and retomada_encerrada_por is null and retomada_motivo is null and retomada_encerrada_em is null");
        var semResponsavel = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId,
            "update conversas set marcada_retomada_humana = false where id = @i", conversa, null));

        Assert.Equal(1, limpo);
        Assert.Equal("P0001", semResponsavel.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task S8_hash_do_texto_original_fica_na_fila_depois_do_expurgo()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        await amb.AssumirAsync();
        const string texto = "Envie o comprovante para financeiro@empresa.com.br.";
        await amb.Supervisao.ResponderAsync(amb.Supervisor, conversa, texto);
        await amb.Worker().ProcessarProximoAsync();

        var esperado = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(texto)));

        Assert.Equal(esperado, await amb.ConsultarAsync<string>("select texto_sha256 from outbox where tenant_id = @tenant and autor_id is not null"));
        Assert.Equal(1, await amb.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null and texto_mascarado and texto not like '%financeiro@%'"));
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task S11_troca_de_papel_e_desativacao_ficam_no_historico_da_empresa()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (usuario, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        await AdministracaoSupervisao.AlterarPapelAsync(banco.ConnectionStringDono, usuario.Id, PapelSupervisao.Supervisor);
        await AdministracaoSupervisao.DesativarSupervisorAsync(banco.ConnectionStringDono, usuario.Id);

        Assert.Equal("Atendente->Supervisor", await amb.ConsultarAsync<string>(
            $"select dados->>'de' || '->' || (dados->>'para') from eventos_historico where tenant_id = @tenant and tipo = 'papel_alterado' and dados->>'usuario' = '{usuario.Id}'"));
        Assert.Equal(1, await amb.ContarAsync($"eventos_historico where tenant_id = @tenant and tipo = 'usuario_desativado' and dados->>'usuario' = '{usuario.Id}'"));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task S12_funcao_de_papel_nao_responde_sobre_usuario_de_outra_empresa()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);

        var papel = await new ExecutorTenant(a.SupervisaoDs).ExecutarAsync(a.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("select papel_supervisor(@t, @u)", c, tx);
            cmd.Parameters.AddWithValue("t", b.TenantId);
            cmd.Parameters.AddWithValue("u", b.Supervisor.Id);
            return await cmd.ExecuteScalarAsync(ct);
        });

        Assert.True(papel is DBNull or null);
    }

    [Theory]
    [Trait("Regra", "INV-19")]
    [InlineData("'{Publico}'", "'Proposta'", "gen_random_uuid()", "null")]
    [InlineData("'{Interno}'", "'Empresa'", "null", "null")]
    public async Task S13_banco_recusa_aprovar_proposta_ou_nao_pub_sem_validade(string classificacoes, string escopo, string referencia, string validade)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => Executar(amb.SupervisaoDs, amb.TenantId, $"""
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em)
            values (@i, 2, @t, 'texto', {classificacoes}, {escopo}, {referencia}, {validade}, 'contornando', @u, now())
            """, item.ItemId, amb.Supervisor.Id));

        Assert.Equal("P0001", erro.SqlState);
    }

    private static async Task<Guid> ClienteAsync(AmbientePipeline amb)
    {
        if (await amb.ConsultarAsync<Guid?>("select id from clientes where tenant_id = @tenant") is { } id)
        {
            return id;
        }

        await amb.ConversaAtendidaPelaIaAsync(Horario);
        return (await amb.ConsultarAsync<Guid?>("select id from clientes where tenant_id = @tenant"))!.Value;
    }

    private static Task<int> Executar(NpgsqlDataSource ds, Guid tenant, string sql, Guid id, Guid? usuario) =>
        AmbientePipeline.ExecutarComPapelAsync(ds, tenant, usuario, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", tenant);
            cmd.Parameters.AddWithValue("i", id);
            cmd.Parameters.AddWithValue("u", (object?)usuario ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct);
        });
}
