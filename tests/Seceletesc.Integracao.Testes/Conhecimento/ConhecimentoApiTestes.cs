using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Seceletesc.Dominio.Contratos;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Integracao.Testes.Pipeline;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.Conhecimento;

/// <summary>
/// F3, governança do conhecimento, de ponta a ponta (HTTP, papel seceletesc_supervisao, PostgreSQL real).
/// Cada teste confere a decisão do backend e o que ficou no banco, e se o agente recebe ou não o conhecimento.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class ConhecimentoApiTestes(BancoFixture banco)
{
    private const string Horario = "Atendemos de segunda a sexta, das 9h às 18h.";
    private static readonly DateTimeOffset Futuro = AmbientePipeline.Inicio.AddDays(30);

    // ------------------------------------------------------------------ papéis

    [Fact]
    [Trait("Regra", "INV-17")]
    [Trait("Regra", "CTX-012")]
    public async Task Atendente_sugere_mas_nao_edita_aprova_rejeita_nem_revoga()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var (_, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var atendente = api.Cliente(chaveAtendente);

        var item = await SugerirAsync(atendente, "horario", "Abrimos aos sábados das 8h às 12h.");
        var tentativas = new[]
        {
            await atendente.PostAsJsonAsync($"/supervisao/conhecimento/{item}/editar", new { versaoBase = 1, conteudo = "outro" }),
            await atendente.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Publico"])),
            await atendente.PostAsJsonAsync($"/supervisao/conhecimento/{item}/rejeitar", new { motivo = "não" }),
            await atendente.PostAsJsonAsync($"/supervisao/conhecimento/{item}/revogar", new { motivo = "não" }),
        };

        Assert.All(tentativas, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item}'"));
        Assert.Equal(1L, await amb.ContarAsync($"versoes_conhecimento where item_id = '{item}'"));
    }

    [Fact]
    [Trait("Regra", "INV-17")]
    [Trait("Regra", "INV-11")]
    public async Task Supervisor_aprova_conhecimento_comum_mas_nao_comercial_nem_revoga()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var (_, chaveSupervisor) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        var supervisor = api.Cliente(chaveSupervisor);
        var cliente = await ClienteAsync(amb);

        var comum = await SugerirAsync(supervisor, "horario", Horario);
        var comercial = await SugerirAsync(supervisor, "precos", "Para este cliente, 10% de desconto.");
        var aprovaComum = await supervisor.PostAsJsonAsync($"/supervisao/conhecimento/{comum}/aprovar", Aprovacao(1, ["Publico"]));
        var aprovaComercial = await supervisor.PostAsJsonAsync($"/supervisao/conhecimento/{comercial}/aprovar",
            Aprovacao(1, ["Negociacao"], "Cliente", cliente, Futuro));
        var revoga = await supervisor.PostAsJsonAsync($"/supervisao/conhecimento/{comum}/revogar", new { motivo = "teste" });

        Assert.Equal(HttpStatusCode.OK, aprovaComum.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, aprovaComercial.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revoga.StatusCode);
        Assert.Equal("Aprovado", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{comum}'"));
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{comercial}'"));
    }

    [Theory]
    [Trait("Regra", "INV-11")]
    [Trait("Regra", "INV-18")]
    [InlineData("Empresa", false, true)]   // NEG para a empresa toda: nunca
    [InlineData("Cliente", true, false)]   // NEG sem validade: nunca
    public async Task Mesmo_o_administrador_so_aprova_condicao_comercial_com_cliente_e_validade(string escopo, bool comReferencia, bool comValidade)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var cliente = await ClienteAsync(amb);
        var item = await SugerirAsync(admin, "precos", "Para este cliente, 10% de desconto.");

        var invalida = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar",
            Aprovacao(1, ["Negociacao"], escopo, comReferencia ? cliente : null, comValidade ? Futuro : null));
        var valida = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Negociacao"], "Cliente", cliente, Futuro));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalida.StatusCode);
        Assert.Equal(HttpStatusCode.OK, valida.StatusCode);
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public async Task Atendente_assume_e_responde_mas_nao_libera_nem_publica_politica()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaNaFilaAsync(Horario);
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var atendente = api.Cliente(chave);

        var assumir = await atendente.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var responder = await atendente.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Olá, já verifico." });
        await amb.Worker().ProcessarProximoAsync();
        var liberar = await atendente.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 3 });
        var politica = await atendente.PutAsJsonAsync("/supervisao/politica", new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" } } });

        Assert.Equal(HttpStatusCode.OK, assumir.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, responder.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, liberar.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, politica.StatusCode);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
    }

    [Theory]
    [Trait("Regra", "INV-11")]
    [InlineData(PapelSupervisao.Atendente)]
    [InlineData(PapelSupervisao.Supervisor)]
    public async Task So_administrador_publica_politica(PapelSupervisao papel)
    {
        // Liberar NEG/EXC, ligar conhecimento de cliente ao modelo ou mexer nos termos são restrições críticas.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var (_, chave) = await amb.CriarUsuarioAsync(papel);

        var r = await api.Cliente(chave).PutAsJsonAsync("/supervisao/politica", new
        {
            versaoEsperada = 1,
            conteudo = new { assuntosAutonomos = new[] { "horario", "precos" }, classificacoesPermitidas = new[] { "Publico", "Negociacao", "Excecao" } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(1, await amb.ConsultarAsync<int>("select max(versao) from politicas_tenant where tenant_id = @tenant"));
    }

    // ------------------------------------------------------------------ fluxo, versões e auditoria

    [Fact]
    [Trait("Regra", "INV-16")]
    [Trait("Regra", "INV-19")]
    [Trait("Regra", "CTX-013")]
    [Trait("Regra", "INV-15")]
    public async Task Sugestao_revisada_e_aprovada_gera_versao_com_autor_permissoes_explicitas_e_auditoria()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var (atendente, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (supervisor, chaveSupervisor) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        var sup = api.Cliente(chaveSupervisor);

        // A sugestão propõe uma classificação de risco; quem aprova define as permissões que valem.
        var item = await SugerirAsync(api.Cliente(chaveAtendente), "horario", "Abrimos aos sábados das 8h às 12h.", sugeridas: new { classificacoes = new[] { "Negociacao" } });
        var editar = await sup.PostAsJsonAsync($"/supervisao/conhecimento/{item}/editar", new { versaoBase = 1, conteudo = "Aos sábados, abrimos das 8h às 12h." });
        var aprovar = await sup.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(2, ["Publico"]));
        var detalhe = await (await sup.GetAsync(new Uri($"/supervisao/conhecimento/{item}", UriKind.Relative))).JsonAsync();

        Assert.Equal(HttpStatusCode.OK, editar.StatusCode);
        Assert.Equal(HttpStatusCode.OK, aprovar.StatusCode);
        var versoes = detalhe.GetProperty("versoes").EnumerateArray().ToList();
        Assert.Equal([1, 2, 3], versoes.Select(v => v.GetProperty("versao").GetInt32()));
        Assert.All(versoes.Take(2), v => Assert.Equal(JsonValueKind.Null, v.GetProperty("aprovadaPor").ValueKind));
        Assert.Equal(supervisor.Id, versoes[2].GetProperty("aprovadaPor").GetGuid());
        Assert.Equal(["Publico"], versoes[2].GetProperty("classificacoes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("Aos sábados, abrimos das 8h às 12h.", versoes[2].GetProperty("conteudo").GetString());
        Assert.Equal(3, detalhe.GetProperty("item").GetProperty("versaoVigente").GetInt32());
        Assert.Equal(["conhecimento_sugerido", "conhecimento_editado", "conhecimento_aprovado"],
            detalhe.GetProperty("auditoria").EnumerateArray().Select(e => e.GetProperty("tipo").GetString()));
        Assert.Equal(atendente.Id, detalhe.GetProperty("auditoria")[0].GetProperty("dados").GetProperty("usuario").GetGuid());
    }

    [Fact]
    [Trait("Regra", "INV-16")]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "CTX-014")]
    public async Task Sugestao_pendente_e_edicao_pendente_nunca_chegam_ao_agente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var aprovado = await SugerirAsync(admin, "horario", Horario);
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{aprovado}/aprovar", Aprovacao(1, ["Publico"]));
        var pendente = await SugerirAsync(admin, "horario", "Abrimos aos domingos.");
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{aprovado}/editar", new { versaoBase = 2, conteudo = "Atendemos 24 horas." });

        var contexto = await ContextoEntregueAsync(amb, "Qual o horário?");

        Assert.Equal([(aprovado, 2)], contexto.Conhecimentos.Select(k => (k.ItemId, k.Versao)));
        Assert.Equal(Horario, contexto.Conhecimentos.Single().Conteudo);
        Assert.DoesNotContain(contexto.Conhecimentos, k => k.ItemId == pendente);
    }

    [Fact]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "CTX-014")]
    [Trait("Regra", "ADV-19")]
    public async Task Rejeitado_nao_e_aprovado_depois_e_revogado_cancela_a_mensagem_na_fila_e_some_do_contexto()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var rejeitado = await SugerirAsync(admin, "horario", "Abrimos às 6h.");
        var rejeitar = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{rejeitado}/rejeitar", new { motivo = "horário errado" });
        var aprovarRejeitado = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{rejeitado}/aprovar", Aprovacao(1, ["Publico"]));

        var item = await SugerirAsync(admin, "horario", Horario);
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Publico"]));
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var r = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));
        var revogar = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/revogar", new { motivo = "horário mudou" });
        var ciclo = await amb.Worker().ProcessarProximoAsync();
        // A conversa foi para a supervisão (resposta cancelada); outro cliente pergunta depois da revogação.
        var depois = await ContextoEntregueAsync(amb, "Qual o horário?", waId: "5511955554444");

        Assert.Equal(HttpStatusCode.OK, rejeitar.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, aprovarRejeitado.StatusCode);
        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, r);
        Assert.Equal(HttpStatusCode.OK, revogar.StatusCode);
        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.DoesNotContain(amb.Gateway.Enviadas, e => e.Texto == Horario);
        Assert.All(motor.Contextos, ctx => Assert.DoesNotContain(ctx.Conhecimentos, k => k.ItemId == rejeitado));
        Assert.DoesNotContain(depois.Conhecimentos, k => k.ItemId == item);
    }

    [Fact]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "CTX-014")]
    public async Task Conhecimento_expirado_deixa_de_chegar_ao_agente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco, p => p.Classificacoes.Add(Dominio.Conhecimento.Classificacao.Temporario));
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var item = await SugerirAsync(admin, "horario", "Nesta semana, abrimos às 10h.");
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Temporario"], validoAte: AmbientePipeline.Inicio.AddHours(1)));

        var antes = await ContextoEntregueAsync(amb, "Que horas abrem?");
        amb.Relogio.Agora += TimeSpan.FromHours(2);
        var depois = await ContextoEntregueAsync(amb, "E hoje, que horas abrem?", waId: "5511966665555");

        Assert.Contains(antes.Conhecimentos, k => k.ItemId == item);
        Assert.DoesNotContain(depois.Conhecimentos, k => k.ItemId == item);
    }

    [Theory]
    [Trait("Regra", "INV-19")]
    [InlineData("""{"versaoProposta":1}""")]                                                                      // sem permissões
    [InlineData("""{"versaoProposta":1,"permissoes":{"classificacoes":[],"escopoTipo":"Empresa"}}""")]           // sem classificação
    [InlineData("""{"versaoProposta":1,"permissoes":{"classificacoes":["Interno"],"escopoTipo":"Empresa"}}""")]  // não-PUB sem validade
    [InlineData("""{"versaoProposta":1,"permissoes":{"classificacoes":["Publico"],"escopoTipo":"Proposta","escopoRef":"00000000-0000-0000-0000-000000000001"}}""")]
    [InlineData("""{"versaoProposta":1,"permissoes":{"classificacoes":["publico"],"escopoTipo":"Empresa"}}""")]   // nome inexato
    [InlineData("""{"versaoProposta":1,"permissoes":{"classificacoes":["Publico"],"escopoTipo":"Cliente","escopoRef":"00000000-0000-0000-0000-000000000001"}}""")] // cliente inexistente
    public async Task Aprovacao_sem_permissoes_explicitas_e_validas_e_recusada(string corpo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var item = await SugerirAsync(admin, "horario", Horario);

        var r = await admin.PostAsync(new Uri($"/supervisao/conhecimento/{item}/aprovar", UriKind.Relative),
            new StringContent(corpo, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item}'"));
    }

    // ------------------------------------------------------------------ isolamento

    [Fact]
    [Trait("Regra", "ADV-22")]
    [Trait("Regra", "INV-10")]
    public async Task Usuario_de_outra_empresa_nao_ve_nem_altera_conhecimento_pelo_id()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        await using var api = a.Api();
        var adminA = api.Cliente(a.ChaveSupervisor);
        var adminB = api.Cliente(b.ChaveSupervisor);
        var pendente = await SugerirAsync(adminA, "horario", "Abrimos às 7h.");
        var aprovado = await SugerirAsync(adminA, "horario", Horario);
        await adminA.PostAsJsonAsync($"/supervisao/conhecimento/{aprovado}/aprovar", Aprovacao(1, ["Publico"]));

        var respostas = new[]
        {
            await adminB.GetAsync(new Uri($"/supervisao/conhecimento/{pendente}", UriKind.Relative)),
            await adminB.PostAsJsonAsync($"/supervisao/conhecimento/{pendente}/editar", new { versaoBase = 1, conteudo = "Abrimos às 5h." }),
            await adminB.PostAsJsonAsync($"/supervisao/conhecimento/{pendente}/aprovar", Aprovacao(1, ["Publico"])),
            await adminB.PostAsJsonAsync($"/supervisao/conhecimento/{pendente}/rejeitar", new { motivo = "x" }),
            await adminB.PostAsJsonAsync($"/supervisao/conhecimento/{aprovado}/revogar", new { motivo = "x" }),
        };
        var listaB = await (await adminB.GetAsync(new Uri("/supervisao/conhecimento", UriKind.Relative))).JsonAsync();
        // Sugestão de B não consegue apontar para conversa ou cliente de A.
        var conversaA = await a.ConversaAtendidaPelaIaAsync(Horario);
        var sugestaoCruzada = await adminB.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "horario", conteudo = "x", conversaOrigem = conversaA });

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(0, listaB.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, sugestaoCruzada.StatusCode);
        Assert.Equal("Rev", await a.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{pendente}'"));
        Assert.Equal("Aprovado", await a.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{aprovado}'"));
        Assert.Equal(1L, await a.ContarAsync($"versoes_conhecimento where item_id = '{pendente}'"));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    [Trait("Regra", "CTX-007")]
    [Trait("Regra", "INV-11")]
    public async Task Conhecimento_aprovado_para_um_cliente_nao_chega_ao_atendimento_de_outro()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        await admin.PutAsJsonAsync("/supervisao/politica", new
        {
            versaoEsperada = 1,
            conteudo = new
            {
                assuntosAutonomos = new[] { "horario", "agenda" },
                classificacoesPermitidas = new[] { "Publico", "Cliente" },
                enviarConhecimentoClienteAoModelo = true,
                textoAvisoTransferencia = "Vou encaminhar sua mensagem para nossa equipe.",
            },
        });
        var clienteA = await ClienteAsync(amb);
        var item = await SugerirAsync(admin, "agenda", "Sua consulta de retorno é na terça às 14h.");
        var aprovar = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Cliente"], "Cliente", clienteA, Futuro));

        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        var contextoA = await ContextoEntregueAsync(amb, "Quando é meu retorno?");
        var contextoB = await ContextoEntregueAsync(amb, "Quando é meu retorno?", waId: "5511977776666");

        Assert.Equal(HttpStatusCode.OK, aprovar.StatusCode);
        Assert.Contains(contextoA.Conhecimentos, k => k.ItemId == item);
        Assert.DoesNotContain(contextoB.Conhecimentos, k => k.ItemId == item);
    }

    // ------------------------------------------------------------------ concorrência

    [Fact]
    [Trait("Regra", "INV-20")]
    public async Task Aprovacao_e_revogacao_simultaneas_terminam_sempre_revogadas_e_fora_do_contexto()
    {
        for (var rodada = 0; rodada < 5; rodada++)
        {
            await using var amb = await AmbientePipeline.CriarAsync(banco);
            var item = await amb.ConhecimentoAsync("horario", Horario); // aprovado (v2)
            var editar = await amb.Conhecimento.EditarAsync(amb.Supervisor, item.ItemId, 2, "Atendemos das 8h às 20h.", null);
            Assert.Equal(SituacaoOperacao.Ok, editar.Situacao);

            var aprovar = amb.Conhecimento.AprovarAsync(amb.Supervisor, item.ItemId, 3, new PermissoesConhecimento(["Publico"], "Empresa", null, null));
            var revogar = amb.Conhecimento.RevogarAsync(amb.Supervisor, item.ItemId, "horário em revisão");
            var resultados = await Task.WhenAll(aprovar, revogar);

            Assert.Equal(SituacaoOperacao.Ok, resultados[1].Situacao);
            Assert.Contains(resultados[0].Situacao, new[] { SituacaoOperacao.Ok, SituacaoOperacao.Conflito });
            Assert.Equal("Revogado", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item.ItemId}'"));
            Assert.DoesNotContain((await ContextoEntregueAsync(amb, "Qual o horário?")).Conhecimentos, k => k.ItemId == item.ItemId);
        }
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public async Task Edicao_e_aprovacao_simultaneas_sobre_a_mesma_versao_so_uma_vence()
    {
        for (var rodada = 0; rodada < 5; rodada++)
        {
            await using var amb = await AmbientePipeline.CriarAsync(banco);
            var item = (await amb.Conhecimento.SugerirAsync(amb.Supervisor, "horario", Horario, null, null)).Id!.Value;

            var editar = amb.Conhecimento.EditarAsync(amb.Supervisor, item, 1, "Atendemos das 8h às 20h.", null);
            var aprovar = amb.Conhecimento.AprovarAsync(amb.Supervisor, item, 1, new PermissoesConhecimento(["Publico"], "Empresa", null, null));
            var resultados = await Task.WhenAll(editar, aprovar);

            Assert.Single(resultados, r => r.Situacao == SituacaoOperacao.Ok);
            Assert.Single(resultados, r => r.Situacao == SituacaoOperacao.Conflito);
            var status = await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item}'");
            // Se a edição venceu, nada foi aprovado; se a aprovação venceu, vale o texto que o revisor viu.
            Assert.Equal(resultados[1].Situacao == SituacaoOperacao.Ok ? "Aprovado" : "Rev", status);
            if (status == "Aprovado")
            {
                Assert.Equal(Horario, await amb.ConsultarAsync<string>($"select v.conteudo from versoes_conhecimento v join itens_conhecimento i on i.id = v.item_id and v.versao = i.versao_vigente where i.id = '{item}'"));
            }
        }
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public async Task Edicao_ou_aprovacao_sobre_versao_desatualizada_e_recusada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var item = await SugerirAsync(admin, "horario", Horario);
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/editar", new { versaoBase = 1, conteudo = "Atendemos das 8h às 20h." });

        // Outro revisor ainda está olhando a versão 1.
        var editarAntiga = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/editar", new { versaoBase = 1, conteudo = "Atendemos 24 horas." });
        var aprovarAntiga = await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Publico"]));

        Assert.Equal(HttpStatusCode.Conflict, editarAntiga.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, aprovarAntiga.StatusCode);
        Assert.Equal(2L, await amb.ContarAsync($"versoes_conhecimento where item_id = '{item}'"));
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item}'"));
    }

    // ------------------------------------------------------------------ dados protegidos em conhecimento e logs

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("O CPF do responsável é 529.982.247-25.")]
    [InlineData("Cartão para cobrança: 4111 1111 1111 1111.")]
    public async Task Sugestao_com_dado_de_identificacao_ou_financeiro_e_recusada(string conteudo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();

        var r = await api.Cliente(amb.ChaveSupervisor).PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "cadastro", conteudo });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, await amb.ContarAsync("itens_conhecimento where tenant_id = @tenant"));
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    [Trait("Regra", "INV-6")]
    public async Task Email_da_empresa_aprovado_em_conhecimento_sai_na_resposta_automatica_e_fica_mascarado_no_historico()
    {
        const string fonte = "Segunda via de boleto pelo e-mail financeiro@empresa.com.br.";
        await using var amb = await AmbientePipeline.CriarAsync(banco, p => p.Emails.Add("financeiro@empresa.com.br"));
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var item = await SugerirAsync(admin, "horario", fonte);
        await admin.PostAsJsonAsync($"/supervisao/conhecimento/{item}/aprovar", Aprovacao(1, ["Publico"]));
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, "Peça a segunda via para financeiro@empresa.com.br.", "horario")));

        var r = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Como peço a segunda via?"));
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, r);
        Assert.Equal(ResultadoCiclo.Enviada, ciclo);
        Assert.Contains("financeiro@empresa.com.br", amb.Gateway.Enviadas[^1].Texto, StringComparison.Ordinal);
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from mensagens where tenant_id = '{amb.TenantId}' and texto_mascarado like '%financeiro@%'"));
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from outbox where tenant_id = '{amb.TenantId}' and texto like '%financeiro@%'"));
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public async Task Logs_da_api_nao_registram_dado_protegido_nem_chave()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);
        var logs = new LogsCapturados();
        await using var api = new ApiSupervisaoComLogs(banco, amb.Relogio, logs);
        var admin = api.Cliente(amb.ChaveSupervisor);

        await admin.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Seu CPF 529.982.247-25 está ok." });
        await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Envie para financeiro@empresa.com.br." });
        await admin.PostAsJsonAsync("/supervisao/conhecimento", new { assunto = "cadastro", conteudo = "Cartão 4111 1111 1111 1111." });
        await api.Cliente("sec_chave-vazada-que-nao-existe").GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));
        await api.Cliente(amb.ChaveSupervisor[..^2] + "xx").GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        var texto = logs.Texto();
        Assert.Contains("Falha de autenticação", texto, StringComparison.Ordinal); // os logs foram mesmo capturados
        Assert.DoesNotContain("529.982", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("financeiro@", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("4111", texto, StringComparison.Ordinal);
        Assert.DoesNotContain("chave-vazada", texto, StringComparison.Ordinal);
        Assert.DoesNotContain(amb.ChaveSupervisor[..20], texto, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ pendência de retomada pelo celular

    [Fact]
    [Trait("Regra", "INV-24")]
    public async Task Pendencia_de_retomada_continua_apos_liberar_e_so_supervisor_a_encerra_com_motivo_registrado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await ConversaComPendenciaAsync(amb);
        await using var api = amb.Api();
        var (_, chaveAtendente) = await amb.CriarUsuarioAsync(PapelSupervisao.Atendente);
        var (supervisor, chaveSupervisor) = await amb.CriarUsuarioAsync(PapelSupervisao.Supervisor);
        var sup = api.Cliente(chaveSupervisor);

        await sup.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var versao = await amb.VersaoConversaAsync();
        var liberar = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = versao });
        var aposLiberar = await amb.ContarAsync("conversas where tenant_id = @tenant and marcada_retomada_humana");
        var porAtendente = await api.Cliente(chaveAtendente).PostAsJsonAsync($"/supervisao/conversas/{conversa}/encerrar-retomada", new { motivo = "resolvido" });
        var semMotivo = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/encerrar-retomada", new { motivo = " " });
        var encerrar = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/encerrar-retomada", new { motivo = "Cliente atendido pelo celular." });
        var deNovo = await sup.PostAsJsonAsync($"/supervisao/conversas/{conversa}/encerrar-retomada", new { motivo = "de novo" });
        var detalhe = await (await sup.GetAsync(new Uri($"/supervisao/conversas/{conversa}", UriKind.Relative))).JsonAsync();

        Assert.Equal(HttpStatusCode.OK, liberar.StatusCode);
        Assert.Equal(1, aposLiberar);
        Assert.Equal(HttpStatusCode.Forbidden, porAtendente.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, semMotivo.StatusCode);
        Assert.Equal(HttpStatusCode.OK, encerrar.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deNovo.StatusCode);
        Assert.False(detalhe.GetProperty("conversa").GetProperty("marcadaRetomadaHumana").GetBoolean());
        Assert.Equal("Cliente atendido pelo celular.", detalhe.GetProperty("conversa").GetProperty("retomadaMotivo").GetString());
        Assert.Equal(supervisor.Id.ToString(), await amb.ConsultarAsync<string>("select retomada_encerrada_por::text from conversas where tenant_id = @tenant"));
        Assert.Equal(1, await amb.ContarAsync("conversas where tenant_id = @tenant and retomada_encerrada_em is not null"));
        Assert.Equal(supervisor.Id.ToString(), await amb.ConsultarAsync<string>("select dados->>'usuario' from eventos_historico where tenant_id = @tenant and tipo = 'retomada_encerrada'"));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-8")]
    public async Task Com_pendencia_de_retomada_a_IA_nao_diz_que_o_assunto_foi_resolvido()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await ConversaComPendenciaAsync(amb);
        await using var api = amb.Api();
        var admin = api.Cliente(amb.ChaveSupervisor);
        var conversa = await amb.ConversaIdAsync();
        await admin.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        await admin.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = await amb.VersaoConversaAsync() });
        await amb.ConhecimentoAsync("pedidos", "Pedidos já foi resolvido quando o status aparece como entregue.");
        await PoliticaComAssuntoAsync(amb, "pedidos");

        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, "Seu pedido já foi resolvido.", "pedidos")));
        var r = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("E o meu pedido?"));

        Assert.Equal(ResultadoProcessamento.Transferida, r);
        Assert.Equal("ResolucaoComPendencia", await amb.ConsultarAsync<string>("select motivo from decisoes_envio where tenant_id = @tenant and tipo = 'resposta' order by criada_em desc limit 1"));
    }

    // ------------------------------------------------------------------ apoio

    private static object Aprovacao(int versao, string[] classificacoes, string escopo = "Empresa", Guid? escopoRef = null, DateTimeOffset? validoAte = null) =>
        new { versaoProposta = versao, permissoes = new { classificacoes, escopoTipo = escopo, escopoRef, validoAte } };

    private static async Task<Guid> SugerirAsync(HttpClient c, string assunto, string conteudo, object? sugeridas = null)
    {
        var r = await c.PostAsJsonAsync("/supervisao/conhecimento", new { assunto, conteudo, sugeridas });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.JsonAsync()).GetProperty("id").GetGuid();
    }

    /// <summary>Cliente padrão do ambiente (criado pela primeira mensagem).</summary>
    private static async Task<Guid> ClienteAsync(AmbientePipeline amb)
    {
        if (await amb.ConsultarAsync<Guid?>("select id from clientes where tenant_id = @tenant") is { } id)
        {
            return id;
        }

        await amb.ConversaAtendidaPelaIaAsync(Horario);
        return (await amb.ConsultarAsync<Guid?>("select id from clientes where tenant_id = @tenant"))!.Value;
    }

    /// <summary>O que o agente recebeu ao responder esta mensagem (o motor só registra; o rascunho é descartado).</summary>
    private static async Task<ContextoModelo> ContextoEntregueAsync(AmbientePipeline amb, string texto, string? waId = null)
    {
        var motor = new MotorFalso(_ => Task.FromResult("{}"));
        var mensagem = new MensagemRecebida(amb.TenantId, waId ?? AmbientePipeline.WaCliente, texto, "wamid.entrada." + Guid.NewGuid(), amb.Relogio.Agora);
        await amb.Orquestrador(motor).ProcessarAsync(mensagem);
        return motor.Contextos.Single();
    }

    /// <summary>Conversa com pendência de retomada: a resposta da IA perdeu a janela de 24h na fila.</summary>
    private static async Task<Guid> ConversaComPendenciaAsync(AmbientePipeline amb)
    {
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        // Mensagem do cliente de 23h59 atrás; quando o worker processa, a janela já fechou (a resposta ainda é válida).
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?", em: amb.Relogio.Agora.AddHours(-23).AddMinutes(-59)));
        amb.Relogio.Agora += TimeSpan.FromMinutes(2);
        await amb.Worker().ProcessarProximoAsync();
        Assert.Equal(1, await amb.ContarAsync("conversas where tenant_id = @tenant and marcada_retomada_humana"));
        // A janela reabre quando o cliente escreve de novo.
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).ProcessarAsync(amb.Mensagem("Oi, ainda estou aguardando."));
        return await amb.ConversaIdAsync();
    }

    private static async Task PoliticaComAssuntoAsync(AmbientePipeline amb, string assunto)
    {
        var atual = await amb.ConsultarAsync<int>("select max(versao) from politicas_tenant where tenant_id = @tenant");
        var r = await amb.Supervisao.PublicarPoliticaAsync(amb.Supervisor, atual,
            JsonSerializer.Serialize(new { assuntosAutonomos = new[] { "horario", assunto }, textoAvisoTransferencia = "Vou encaminhar sua mensagem para nossa equipe." }));
        Assert.Equal(SituacaoOperacao.Ok, r.Situacao);
    }
}
