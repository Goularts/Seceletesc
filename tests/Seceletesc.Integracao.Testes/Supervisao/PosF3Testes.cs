using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Seceletesc.Api.Supervisao;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Integracao.Testes.Pipeline;

namespace Seceletesc.Integracao.Testes.Supervisao;

/// <summary>
/// Direcionamento pós-F3 do Victor (2026-11-25; decisões 24 a 30): identidade comprovada no banco, atribuição de
/// conversas, visibilidade por papel, políticas fundamentais, e-mail institucional, validade máxima, HTTPS e
/// tratamento das recusas do banco na API.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class PosF3Testes(BancoFixture banco)
{
    private const string Horario = SupervisaoApiTestes.Horario;

    // ------------------------------------------------------------------ identidade comprovada no banco (INV-26)

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Escrita_da_supervisao_sem_identidade_comprovada_e_recusada_pelo_banco()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);

        // Papel de supervisão, tenant certo, mas sem apresentar a chave do usuário na transação.
        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("update conversas set estado = 'Humano', versao_estado = versao_estado + 1 where id = @c", c, tx);
            cmd.Parameters.AddWithValue("c", conversa);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("42501", erro.SqlState);
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-17")]
    public async Task Atendente_comprovado_nao_grava_aprovacao_em_nome_do_administrador()
    {
        // Antes (0004) o banco confiava no id gravado: uma API comprometida podia aprovar "como" o administrador.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "sugestão", status: "Rev");
        var (atendente, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAutenticadoAsync(amb.TenantId, chave, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("""
                insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em)
                values (@i, 2, @t, 'texto', '{Publico}', 'Empresa', null, null, 'forjado', @adm, now())
                """, c, tx);
            cmd.Parameters.AddWithValue("i", item.ItemId);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            cmd.Parameters.AddWithValue("adm", amb.Supervisor.Id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
        Assert.NotEqual(atendente.Id, amb.Supervisor.Id);
    }

    [Theory]
    [Trait("Regra", "INV-26")]
    [InlineData("update conversas set estado = 'Automatico', versao_estado = versao_estado + 1, liberada_em = now(), liberada_por = @adm where id = @c")]
    [InlineData("update conversas set atribuida_a = @adm where id = @c")]
    public async Task Supervisor_comprovado_nao_registra_autoria_de_outro_usuario(string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        var (supervisor, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Supervisao.AssumirAsync(supervisor, conversa)).Situacao);
        var (atendente, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        // Liberar em nome do administrador, ou atendente "pegando" para outro: a autoria não é a do usuário comprovado.
        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAutenticadoAsync(
            amb.TenantId, sql.Contains("atribuida_a", StringComparison.Ordinal) ? chaveAtendente : chave, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("adm", amb.Supervisor.Id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
        Assert.NotEqual(atendente.Id, supervisor.Id);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-4")]
    public async Task Banco_recusa_resposta_humana_em_nome_de_outro_usuario()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await amb.AssumirAsync();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);

        // Supervisor comprovado grava a mensagem como se fosse do administrador (ativo, da mesma empresa).
        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAutenticadoAsync(amb.TenantId, chave, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("""
                insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
                values (gen_random_uuid(), @t, @c, null, @a, 'em nome de outro', 'pendente', now() + interval '5 minutes')
                """, c, tx);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("a", amb.Supervisor.Id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-10")]
    [InlineData("app_teste", "select * from autenticar_transacao('x')")]
    [InlineData("worker_teste", "select * from autenticar_transacao('x')")]
    [InlineData("supervisao_teste", "select count(*) from autenticacoes_transacao")]
    [InlineData("supervisao_teste", "insert into autenticacoes_transacao (xid, usuario_id, tenant_id) values (pg_current_xact_id(), gen_random_uuid(), gen_random_uuid())")]
    public async Task Identidade_da_transacao_so_e_gravada_pela_funcao_de_autenticacao(string usuario, string sql)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(ds).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("42501", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-10")]
    public async Task Chave_de_outra_empresa_nao_comprova_identidade()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            new ExecutorTenant(a.SupervisaoDs).ExecutarAutenticadoAsync(a.TenantId, b.ChaveSupervisor, (_, _, _) => Task.FromResult(0)));

        Assert.Equal("28000", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Usuario_sem_credencial_nao_opera_e_a_chave_nao_aparece_no_texto_do_usuario()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        var semCredencial = amb.Supervisor with { Credencial = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => amb.Supervisao.AssumirAsync(semCredencial, conversa));
        Assert.DoesNotContain(amb.ChaveSupervisor, amb.Supervisor.ToString(), StringComparison.Ordinal);
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
    }

    // ------------------------------------------------------------------ atribuição de conversas (decisão 27)

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-10")]
    public async Task Atendente_ve_so_as_conversas_atribuidas_a_ele_e_a_fila()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var naFila = await amb.ConversaNaFilaAsync(Horario);
        var comIa = await ConversaDeOutroClienteComIaAsync(amb);
        await using var api = amb.Api();
        var (_, chaveA) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (_, chaveB) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var a = api.Cliente(chaveA);
        var b = api.Cliente(chaveB);

        var antes = await IdsAsync(a);
        var detalheFila = await a.GetAsync(new Uri($"/supervisao/conversas/{naFila}", UriKind.Relative));
        var pegar = await a.PostAsync(new Uri($"/supervisao/conversas/{naFila}/assumir", UriKind.Relative), null);
        var depoisA = await IdsAsync(a);
        var depoisB = await IdsAsync(b);
        var detalheA = await a.GetAsync(new Uri($"/supervisao/conversas/{naFila}", UriKind.Relative));
        var detalheB = await b.GetAsync(new Uri($"/supervisao/conversas/{naFila}", UriKind.Relative));
        var responderB = await b.PostAsJsonAsync($"/supervisao/conversas/{naFila}/responder", new { texto = "Olá" });
        var assumirB = await b.PostAsync(new Uri($"/supervisao/conversas/{naFila}/assumir", UriKind.Relative), null);
        var assumirIa = await b.PostAsync(new Uri($"/supervisao/conversas/{comIa}/assumir", UriKind.Relative), null);

        Assert.Equal([naFila], antes);                                  // a fila, sem a conversa que está com a IA
        Assert.Equal(HttpStatusCode.NotFound, detalheFila.StatusCode);  // da fila, só o resumo: pega para ler
        Assert.Equal(HttpStatusCode.OK, pegar.StatusCode);
        Assert.Equal([naFila], depoisA);
        Assert.Empty(depoisB);                                          // atribuída a outro: some da fila
        Assert.Equal(HttpStatusCode.OK, detalheA.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detalheB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, responderB.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, assumirB.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, assumirIa.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Supervisor_gerencia_a_fila_e_atendente_nao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await using var api = amb.Api();
        var (atendente, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (_, chaveSupervisor) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        var sup = api.Cliente(chaveSupervisor);

        var atendenteAtribui = await api.Cliente(chaveAtendente).PostAsJsonAsync($"/supervisao/conversas/{conversa}/atribuir", new { responsavel = atendente.Id });
        var atribuir = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/atribuir", new { responsavel = atendente.Id });
        var atribuida = await amb.ConsultarAsync<Guid?>($"select atribuida_a from conversas where id = '{conversa}'");
        var outraEmpresa = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/atribuir", new { responsavel = Guid.NewGuid() });
        var devolver = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/atribuir", new { responsavel = (Guid?)null });

        Assert.Equal(HttpStatusCode.Forbidden, atendenteAtribui.StatusCode);
        Assert.Equal(HttpStatusCode.OK, atribuir.StatusCode);
        Assert.Equal(atendente.Id, atribuida);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outraEmpresa.StatusCode);
        Assert.Equal(HttpStatusCode.OK, devolver.StatusCode);
        Assert.Null(await amb.ConsultarAsync<Guid?>($"select atribuida_a from conversas where id = '{conversa}'"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-4")]
    public async Task Banco_recusa_resposta_de_atendente_em_conversa_nao_atribuida_a_ele()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await amb.AssumirAsync(); // administrador assume e fica responsável
        var (atendente, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAutenticadoAsync(amb.TenantId, chave, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("""
                insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
                values (gen_random_uuid(), @t, @c, null, @a, 'contornando a API', 'pendente', now() + interval '5 minutes')
                """, c, tx);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("a", atendente.Id);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public async Task Liberar_para_a_IA_tira_a_atribuicao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await amb.AssumirAsync();
        Assert.Equal(amb.Supervisor.Id, await amb.ConsultarAsync<Guid?>($"select atribuida_a from conversas where id = '{conversa}'"));

        await amb.LiberarAsync();

        Assert.Null(await amb.ConsultarAsync<Guid?>($"select atribuida_a from conversas where id = '{conversa}'"));
    }

    // ------------------------------------------------------------------ visibilidade do conhecimento (decisão 24)

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-11")]
    public async Task Cada_papel_ve_so_o_conhecimento_que_pode()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var futuro = amb.Relogio.Agora.AddDays(30);
        var cliente = await ClienteAsync(amb);
        var pub = await amb.ConhecimentoAsync("horario", "Abrimos às 8h.");
        var tmp = await amb.ConhecimentoAsync("horario", "Feriado: fechado.", [Classificacao.Temporario], validoAte: futuro);
        var inter = await amb.ConhecimentoAsync("horario", "Margem interna.", [Classificacao.Interno], validoAte: futuro);
        var cli = await amb.ConhecimentoAsync("horario", "Cliente prefere à tarde.", [Classificacao.Cliente], TipoEscopo.Cliente, cliente, futuro);
        var res = await amb.ConhecimentoAsync("horario", "Senha do cofre.", [Classificacao.Restrito], validoAte: futuro);
        var neg = await amb.ConhecimentoAsync("precos", "Desconto negociado.", [Classificacao.Negociacao], TipoEscopo.Cliente, cliente, futuro);
        var pendenteDoAdmin = await amb.ConhecimentoAsync("horario", "Rascunho do administrador.", status: "Rev");
        await using var api = amb.Api();
        var (_, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (_, chaveSupervisor) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        var atendente = api.Cliente(chaveAtendente);
        var propria = (await (await atendente.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "horario", conteudo = "Sugestão do atendente." })).JsonAsync()).GetProperty("id").GetGuid();

        var veAtendente = await ItensAsync(atendente);
        var veSupervisor = await ItensAsync(api.Cliente(chaveSupervisor));
        var veAdmin = await ItensAsync(api.Cliente(amb.ChaveSupervisor));
        var detalheRestrito = await atendente.GetAsync(new Uri($"/supervisao/conhecimento/{res.ItemId}", UriKind.Relative));

        Guid[] todos = [pub.ItemId, tmp.ItemId, inter.ItemId, cli.ItemId, res.ItemId, neg.ItemId, pendenteDoAdmin.ItemId, propria];
        Assert.Equal(new[] { pub.ItemId, tmp.ItemId, propria }.Order(), veAtendente.Intersect(todos).Order());
        Assert.Equal(new[] { pub.ItemId, tmp.ItemId, inter.ItemId, cli.ItemId, pendenteDoAdmin.ItemId, propria }.Order(), veSupervisor.Intersect(todos).Order());
        Assert.Equal(todos.Order(), veAdmin.Intersect(todos).Order());
        Assert.Equal(veAdmin.Count, (await amb.ContarAsync($"itens_conhecimento where tenant_id = '{amb.TenantId}'")));
        Assert.Equal(HttpStatusCode.NotFound, detalheRestrito.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Atendente_nao_ve_edicao_pendente_de_outro_usuario()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = await amb.ConhecimentoAsync("horario", "Abrimos às 8h."); // v2 aprovada
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Conhecimento.EditarAsync(amb.Supervisor, item.ItemId, 2, "Rascunho interno: margem de 40%.", null)).Situacao);
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var detalhe = await (await api.Cliente(chave).GetAsync(new Uri($"/supervisao/conhecimento/{item.ItemId}", UriKind.Relative))).JsonAsync();

        var conteudos = detalhe.GetProperty("versoes").EnumerateArray().Select(v => v.GetProperty("conteudo").GetString()).ToList();
        Assert.DoesNotContain("Rascunho interno: margem de 40%.", conteudos);
        Assert.Contains("Abrimos às 8h.", conteudos);
    }

    // ------------------------------------------------------------------ políticas fundamentais (INV-25)

    [Theory]
    [Trait("Regra", "INV-25")]
    [InlineData("Interno")]
    [InlineData("Restrito")]
    public async Task Politica_nao_libera_INT_nem_RES_em_resposta_automatica(string classificacao)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();

        var r = await api.Cliente(amb.ChaveSupervisor).PutAsJsonAsync("/supervisao/politica",
            new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" }, classificacoesPermitidas = new[] { "Publico", classificacao } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(1, await amb.ConsultarAsync<int>("select max(versao) from politicas_tenant where tenant_id = @tenant"));
    }

    [Fact]
    [Trait("Regra", "INV-25")]
    [Trait("Regra", "INV-8")]
    public async Task Administrador_nao_remove_os_termos_de_compromisso_padrao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        await using var api = amb.Api();
        var publicar = await api.Cliente(amb.ChaveSupervisor).PutAsJsonAsync("/supervisao/politica",
            new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" }, termosCompromisso = Array.Empty<string>() } });
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario + " Garantimos o atendimento.", "horario")));

        var r = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));

        Assert.Equal(HttpStatusCode.OK, publicar.StatusCode);
        Assert.Equal(ResultadoProcessamento.Transferida, r);
        Assert.Equal("CompromissoSemFonte", await amb.ConsultarAsync<string>("select motivo from decisoes_envio where tenant_id = @tenant and tipo = 'resposta' order by criada_em desc limit 1"));
    }

    // ------------------------------------------------------------------ e-mail institucional (decisão 29)

    [Fact]
    [Trait("Regra", "INV-23")]
    public async Task Email_pessoal_nao_entra_em_conhecimento_e_o_institucional_entra()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco, p => p.Emails.Add("atendimento@empresa.com.br"));
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);

        var avulsaPessoal = await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "contato", conteudo = "Fale com joao.silva@gmail.com" });
        var orientacaoInstitucional = await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar",
            new { texto = "Oriente o cliente a escrever para atendimento@empresa.com.br", assunto = "contato" });
        var orientacaoPessoal = await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar",
            new { texto = "O cliente usa joao.silva@gmail.com", assunto = "contato" });
        var cepDoCliente = await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar",
            new { texto = "O CEP dele é 88015-200", assunto = "contato" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, avulsaPessoal.StatusCode);
        Assert.Equal(HttpStatusCode.Created, orientacaoInstitucional.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, orientacaoPessoal.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, cepDoCliente.StatusCode);
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from versoes_conhecimento where tenant_id = '{amb.TenantId}' and (conteudo like '%gmail%' or conteudo like '%88015%')"));
    }

    // ------------------------------------------------------------------ validade máxima por classificação (decisão 28)

    [Fact]
    [Trait("Regra", "INV-19")]
    public async Task Aprovacao_respeita_a_validade_maxima_da_classificacao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var publicar = await admin.PutAsJsonAsync("/supervisao/politica",
            new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" }, validadeMaximaDias = new Dictionary<string, int> { ["Temporario"] = 7 } } });
        var item = (await (await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "horario", conteudo = "Fechado no feriado." })).JsonAsync()).GetProperty("id").GetGuid();

        var longa = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar",
            new { versaoProposta = 1, permissoes = new { classificacoes = new[] { "Temporario" }, escopoTipo = "Empresa", validoAte = amb.Relogio.Agora.AddDays(30) } });
        var curta = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar",
            new { versaoProposta = 1, permissoes = new { classificacoes = new[] { "Temporario" }, escopoTipo = "Empresa", validoAte = amb.Relogio.Agora.AddDays(5) } });

        Assert.Equal(HttpStatusCode.OK, publicar.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longa.StatusCode);
        Assert.Equal(HttpStatusCode.OK, curta.StatusCode);
    }

    // ------------------------------------------------------------------ HTTPS e recusas do banco na API (decisão 30)

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Fora_do_desenvolvimento_a_supervisao_exige_https()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = new ApiEmProducao(banco, amb.Relogio);
        var http = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", amb.ChaveSupervisor);
        var https = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        https.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", amb.ChaveSupervisor);

        var semTls = await http.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));
        var comTls = await https.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, semTls.StatusCode);
        Assert.Equal(HttpStatusCode.OK, comTls.StatusCode);
    }

    [Theory]
    [Trait("Regra", "INV-14")]
    [InlineData("P0001", HttpStatusCode.Conflict)]
    [InlineData("42501", HttpStatusCode.Forbidden)]
    [InlineData("28000", HttpStatusCode.Forbidden)]
    public async Task Recusa_do_banco_vira_resposta_generica_sem_detalhe_interno(string codigo, HttpStatusCode esperado)
    {
        // Revisão da F3 (R21): o tratamento das recusas do banco na API não tinha teste, porque a API confere antes.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = new ApiComFalhaDoBanco(banco, amb.Relogio);
        var cliente = api.Cliente(amb.ChaveSupervisor);

        var r = await cliente.GetAsync(new Uri($"/supervisao/teste-falha/{codigo}", UriKind.Relative));
        var corpo = await r.Content.ReadAsStringAsync();

        Assert.Equal(esperado, r.StatusCode);
        Assert.Contains("Operação recusada pelas regras do sistema.", corpo, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo interno", corpo, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ revisão pós-F3 (revisoes/2026-11-25-pos-f3.md)

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task G7_atendente_nao_pega_da_fila_para_outra_pessoa()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (outro, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ComoAsync(amb, chave, "update conversas set atribuida_a = @x where id = @c", conversa, outro.Id));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Null(await amb.ConsultarAsync<Guid?>($"select atribuida_a from conversas where id = '{conversa}'"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-5")]
    public async Task S4_atendente_comprovado_nao_tira_da_IA_conversa_fora_da_fila()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        var (atendente, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => ComoAsync(amb, chave,
            "update conversas set estado = 'Humano', versao_estado = versao_estado + 1, atribuida_a = @x where id = @c", conversa, atendente.Id));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-15")]
    public async Task S1_evento_da_supervisao_registra_so_o_usuario_comprovado_e_a_versao_proposta_e_carimbada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        var (atendente, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var item = Guid.NewGuid();

        var evento = await Assert.ThrowsAsync<PostgresException>(() => ComoAsync(amb, chave,
            "insert into eventos_historico (tenant_id, conversa_id, tipo, dados) values (@t, @c, 'conversa_assumida', jsonb_build_object('usuario', @x::text))", conversa, amb.Supervisor.Id));
        await ComoAsync(amb, chave, """
            insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (@x, @t, 'horario', 'Rev', 1);
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, origem, proposta_por)
            values (@x, 1, @t, 'texto', '{}', 'Empresa', 'sugestao:usuario:outra-pessoa', null)
            """, conversa, item);

        Assert.Equal("P0001", evento.SqlState);
        Assert.Equal(atendente.Id, await amb.ConsultarAsync<Guid?>($"select proposta_por from versoes_conhecimento where item_id = '{item}'"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-4")]
    public async Task S8_resposta_de_atendente_nao_sai_se_a_conversa_foi_reatribuida_antes_do_envio()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await amb.Worker().ProcessarProximoAsync(); // aviso de transferência
        var enviadasAntes = amb.Gateway.Enviadas.Count;
        var (atendente, _) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Supervisao.AssumirAsync(atendente, conversa)).Situacao);
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Supervisao.ResponderAsync(atendente, conversa, "Já verifico para você.")).Situacao);
        Assert.Equal(SituacaoOperacao.Ok, (await amb.Supervisao.AtribuirAsync(amb.Supervisor, conversa, amb.Supervisor.Id)).Situacao);

        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Equal(enviadasAntes, amb.Gateway.Enviadas.Count);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task S3_caminho_em_maiusculas_tambem_passa_pela_autenticacao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();

        var r = await api.Cliente(null).PostAsJsonAsync("/SUPERVISAO/conhecimento", new { assunto = "x", conteudo = "y" });

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync($"itens_conhecimento where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task S9_fora_do_desenvolvimento_a_configuracao_nao_desliga_o_https()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = new ApiEmProducaoSemHttps(banco, amb.Relogio);
        var http = api.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost") });

        var r = await http.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-25")]
    public async Task S10_politica_com_elemento_nulo_e_recusada_sem_erro_interno()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();

        var r = await api.Cliente(amb.ChaveSupervisor).PutAsJsonAsync("/supervisao/politica",
            new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" }, emailsInstitucionais = new string?[] { null } } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public async Task G1_cep_nao_entra_em_conhecimento_nem_avulso()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var cliente = await ClienteAsync(amb);

        var avulsa = await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "endereco", conteudo = "Rua das Flores, CEP 88015-200" });
        var comEscopoDeCliente = await admin.PostAsJsonAsync("/supervisao/conhecimento",
            new { assunto = "endereco", conteudo = "Entrega no CEP 88015-200", sugeridas = new { escopoTipo = "Cliente", escopoRef = cliente } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, avulsa.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, comEscopoDeCliente.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-15")]
    public async Task G3_conversa_com_a_IA_nao_recebe_responsavel()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);

        var pelaApi = await amb.Supervisao.AtribuirAsync(amb.Supervisor, conversa, amb.Supervisor.Id);
        var direto = await Assert.ThrowsAsync<PostgresException>(() => ComoAsync(amb, amb.ChaveSupervisor, "update conversas set atribuida_a = @x where id = @c", conversa, amb.Supervisor.Id));

        Assert.Equal(SituacaoOperacao.Conflito, pelaApi.Situacao);
        Assert.Equal("P0001", direto.SqlState);
        Assert.Equal(0L, await amb.ContarAsync($"eventos_historico where tenant_id = '{amb.TenantId}' and tipo = 'conversa_atribuida'"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-10")]
    public async Task G4_atendente_ve_o_telefone_da_fila_so_parcialmente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var atendente = api.Cliente(chave);

        var naFila = (await (await atendente.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative))).JsonAsync())[0].GetProperty("waIdCliente").GetString()!;
        await atendente.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var atribuida = (await (await atendente.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative))).JsonAsync())[0].GetProperty("waIdCliente").GetString()!;

        Assert.DoesNotContain(AmbientePipeline.WaCliente, naFila, StringComparison.Ordinal);
        Assert.EndsWith(AmbientePipeline.WaCliente[^4..], naFila, StringComparison.Ordinal);
        Assert.Equal(AmbientePipeline.WaCliente, atribuida);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    [Trait("Regra", "INV-20")]
    public async Task G4_atendente_nao_ve_conhecimento_revogado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var revogado = await amb.ConhecimentoAsync("horario", "Abrimos às 7h.", status: "Revogado");
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var itens = await ItensAsync(api.Cliente(chave));

        Assert.DoesNotContain(revogado.ItemId, itens);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task S6_auditoria_do_atendente_nao_mostra_edicoes_de_outros()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var item = (await amb.Conhecimento.SugerirAsync(amb.Supervisor, "horario", "Abrimos às 8h.", null, null)).Id!.Value;
        await amb.Conhecimento.AprovarAsync(amb.Supervisor, item, 1, new PermissoesConhecimento(["Publico"], "Empresa", null, null));
        await amb.Conhecimento.EditarAsync(amb.Supervisor, item, 2, "Rascunho com margem interna.", null);
        await amb.Conhecimento.RejeitarAsync(amb.Supervisor, item, "margem é interna");
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);

        var detalhe = await (await api.Cliente(chave).GetAsync(new Uri($"/supervisao/conhecimento/{item}", UriKind.Relative))).JsonAsync();
        var tipos = detalhe.GetProperty("auditoria").EnumerateArray().Select(e => e.GetProperty("tipo").GetString()).ToList();

        Assert.Equal(["conhecimento_aprovado"], tipos);
    }

    // ------------------------------------------------------------------ auxiliares

    private static async Task<List<Guid>> IdsAsync(HttpClient c) =>
        (await (await c.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative))).JsonAsync()).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

    private static async Task<List<Guid>> ItensAsync(HttpClient c) =>
        (await (await c.GetAsync(new Uri("/supervisao/conhecimento", UriKind.Relative))).JsonAsync()).EnumerateArray().Select(x => x.GetProperty("id").GetGuid()).ToList();

    private static async Task<Guid> ClienteAsync(AmbientePipeline amb)
    {
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        return (await amb.ConsultarAsync<Guid?>("select id from clientes where tenant_id = @tenant"))!.Value;
    }

    /// <summary>Uma segunda conversa (outro cliente) que continua com a IA.</summary>
    private static async Task<Guid> ConversaDeOutroClienteComIaAsync(AmbientePipeline amb)
    {
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?") with { WaIdCliente = "5548999990002" });
        return (await amb.ConsultarAsync<Guid?>("select cv.id from conversas cv join clientes cl on cl.id = cv.cliente_id where cv.tenant_id = @tenant and cl.wa_id = '5548999990002'"))!.Value;
    }

    private static Task<int> ComoAsync(AmbientePipeline amb, string chave, string sql, Guid conversa, Guid x) =>
        new ExecutorTenant(amb.SupervisaoDs).ExecutarAutenticadoAsync(amb.TenantId, chave, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("t", amb.TenantId);
            cmd.Parameters.AddWithValue("c", conversa);
            cmd.Parameters.AddWithValue("x", x);
            return await cmd.ExecuteNonQueryAsync(ct);
        });

    private sealed class ApiEmProducaoSemHttps(BancoFixture banco, TimeProvider relogio) : ApiSupervisaoBase(banco, relogio)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
            builder.UseSetting("Seguranca:ExigirHttps", "false");
        }
    }

    private sealed class ApiEmProducao(BancoFixture banco, TimeProvider relogio) : ApiSupervisaoBase(banco, relogio)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Production");
        }
    }

    /// <summary>A API real com uma rota a mais, atrás da mesma autenticação, que provoca uma recusa do banco.</summary>
    private sealed class ApiComFalhaDoBanco(BancoFixture banco, TimeProvider relogio) : ApiSupervisaoBase(banco, relogio)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, RotaDeFalha>());
        }

        private sealed class RotaDeFalha : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                next(app);
                app.Run(async http =>
                {
                    var codigo = http.Request.Path.Value!.Split('/')[^1];
                    var ds = http.RequestServices.GetRequiredKeyedService<NpgsqlDataSource>(EndpointsSupervisao.ChaveDataSource);
                    await using var c = await ds.OpenConnectionAsync();
                    await using var cmd = new NpgsqlCommand($"do $$ begin raise exception 'segredo interno' using errcode = '{codigo}'; end $$", c);
                    await cmd.ExecuteNonQueryAsync();
                    http.Response.StatusCode = StatusCodes.Status200OK;
                });
            };
        }
    }
}
