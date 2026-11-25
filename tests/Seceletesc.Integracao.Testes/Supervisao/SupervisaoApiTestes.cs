using System.Net;
using System.Net.Http.Json;
using System.Text;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Integracao.Testes.Pipeline;

namespace Seceletesc.Integracao.Testes.Supervisao;

/// <summary>
/// API de supervisão de ponta a ponta: HTTP real em memória, papel seceletesc_supervisao, PostgreSQL real.
/// Verifica o que ficou no banco e o que o worker enviou, não só o código HTTP.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class SupervisaoApiTestes(BancoFixture banco)
{
    internal const string Horario = "Atendemos de segunda a sexta, das 9h às 18h.";
    private const string Precos = "A consulta custa R$ 150,00.";

    // ------------------------------------------------------------------ autenticação

    [Theory]
    [InlineData(null)]
    [InlineData("sec_chave-que-nao-existe")]
    [InlineData("")]
    public async Task Sem_chave_valida_a_supervisao_responde_401(string? chave)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();

        var r = await api.Cliente(chave).GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Chave_enviada_fora_do_esquema_bearer_nao_autentica()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var c = api.Cliente(null);
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", amb.ChaveSupervisor);

        var r = await c.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Usuario_criado_pelo_comando_autentica_e_depois_de_desativado_nao_autentica_mais()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var tenant = await AdministracaoSupervisao.CriarTenantAsync(banco.ConnectionStringDono, "Empresa criada pelo comando");
        var (usuario, chave) = await AdministracaoSupervisao.CriarSupervisorAsync(banco.ConnectionStringDono, tenant, "Recepção");

        var antes = await api.Cliente(chave).GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));
        Assert.True(await AdministracaoSupervisao.DesativarSupervisorAsync(banco.ConnectionStringDono, usuario));
        var depois = await api.Cliente(chave).GetAsync(new Uri("/supervisao/conversas", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, antes.StatusCode);
        Assert.Equal(0, (await antes.JsonAsync()).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, depois.StatusCode);
        // Só o hash da chave fica no banco.
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from usuarios_supervisao where chave_sha256 = '{chave}'"));
        Assert.Equal(1L, await banco.ConsultarComoAdminAsync($"select count(*) from usuarios_supervisao where chave_sha256 = '{ServicoSupervisao.HashChave(chave)}'"));
    }

    [Theory]
    [InlineData("POST", "/supervisao/conversas/00000000-0000-0000-0000-000000000001/responder", "{ isto não é json")]
    [InlineData("PUT", "/supervisao/politica", "{\"versaoEsperada\": \"x\"}")]
    public async Task Sem_chave_o_corpo_nem_e_lido_e_a_resposta_e_401(string metodo, string rota, string corpo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        using var pedido = new HttpRequestMessage(new HttpMethod(metodo), new Uri(rota, UriKind.Relative))
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };

        var r = await api.Cliente(null).SendAsync(pedido);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Corpo_acima_do_limite_e_recusado_antes_de_autenticar()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var corpo = new StringContent(new string('x', (int)Seceletesc.Api.Supervisao.EndpointsSupervisao.TamanhoMaximoCorpo + 1), Encoding.UTF8, "application/json");

        var r = await api.Cliente(amb.ChaveSupervisor).PutAsync(new Uri("/supervisao/politica", UriKind.Relative), corpo);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
    }

    // ------------------------------------------------------------------ isolamento (IDOR)

    [Fact]
    [Trait("Regra", "ADV-22")]
    [Trait("Regra", "INV-10")]
    public async Task Usuario_de_outra_empresa_nao_ve_nem_altera_conversa_pelo_id()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var conversaA = await a.ConversaAtendidaPelaIaAsync();
        await using var api = a.Api();
        var intruso = api.Cliente(b.ChaveSupervisor);

        var respostas = new[]
        {
            await intruso.GetAsync(new Uri($"/supervisao/conversas/{conversaA}", UriKind.Relative)),
            await intruso.PostAsync(new Uri($"/supervisao/conversas/{conversaA}/assumir", UriKind.Relative), null),
            await intruso.PostAsJsonAsync($"/supervisao/conversas/{conversaA}/liberar", new { versaoEsperada = 1 }),
            await intruso.PostAsJsonAsync($"/supervisao/conversas/{conversaA}/responder", new { texto = "Olá" }),
            await intruso.PostAsJsonAsync($"/supervisao/conversas/{conversaA}/orientar", new { texto = "Faça assim", assunto = "horario" }),
        };
        var lista = await (await intruso.GetAsync(new Uri("/supervisao/conversas", UriKind.Relative))).JsonAsync();

        Assert.All(respostas, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal(0, lista.GetArrayLength());
        Assert.Equal("Automatico", await a.EstadoConversaAsync());
        Assert.Equal(1L, await a.VersaoConversaAsync());
        Assert.Equal(0, await a.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null"));
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync("select count(*) from itens_conhecimento where status = 'Rev' and assunto = 'horario' and tenant_id in ('" + a.TenantId + "','" + b.TenantId + "')"));
        Assert.Equal(0L, await banco.ConsultarComoAdminAsync($"select count(*) from eventos_historico where conversa_id = '{conversaA}' and tipo like 'conversa_%'"));
    }

    [Fact]
    [Trait("Regra", "ADV-22")]
    [Trait("Regra", "INV-10")]
    public async Task Listagem_e_detalhe_mostram_so_a_propria_empresa()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var conversaA = await a.ConversaAtendidaPelaIaAsync();
        var conversaB = await b.ConversaAtendidaPelaIaAsync();
        await using var api = a.Api();

        var lista = await (await api.Cliente(a.ChaveSupervisor).GetAsync(new Uri("/supervisao/conversas", UriKind.Relative))).JsonAsync();
        var detalhe = await (await api.Cliente(a.ChaveSupervisor).GetAsync(new Uri($"/supervisao/conversas/{conversaA}", UriKind.Relative))).JsonAsync();

        Assert.Equal([conversaA], lista.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()));
        Assert.NotEqual(conversaA, conversaB);
        Assert.Equal("Automatico", detalhe.GetProperty("conversa").GetProperty("estado").GetString());
        Assert.Equal(["entrada", "saida"], detalhe.GetProperty("mensagens").EnumerateArray().Select(m => m.GetProperty("direcao").GetString()));
    }

    // ------------------------------------------------------------------ assumir e liberar

    [Fact]
    [Trait("Regra", "INV-5")]
    [Trait("Regra", "CTX-011")]
    public async Task Assumir_pela_api_silencia_a_IA_e_o_worker_cancela_a_resposta_pendente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));
        await using var api = amb.Api();

        var r = await api.Cliente(amb.ChaveSupervisor).PostAsync(new Uri($"/supervisao/conversas/{await amb.ConversaIdAsync()}/assumir", UriKind.Relative), null);
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.JsonAsync()).GetProperty("versao").GetInt64());
        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Empty(amb.Gateway.Enviadas);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
        Assert.Equal(amb.Supervisor.Id.ToString(), await amb.ConsultarAsync<string>("select dados->>'usuario' from eventos_historico where tenant_id = @tenant and tipo = 'conversa_assumida'"));
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public async Task Liberar_exige_controle_humano_e_a_versao_que_o_humano_viu()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        var semAssumir = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 1 });
        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var versaoAntiga = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 1 });
        var estadoAposRecusa = await amb.EstadoConversaAsync();
        var ok = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 2 });

        Assert.Equal(HttpStatusCode.Conflict, semAssumir.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, versaoAntiga.StatusCode);
        Assert.Equal(2, (await versaoAntiga.JsonAsync()).GetProperty("versaoAtual").GetInt64());
        Assert.Equal("Humano", estadoAposRecusa);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
        Assert.Equal(1, await amb.ContarAsync("conversas where tenant_id = @tenant and liberada_em is not null"));
        Assert.Equal(1, await amb.ContarAsync("eventos_historico where tenant_id = @tenant and tipo = 'conversa_liberada'"));
    }

    // ------------------------------------------------------------------ responder (invariante 4)

    [Fact]
    [Trait("Regra", "INV-4")]
    public async Task Resposta_humana_sai_pelo_mesmo_numero_com_autor_e_fica_no_historico()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);
        const string texto = "Olá, aqui é a Ana, da recepção. Seu horário de amanhã está confirmado.";

        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var r = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto });
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(ResultadoCiclo.Enviada, ciclo);
        Assert.Equal((AmbientePipeline.WaCliente, texto), amb.Gateway.Enviadas[^1]);
        Assert.Equal(1, await amb.ContarAsync($"mensagens where tenant_id = @tenant and direcao = 'saida' and texto_mascarado = '{texto}'"));
        Assert.Equal(1, await amb.ContarAsync($"outbox where tenant_id = @tenant and autor_id = '{amb.Supervisor.Id}' and status = 'enviada' and decisao_id is null"));
        Assert.Equal(amb.Supervisor.Id.ToString(), await amb.ConsultarAsync<string>("select dados->>'autor' from eventos_historico where tenant_id = @tenant and tipo = 'mensagem_enviada' order by id desc limit 1"));
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    [Trait("Regra", "INV-4")]
    public async Task Responder_sem_assumir_a_conversa_e_recusado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();

        var r = await api.Cliente(amb.ChaveSupervisor).PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Olá" });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(0, await amb.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null"));
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public async Task Liberacao_espera_a_resposta_humana_sair_para_a_IA_nao_responder_fora_de_ordem()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Pronto, resolvido." });
        var cedo = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 2 });
        var ciclo = await amb.Worker().ProcessarProximoAsync();
        var depois = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 2 });

        Assert.Equal(HttpStatusCode.Conflict, cedo.StatusCode);
        Assert.Equal(ResultadoCiclo.Enviada, ciclo);
        Assert.Equal("Pronto, resolvido.", amb.Gateway.Enviadas[^1].Texto);
        Assert.Equal(HttpStatusCode.OK, depois.StatusCode);
        Assert.Equal("Automatico", await amb.EstadoConversaAsync());
    }

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("Seu CPF 529.982.247-25 foi registrado.")]
    [InlineData("Mande para financeiro@empresa.com.br")]
    public async Task Resposta_humana_com_dado_protegido_e_recusada_e_nada_fica_guardado(string texto)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        var r = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(0, await amb.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null"));
        Assert.DoesNotContain("529.982", await r.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public async Task Orientacao_com_dado_protegido_no_texto_ou_no_assunto_e_recusada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        var noTexto = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar", new { texto = "O CPF dele é 529.982.247-25", assunto = "cadastro" });
        var noAssunto = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar", new { texto = "Confirmar cadastro", assunto = "cpf 529.982.247-25" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noTexto.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noAssunto.StatusCode);
        Assert.Equal(0, await amb.ContarAsync("itens_conhecimento where tenant_id = @tenant and status = 'Rev'"));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    public async Task Fora_da_janela_de_24h_a_resposta_humana_e_recusada_pela_api()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);
        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);

        amb.Relogio.Agora += TimeSpan.FromHours(25);
        var r = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Olá, ainda precisa de ajuda?" });

        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal(0, await amb.ContarAsync("outbox where tenant_id = @tenant and autor_id is not null"));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    public async Task Resposta_humana_que_perde_a_janela_na_fila_nao_sai_e_marca_retomada_humana()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);
        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);

        amb.Relogio.Agora += TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1);
        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Olá, ainda precisa de ajuda?" });
        amb.Relogio.Agora += TimeSpan.FromMinutes(2);
        var enviadasAntes = amb.Gateway.Enviadas.Count;
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Equal(enviadasAntes, amb.Gateway.Enviadas.Count);
        Assert.Equal(1, await amb.ContarAsync("conversas where tenant_id = @tenant and marcada_retomada_humana"));
    }

    [Fact]
    public async Task Resposta_humana_que_vence_na_fila_nao_e_enviada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);
        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Já verifico." });

        amb.Relogio.Agora += ServicoSupervisao.ValidadeRespostaHumana;
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Equal("mensagem humana vencida", await amb.ConsultarAsync<string>("select motivo from outbox where tenant_id = @tenant and autor_id is not null"));
    }

    // ------------------------------------------------------------------ orientar (invariante 4, decisão 6a) e exceção (ADV-11)

    [Fact]
    [Trait("Regra", "INV-4")]
    [Trait("Regra", "INV-16")]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "INV-5")]
    public async Task Orientacao_interna_vira_sugestao_REV_nao_muda_o_estado_e_nao_chega_ao_modelo()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);
        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);

        var r = await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/orientar",
            new { texto = "Neste sábado, excepcionalmente, atendemos das 8h às 12h.", assunto = "horario" });
        var item = (await r.JsonAsync()).GetProperty("id").GetGuid();
        var estadoAposOrientar = await amb.EstadoConversaAsync();

        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 2 });
        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, "Sábado atendemos das 8h às 12h.", "horario")));
        var resultado = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("E no sábado?"));

        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("Humano", estadoAposOrientar); // decisão 5: a IA segue muda até a liberação
        Assert.Equal("Rev", await amb.ConsultarAsync<string>($"select status from itens_conhecimento where id = '{item}'"));
        Assert.Equal($"Atendimento:{conversa}:0", await amb.ConsultarAsync<string>(
            $"select escopo_tipo || ':' || escopo_ref || ':' || cardinality(classificacoes) from versoes_conhecimento where item_id = '{item}'"));
        Assert.DoesNotContain(motor.Contextos.Single().Conhecimentos, k => k.ItemId == item);
        Assert.Equal(ResultadoProcessamento.Transferida, resultado); // sem fonte para "8h às 12h"
    }

    [Fact]
    [Trait("Regra", "ADV-11")]
    [Trait("Regra", "INV-18")]
    public async Task Excecao_dada_em_resposta_humana_nao_vira_conhecimento_nem_e_repetida_pela_IA()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("precos", Precos);
        var conversa = await amb.ConversaAtendidaPelaIaAsync();
        var conhecimentosAntes = await amb.ContarAsync("itens_conhecimento where tenant_id = @tenant");
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        await c.PostAsync(new Uri($"/supervisao/conversas/{conversa}/assumir", UriKind.Relative), null);
        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/responder", new { texto = "Para você, faço 30% de desconto na consulta." });
        await amb.Worker().ProcessarProximoAsync();
        await c.PostAsJsonAsync($"/supervisao/conversas/{conversa}/liberar", new { versaoEsperada = 2 });
        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        var enviadasAntes = amb.Gateway.Enviadas.Count;

        // O histórico desta conversa chega ao modelo, e ele tenta repetir a exceção.
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, "Você tem 30% de desconto na consulta.", "precos")));
        var resultado = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("E aquele desconto?"));
        await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(conhecimentosAntes, await amb.ContarAsync("itens_conhecimento where tenant_id = @tenant"));
        Assert.Equal(ResultadoProcessamento.Transferida, resultado);
        Assert.Equal(1, await amb.ContarAsync("decisoes_envio where tenant_id = @tenant and tipo = 'resposta' and resultado = 'bloqueada'"));
        Assert.DoesNotContain(amb.Gateway.Enviadas.Skip(enviadasAntes), e => e.Texto.Contains("30%", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ política

    [Fact]
    [Trait("Regra", "INV-9")]
    public async Task Politica_publicada_pela_api_vale_no_pipeline_a_partir_da_proxima_mensagem()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("precos", Precos);
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        var atual = await (await c.GetAsync(new Uri("/supervisao/politica", UriKind.Relative))).JsonAsync();
        var r = await c.PutAsJsonAsync("/supervisao/politica", new
        {
            versaoEsperada = 1,
            conteudo = new
            {
                assuntosAutonomos = new[] { "horario" },
                assuntosExigemHumano = new[] { "desconto", "precos" },
                classificacoesPermitidas = new[] { "Publico" },
                textoAvisoTransferencia = "Vou encaminhar sua mensagem para nossa equipe.",
            },
        });
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Precos, "precos")));
        var resultado = await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Quanto custa a consulta?"));

        Assert.Equal(1, atual.GetProperty("versao").GetInt32());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(2, (await r.JsonAsync()).GetProperty("versao").GetInt64());
        Assert.Equal(ResultadoProcessamento.Transferida, resultado);
        Assert.Equal(2, await amb.ConsultarAsync<int>("select politica_versao from decisoes_envio where tenant_id = @tenant and tipo = 'resposta'"));
        Assert.Equal(amb.Supervisor.Id.ToString(), await amb.ConsultarAsync<string>("select dados->>'usuario' from eventos_historico where tenant_id = @tenant and tipo = 'politica_publicada'"));
    }

    [Theory]
    [Trait("Regra", "INV-11")]
    [InlineData("""{"assuntosAutonomos":["horario"],"classificacoesPermitidas":["99"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"classificacoesPermitidas":["Publico, Interno"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"classificacoesPermitidas":["publico"]}""")]
    public Task Classificacao_desconhecida_ou_combinada_na_politica_e_recusada(string conteudo) => PoliticaRecusadaAsync(conteudo);

    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData("""{"assuntosAutonomos":["horario"],"textoAvisoTransferencia":"Retornamos em 10 minutos."}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"textoAvisoTransferencia":"Escreva para contato@empresa.com.br"}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"textoAvisoTransferencia":"Me chamo Ana, já te atendo."}""")]
    public Task Aviso_de_transferencia_que_nao_passa_na_validacao_do_envio_e_recusado(string conteudo) => PoliticaRecusadaAsync(conteudo);

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("""{"assuntosAutonomos":["horario", "cpf 529.982.247-25"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"termosCompromisso":["garantido", "financeiro@empresa.com.br"]}""")]
    public Task Politica_com_dado_protegido_e_recusada(string conteudo) => PoliticaRecusadaAsync(conteudo);

    [Theory]
    [InlineData("""{"assuntosAutonomos":["horario"],"assuntosExigemHumano":["horario"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"dominiosPermitidos":["com"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"dominiosPermitidos":[".br"]}""")]
    [InlineData("""{"assuntosAutonomos":["horario", ""]}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"validadeMensagemAutomaticaSegundos":5}""")]
    [InlineData("""{"assuntosAutonomos":["horario"],"termosCompromisso":["garantido", " "]}""")]
    [InlineData("""["horario"]""")]
    public Task Politica_invalida_e_recusada_e_a_vigente_nao_muda(string conteudo) => PoliticaRecusadaAsync(conteudo);

    private async Task PoliticaRecusadaAsync(string conteudo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var corpo = new StringContent($$"""{"versaoEsperada":1,"conteudo":{{conteudo}}}""", Encoding.UTF8, "application/json");

        var r = await api.Cliente(amb.ChaveSupervisor).PutAsync(new Uri("/supervisao/politica", UriKind.Relative), corpo);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode);
        Assert.Equal(1, await amb.ConsultarAsync<int>("select max(versao) from politicas_tenant where tenant_id = @tenant"));
    }

    [Fact]
    [Trait("Regra", "INV-11")]
    public async Task Politica_com_versao_desatualizada_e_recusada_e_campos_desconhecidos_sao_descartados()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        var desatualizada = await c.PutAsJsonAsync("/supervisao/politica", new { versaoEsperada = 0, conteudo = new { assuntosAutonomos = new[] { "horario" } } });
        var ok = await c.PutAsJsonAsync("/supervisao/politica", new { versaoEsperada = 1, conteudo = new { assuntosAutonomos = new[] { "horario" }, permitirTudo = true } });
        var gravada = await (await c.GetAsync(new Uri("/supervisao/politica", UriKind.Relative))).JsonAsync();

        Assert.Equal(HttpStatusCode.Conflict, desatualizada.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(gravada.GetProperty("conteudo").TryGetProperty("permitirTudo", out _));
        // Campos ausentes assumem o padrão conservador: só PUB em resposta automática.
        Assert.Equal(["Publico"], gravada.GetProperty("conteudo").GetProperty("classificacoesPermitidas").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Empresa_sem_politica_publica_a_primeira_versao_pela_api()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco, p => p.SemPolitica = true);
        await using var api = amb.Api();
        var c = api.Cliente(amb.ChaveSupervisor);

        var antes = await c.GetAsync(new Uri("/supervisao/politica", UriKind.Relative));
        var r = await c.PutAsJsonAsync("/supervisao/politica", new { versaoEsperada = 0, conteudo = new { assuntosAutonomos = new[] { "horario" } } });

        Assert.Equal(HttpStatusCode.NotFound, antes.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(1, await amb.ConsultarAsync<int>("select max(versao) from politicas_tenant where tenant_id = @tenant"));
    }
}
