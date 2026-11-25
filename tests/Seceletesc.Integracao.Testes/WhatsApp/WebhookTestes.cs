using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Seceletesc.Aplicacao.Envio;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.WhatsApp;
using Seceletesc.Integracao.Testes.Pipeline;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.WhatsApp;

/// <summary>
/// F4 (plano técnico, seção 4.8): webhook com assinatura sobre o corpo bruto, idempotência pelo wamid, fila durável
/// da IA, mensagens que não são texto, eco do app (coexistência), status de entrega e gateway real desligado por
/// padrão. Nada aqui fala com a Meta: o gateway real é testado com um HTTP falso.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class WebhookTestes(BancoFixture banco)
{
    private const string Segredo = "segredo-de-teste-do-app";
    private const string TokenVerificacao = "token-de-verificacao-de-teste";
    private const string Horario = SupervisaoApiTestes.Horario;
    private const int EndpointsLimite = Api.WhatsApp.EndpointsWhatsApp.TamanhoMaximoCorpo;

    // ------------------------------------------------------------------ verificação e assinatura

    [Fact]
    [Trait("Regra", "INV-14")]
    public async Task Verificacao_do_webhook_so_devolve_o_desafio_com_o_token_certo()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var c = api.CreateClient();

        var certo = await c.GetAsync(new Uri($"/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={TokenVerificacao}&hub.challenge=12345", UriKind.Relative));
        var errado = await c.GetAsync(new Uri("/whatsapp/webhook?hub.mode=subscribe&hub.verify_token=outro&hub.challenge=12345", UriKind.Relative));
        var refletido = await c.GetAsync(new Uri($"/whatsapp/webhook?hub.mode=subscribe&hub.verify_token={TokenVerificacao}&hub.challenge=%3Cscript%3E", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, certo.StatusCode);
        Assert.Equal("12345", await certo.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, errado.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refletido.StatusCode);
    }

    [Theory]
    [Trait("Regra", "INV-14")]
    [Trait("Regra", "INV-12")]
    [InlineData("sem")]
    [InlineData("outro-segredo")]
    [InlineData("corpo-alterado")]
    public async Task Webhook_sem_assinatura_valida_nao_registra_nada(string caso)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Texto(numero, "wamid.a1", "Qual o horário?", amb.Relogio.Agora);
        var assinatura = caso switch
        {
            "sem" => null,
            "outro-segredo" => AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), "outro"),
            _ => AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo.Replace("Qual", "Quanto", StringComparison.Ordinal)), Segredo),
        };

        var r = await PostarAsync(api, corpo, assinatura);

        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public async Task Sem_app_secret_configurado_o_webhook_falha_fechado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio, appSecret: "");
        var corpo = Texto(numero, "wamid.a2", "Oi", amb.Relogio.Agora);

        var r = await PostarAsync(api, corpo, AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), Segredo));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public async Task Corpo_acima_do_limite_e_recusado_sem_ler_tudo()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Texto(numero, "wamid.grande", new string('a', 100), amb.Relogio.Agora) + new string(' ', EndpointsLimite + 1);

        var r = await PostarAsync(api, corpo, AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), Segredo));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync("mensagens where wamid = 'wamid.grande'"));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public async Task Corpo_sem_tamanho_declarado_acima_do_limite_e_recusado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Encoding.UTF8.GetBytes(Texto(numero, "wamid.blocos", "Oi", amb.Relogio.Agora) + new string(' ', EndpointsLimite + 1));
        using var pedido = new HttpRequestMessage(HttpMethod.Post, new Uri("/whatsapp/webhook", UriKind.Relative)) { Content = new SemTamanho(corpo) };
        pedido.Headers.Add("X-Hub-Signature-256", AssinaturaWebhook.Calcular(corpo, Segredo));

        var r = await api.CreateClient().SendAsync(pedido);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync("mensagens where wamid = 'wamid.blocos'"));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Aplicacao_nao_enfileira_na_fila_da_IA_mensagem_de_outra_empresa()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var (_, mensagemB) = await b.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).RegistrarAsync(b.Mensagem("Oi"));
        var conversaB = await b.ConversaIdAsync();
        await b.Banco.ConsultarComoAdminAsync($"delete from fila_ia where mensagem_id = '{mensagemB}'");

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new Infraestrutura.Banco.ExecutorTenant(a.App).ExecutarAsync(a.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("insert into fila_ia (mensagem_id, tenant_id, conversa_id) values (@m, @t, @c)", c, tx);
            cmd.Parameters.AddWithValue("m", mensagemB!.Value);
            cmd.Parameters.AddWithValue("t", b.TenantId);
            cmd.Parameters.AddWithValue("c", conversaB);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
    }

    // ------------------------------------------------------------------ registro, idempotência e fila da IA

    [Fact]
    [Trait("Regra", "INV-15")]
    [Trait("Regra", "INV-23")]
    [Trait("Regra", "ADV-23")]
    public async Task Mensagem_de_texto_e_registrada_uma_vez_mascarada_e_vai_para_a_fila_da_IA()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Texto(numero, "wamid.b1", "Meu CPF é 529.982.247-25, qual o horário?", amb.Relogio.Agora);
        var assinatura = AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), Segredo);

        var primeira = await PostarAsync(api, corpo, assinatura);
        var reenvio = await PostarAsync(api, corpo, assinatura); // a Meta reenvia o mesmo evento

        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reenvio.StatusCode);
        Assert.Equal(1L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}'"));
        Assert.Equal(1L, await amb.ContarAsync($"fila_ia where tenant_id = '{amb.TenantId}'"));
        Assert.Equal(0L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}' and texto_mascarado like '%529%'"));
        Assert.Equal(1L, await amb.ContarAsync($"dados_protegidos where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-10")]
    public async Task Numero_desconhecido_e_ignorado_sem_gravar_nada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Texto("numero-de-ninguem", "wamid.c1", "Oi", amb.Relogio.Agora);

        var r = await PostarAsync(api, corpo, AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), Segredo));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(0L, await amb.ContarAsync("mensagens where wamid = 'wamid.c1'"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Trabalhador_da_IA_trata_a_mensagem_registrada_pelo_webhook()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        await PostarAssinadoAsync(api, Texto(numero, "wamid.d1", "Qual o horário?", amb.Relogio.Agora));
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));

        var r = await Trabalhador(amb, motor).ProcessarProximoAsync();

        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, r);
        Assert.Equal(0L, await amb.ContarAsync($"fila_ia where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Duas_mensagens_seguidas_so_a_mais_nova_e_respondida()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        await PostarAssinadoAsync(api, Texto(numero, "wamid.e1", "Oi", amb.Relogio.Agora));
        await PostarAssinadoAsync(api, Texto(numero, "wamid.e2", "Qual o horário?", amb.Relogio.Agora.AddSeconds(5)));
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var trabalhador = Trabalhador(amb, motor);

        var primeira = await trabalhador.ProcessarProximoAsync();
        var segunda = await trabalhador.ProcessarProximoAsync();

        Assert.Equal(ResultadoProcessamento.SemAcaoDaIa, primeira);
        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, segunda);
        Assert.Single(motor.Contextos); // a mensagem antiga nem vai ao motor
        Assert.Contains(motor.Contextos[0].Historico, m => m.TextoMascarado == "Oi"); // mas está no histórico da resposta
        Assert.Equal(1L, await amb.ContarAsync($"eventos_historico where tenant_id = '{amb.TenantId}' and tipo = 'mensagem_agrupada'"));
        Assert.Equal(1L, await amb.ContarAsync($"decisoes_envio where tenant_id = '{amb.TenantId}' and tipo = 'resposta'"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Tratar_a_mesma_mensagem_duas_vezes_gera_uma_unica_decisao()
    {
        // Lease vencido com o primeiro trabalhador ainda vivo: os dois geram o rascunho, só um decide.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var orquestrador = amb.Orquestrador(motor);
        var (_, mensagem) = await orquestrador.RegistrarAsync(amb.Mensagem("Qual o horário?"));

        var resultados = await Task.WhenAll(orquestrador.TratarAsync(amb.TenantId, mensagem!.Value), orquestrador.TratarAsync(amb.TenantId, mensagem.Value));

        Assert.Single(resultados, r => r == ResultadoProcessamento.RespostaEnfileirada);
        Assert.Single(resultados, r => r == ResultadoProcessamento.Duplicada);
        Assert.Equal(1L, await amb.ContarAsync($"decisoes_envio where tenant_id = '{amb.TenantId}' and tipo = 'resposta'"));
        Assert.Equal(1L, await amb.ContarAsync($"outbox where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-3")]
    public async Task Mensagem_que_nao_e_texto_vai_para_supervisao_sem_a_IA()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);

        await PostarAssinadoAsync(api, NaoTexto(numero, "wamid.f1", "image", amb.Relogio.Agora));

        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        Assert.Equal(1L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}' and texto_mascarado = '[mensagem não textual: image]'"));
        Assert.Equal(0L, await amb.ContarAsync($"fila_ia where tenant_id = '{amb.TenantId}'"));
    }

    // ------------------------------------------------------------------ eco do app (coexistência) e status

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-5")]
    [Trait("Regra", "INV-23")]
    public async Task Eco_do_app_coloca_a_conversa_sob_controle_humano_e_nao_abre_a_janela()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        var ultimaDoCliente = await amb.ConsultarAsync<DateTime?>("select ultima_mensagem_cliente_em from conversas where tenant_id = @tenant");
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);

        await PostarAssinadoAsync(api, Eco(numero, "wamid.g1", "Pode escrever para maria@cliente.com.br", amb.Relogio.Agora.AddMinutes(1)));
        var ultimaDepoisDoEco = await amb.ConsultarAsync<DateTime?>("select ultima_mensagem_cliente_em from conversas where tenant_id = @tenant");
        var depois = await amb.Orquestrador(new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario"))))
            .ProcessarAsync(amb.Mensagem("Obrigado", em: amb.Relogio.Agora.AddMinutes(2)));

        Assert.Equal("Humano", await amb.EstadoConversaAsync());
        Assert.Equal(ResultadoProcessamento.SemAcaoDaIa, depois);
        Assert.Equal(1L, await amb.ContarAsync($"mensagens where tenant_id = '{amb.TenantId}' and direcao = 'saida_app' and texto_mascarado not like '%maria@%'"));
        Assert.Equal(ultimaDoCliente, ultimaDepoisDoEco); // o eco não abre nem estende a janela de 24h
    }

    [Fact]
    [Trait("Regra", "INV-3")]
    public async Task Falha_de_entrega_de_mensagem_nossa_aciona_a_supervisao_uma_vez()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        var wamid = await amb.ConsultarAsync<string>("select wamid from outbox where tenant_id = @tenant and status = 'enviada'");
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var corpo = Status(numero, wamid!, "failed", amb.Relogio.Agora);

        await PostarAssinadoAsync(api, corpo);
        await PostarAssinadoAsync(api, corpo);

        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        Assert.Equal(1L, await amb.ContarAsync($"status_entrega where tenant_id = '{amb.TenantId}'"));
        Assert.Equal(1L, await amb.ContarAsync($"eventos_historico where tenant_id = '{amb.TenantId}' and tipo = 'entrega_falhou'"));
    }

    // ------------------------------------------------------------------ revisão da F4 (revisoes/2026-11-25-f4.md)

    [Fact]
    [Trait("Regra", "INV-3")]
    [Trait("Regra", "INV-15")]
    public async Task G1_mensagem_antiga_que_chega_depois_da_mais_nova_ja_respondida_vai_para_supervisao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var orquestrador = amb.Orquestrador(motor);
        var nova = await orquestrador.ProcessarAsync(amb.Mensagem("Qual o horário?", em: amb.Relogio.Agora));

        // A Meta entrega fora de ordem: a mensagem enviada antes chega depois da resposta à mais nova.
        var antiga = await orquestrador.ProcessarAsync(amb.Mensagem("Oi, bom dia", em: amb.Relogio.Agora.AddSeconds(-30)));

        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, nova);
        Assert.Equal(ResultadoProcessamento.Transferida, antiga);
        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        Assert.Equal(1L, await amb.ContarAsync($"eventos_historico where tenant_id = '{amb.TenantId}' and tipo = 'transferencia_iniciada' and dados->>'motivo' = 'mensagem_fora_de_ordem'"));
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    [Trait("Regra", "INV-24")]
    public async Task G2_eco_em_conversa_ja_humana_invalida_liberacao_com_a_versao_antiga()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaNaFilaAsync(Horario);
        await amb.AssumirAsync();
        var versaoVista = await amb.VersaoConversaAsync();
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);

        await PostarAssinadoAsync(api, Eco(numero, "wamid.g2", "Já resolvi por aqui.", amb.Relogio.Agora));
        var liberar = await amb.Supervisao.LiberarAsync(amb.Supervisor, await amb.ConversaIdAsync(), versaoVista);

        Assert.Equal(Infraestrutura.Supervisao.SituacaoOperacao.Conflito, liberar.Situacao);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-5")]
    public async Task Eco_cancela_resposta_automatica_que_estava_na_fila()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        Assert.Equal(ResultadoProcessamento.RespostaEnfileirada, await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?")));
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);

        await PostarAssinadoAsync(api, Eco(numero, "wamid.eco-fila", "Atendemos até as 18h.", amb.Relogio.Agora));
        var ciclo = await amb.Worker().ProcessarProximoAsync();

        Assert.Equal(ResultadoCiclo.Cancelada, ciclo);
        Assert.Empty(amb.Gateway.Enviadas);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    [Trait("Regra", "INV-24")]
    public async Task Eco_com_dado_protegido_fica_mascarado_cifrado_e_o_modelo_ve_so_o_marcador()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        await PostarAssinadoAsync(api, Texto(numero, "wamid.h0", "Oi", amb.Relogio.Agora));
        await PostarAssinadoAsync(api, Eco(numero, "wamid.h1", "Seu CPF 529.982.247-25 está ok", amb.Relogio.Agora.AddSeconds(10)));
        await amb.LiberarAsync();
        var motor = new MotorFalso(_ => Task.FromResult("{}"));

        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Obrigado", em: amb.Relogio.Agora.AddMinutes(1)));

        Assert.Equal(1L, await amb.ContarAsync($"dados_protegidos d join mensagens m on m.id = d.mensagem_id where m.tenant_id = '{amb.TenantId}' and m.direcao = 'saida_app'"));
        Assert.DoesNotContain(motor.Contextos.Single().Historico, m => m.TextoMascarado.Contains("529", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-3")]
    public async Task S1_evento_invalido_no_lote_nao_impede_o_eco_de_silenciar_a_IA()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var lote = JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = "waba",
                    changes = new object[]
                    {
                        new { field = "messages", value = new { metadata = new { phone_number_id = numero },
                            messages = new[] { new { from = "nao-e-telefone", id = "wamid.ruim", timestamp = Ts(amb.Relogio.Agora), type = "text", text = new { body = "x" } } } } },
                        new { field = "smb_message_echoes", value = new { metadata = new { phone_number_id = numero },
                            message_echoes = new[] { new { from = numero, to = AmbientePipeline.WaCliente, id = "wamid.eco-lote", timestamp = Ts(amb.Relogio.Agora), type = "text", text = new { body = "Oi" } } } } },
                    },
                },
            },
        });

        var r = await PostarAssinadoAsync(api, lote);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
        Assert.Equal(0L, await amb.ContarAsync("mensagens where wamid = 'wamid.ruim'"));
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-5")]
    public async Task S1_no_mesmo_lote_o_eco_vale_antes_da_mensagem_do_cliente()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConversaAtendidaPelaIaAsync(Horario);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio);
        var lote = JsonSerializer.Serialize(new
        {
            @object = "whatsapp_business_account",
            entry = new[]
            {
                new
                {
                    id = "waba",
                    changes = new object[]
                    {
                        new { field = "messages", value = new { metadata = new { phone_number_id = numero },
                            messages = new[] { new { from = AmbientePipeline.WaCliente, id = "wamid.cli-lote", timestamp = Ts(amb.Relogio.Agora.AddSeconds(5)), type = "text", text = new { body = "E aos sábados?" } } } } },
                        new { field = "smb_message_echoes", value = new { metadata = new { phone_number_id = numero },
                            message_echoes = new[] { new { from = numero, to = AmbientePipeline.WaCliente, id = "wamid.eco-antes", timestamp = Ts(amb.Relogio.Agora), type = "text", text = new { body = "Já te respondo" } } } } },
                    },
                },
            },
        });

        await PostarAssinadoAsync(api, lote);

        // O humano já respondeu pelo celular: a mensagem seguinte do cliente nem entra na fila da IA.
        Assert.Equal("Humano", await amb.EstadoConversaAsync());
        Assert.Equal(0L, await amb.ContarAsync($"fila_ia where tenant_id = '{amb.TenantId}'"));
    }

    [Theory]
    [Trait("Regra", "INV-10")]
    [InlineData("select count(*) from fila_ia")]
    [InlineData("delete from fila_ia")]
    public async Task S2_aplicacao_nao_ve_nem_apaga_a_fila_da_IA_de_outra_empresa(string sql)
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        await b.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).RegistrarAsync(b.Mensagem("Oi"));

        var r = await new Infraestrutura.Banco.ExecutorTenant(a.App).ExecutarAsync(a.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return sql.StartsWith("select", StringComparison.Ordinal) ? (long)(await cmd.ExecuteScalarAsync(ct))! : await cmd.ExecuteNonQueryAsync(ct);
        });

        Assert.Equal(0L, r);
        Assert.Equal(1L, await b.ContarAsync($"fila_ia where tenant_id = '{b.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    [Trait("Regra", "INV-6")]
    public async Task S3_gateway_nao_envia_por_numero_que_nao_e_da_empresa()
    {
        var tenant = Guid.NewGuid();
        var http = new HttpFalso(HttpStatusCode.OK, """{"messages":[{"id":"wamid.x"}]}""");
        var gateway = new GatewayWhatsAppCloud(new HttpClient(http),
            new OpcoesWhatsApp { EnvioHabilitado = true, TokenAcesso = "token", Numeros = { [tenant] = "numero-de-outra-empresa" } },
            (_, _, _) => Task.FromResult(false));

        var r = await gateway.EnviarTextoAsync(tenant, "5511999990000", "Olá", CancellationToken.None);

        Assert.Equal(SituacaoEnvio.FalhaDefinitiva, r.Situacao);
        Assert.Null(http.Uri);
    }

    [Fact]
    [Trait("Regra", "INV-3")]
    public async Task S4_com_o_processamento_desligado_a_mensagem_vai_direto_para_supervisao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var numero = await NumeroAsync(amb);
        await using var api = new ApiWhatsApp(banco, amb.Relogio, processamento: false);

        await PostarAssinadoAsync(api, Texto(numero, "wamid.s4", "Qual o horário?", amb.Relogio.Agora));

        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        Assert.Equal(0L, await amb.ContarAsync($"fila_ia where tenant_id = '{amb.TenantId}'"));
    }

    [Fact]
    [Trait("Regra", "INV-3")]
    [Trait("Regra", "CTX-001")]
    public async Task S4_mensagem_parada_na_fila_alem_do_limite_nao_e_respondida_pela_IA()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var orquestrador = amb.Orquestrador(motor);
        var (_, mensagem) = await orquestrador.RegistrarAsync(amb.Mensagem("Qual o horário?"));
        amb.Relogio.Agora += ProcessadorMensagemRecebida.IdadeMaximaNaFilaPadrao + TimeSpan.FromMinutes(1);

        var r = await orquestrador.TratarAsync(amb.TenantId, mensagem!.Value);

        Assert.Equal(ResultadoProcessamento.Transferida, r);
        Assert.Empty(motor.Contextos);
    }

    [Fact]
    [Trait("Regra", "INV-3")]
    public async Task S5_tentativas_esgotadas_vao_para_supervisao_sem_nova_geracao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await amb.ConhecimentoAsync("horario", Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, Horario, "horario")));
        var orquestrador = amb.Orquestrador(motor);
        var (_, mensagem) = await orquestrador.RegistrarAsync(amb.Mensagem("Qual o horário?"));
        await amb.Banco.ConsultarComoAdminAsync($"update fila_ia set tentativas = {ProcessadorMensagemRecebida.MaximoTentativas + 1} where mensagem_id = '{mensagem}'");

        var r = await orquestrador.TratarAsync(amb.TenantId, mensagem!.Value);

        Assert.Equal(ResultadoProcessamento.Transferida, r);
        Assert.Empty(motor.Contextos);
    }

    [Fact]
    [Trait("Regra", "INV-24")]
    [Trait("Regra", "INV-15")]
    public async Task S8_aplicacao_nao_registra_eco_que_nao_existe()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var conversa = await amb.ConversaAtendidaPelaIaAsync(Horario);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new Infraestrutura.Banco.ExecutorTenant(amb.App).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("select registrar_eco_humano(@c, 'wamid.inventado')", c, tx);
            cmd.Parameters.AddWithValue("c", conversa);
            return await cmd.ExecuteScalarAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
        Assert.Equal(0L, await amb.ContarAsync($"eventos_historico where tenant_id = '{amb.TenantId}' and tipo = 'eco_app_humano'"));
    }

    // ------------------------------------------------------------------ gateway real (sem rede)

    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData(200, """{"messages":[{"id":"wamid.ok"}]}""", SituacaoEnvio.Enviada)]
    [InlineData(200, """{}""", SituacaoEnvio.Incerta)]
    [InlineData(429, """{}""", SituacaoEnvio.NaoEnviada)]
    [InlineData(400, """{"error":{"code":131047}}""", SituacaoEnvio.FalhaDefinitiva)]
    [InlineData(500, """{}""", SituacaoEnvio.Incerta)]
    public async Task Gateway_real_classifica_o_resultado_sem_repetir_o_que_pode_ter_saido(int status, string resposta, SituacaoEnvio esperada)
    {
        var tenant = Guid.NewGuid();
        var http = new HttpFalso((HttpStatusCode)status, resposta);
        var gateway = new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = true, TokenAcesso = "token", Numeros = { [tenant] = "123" } }, (_, _, _) => Task.FromResult(true));

        var r = await gateway.EnviarTextoAsync(tenant, "5511999990000", "Olá", CancellationToken.None);

        Assert.Equal(esperada, r.Situacao);
        using var pedido = JsonDocument.Parse(http.Corpo!);
        Assert.Equal("text", pedido.RootElement.GetProperty("type").GetString());
        Assert.False(pedido.RootElement.GetProperty("text").GetProperty("preview_url").GetBoolean());
        Assert.False(pedido.RootElement.TryGetProperty("interactive", out _));
        Assert.False(pedido.RootElement.TryGetProperty("template", out _));
        Assert.EndsWith("/123/messages", http.Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData(false, "token", true)]
    [InlineData(true, "", true)]
    [InlineData(true, "token", false)]
    public async Task Gateway_real_desligado_ou_sem_configuracao_nao_chama_a_Meta(bool habilitado, string token, bool comNumero)
    {
        var tenant = Guid.NewGuid();
        var http = new HttpFalso(HttpStatusCode.OK, """{"messages":[{"id":"wamid.x"}]}""");
        var opcoes = new OpcoesWhatsApp { EnvioHabilitado = habilitado, TokenAcesso = token };
        if (comNumero)
        {
            opcoes.Numeros[tenant] = "123";
        }

        var r = await new GatewayWhatsAppCloud(new HttpClient(http), opcoes, (_, _, _) => Task.FromResult(true)).EnviarTextoAsync(tenant, "5511999990000", "Olá", CancellationToken.None);

        Assert.Equal(SituacaoEnvio.FalhaDefinitiva, r.Situacao);
        Assert.Null(http.Uri);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public void Por_padrao_a_API_nao_registra_nenhum_trabalhador_de_envio()
    {
        using var api = new ApiWhatsApp(banco, TimeProvider.System);

        Assert.Null(api.Services.GetService<ProcessadorEnvio>());
        Assert.Null(api.Services.GetService<IGatewayWhatsApp>());
    }

    // ------------------------------------------------------------------ auxiliares

    private static TrabalhadorIa Trabalhador(AmbientePipeline amb, MotorFalso motor) => new(amb.App, amb.Orquestrador(motor));

    private static async Task<string> NumeroAsync(AmbientePipeline amb)
    {
        var numero = "pnid-" + Guid.NewGuid().ToString("N");
        await using var c = new NpgsqlConnection(amb.Banco.ConnectionStringAdmin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("insert into tenant_numeros_whatsapp (phone_number_id, tenant_id) values (@p, @t)", c);
        cmd.Parameters.AddWithValue("p", numero);
        cmd.Parameters.AddWithValue("t", amb.TenantId);
        await cmd.ExecuteNonQueryAsync();
        return numero;
    }

    private static Task<HttpResponseMessage> PostarAssinadoAsync(ApiWhatsApp api, string corpo) =>
        PostarAsync(api, corpo, AssinaturaWebhook.Calcular(Encoding.UTF8.GetBytes(corpo), Segredo));

    private static async Task<HttpResponseMessage> PostarAsync(ApiWhatsApp api, string corpo, string? assinatura)
    {
        using var pedido = new HttpRequestMessage(HttpMethod.Post, new Uri("/whatsapp/webhook", UriKind.Relative))
        {
            Content = new StringContent(corpo, Encoding.UTF8, "application/json"),
        };
        if (assinatura is not null)
        {
            pedido.Headers.Add("X-Hub-Signature-256", assinatura);
        }

        var r = await api.CreateClient().SendAsync(pedido);
        return r;
    }

    private static string Envelope(string numero, string campo, object valor) => JsonSerializer.Serialize(new
    {
        @object = "whatsapp_business_account",
        entry = new[] { new { id = "waba", changes = new[] { new { field = campo, value = valor } } } },
    });

    private static string Ts(DateTimeOffset em) => em.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Texto(string numero, string wamid, string texto, DateTimeOffset em) => Envelope(numero, "messages", new
    {
        messaging_product = "whatsapp",
        metadata = new { phone_number_id = numero },
        messages = new[] { new { from = AmbientePipeline.WaCliente, id = wamid, timestamp = Ts(em), type = "text", text = new { body = texto } } },
    });

    private static string NaoTexto(string numero, string wamid, string tipo, DateTimeOffset em) => Envelope(numero, "messages", new
    {
        metadata = new { phone_number_id = numero },
        messages = new[] { new { from = AmbientePipeline.WaCliente, id = wamid, timestamp = Ts(em), type = tipo } },
    });

    private static string Eco(string numero, string wamid, string texto, DateTimeOffset em) => Envelope(numero, "smb_message_echoes", new
    {
        metadata = new { phone_number_id = numero },
        message_echoes = new[] { new { from = numero, to = AmbientePipeline.WaCliente, id = wamid, timestamp = Ts(em), type = "text", text = new { body = texto } } },
    });

    private static string Status(string numero, string wamid, string status, DateTimeOffset em) => Envelope(numero, "messages", new
    {
        metadata = new { phone_number_id = numero },
        statuses = new[] { new { id = wamid, status, timestamp = Ts(em), recipient_id = AmbientePipeline.WaCliente, errors = new[] { new { code = 131026 } } } },
    });

    /// <summary>A API real com os papéis da aplicação e o webhook configurado (sem token de envio: nada sai).</summary>
    private sealed class ApiWhatsApp(BancoFixture banco, TimeProvider relogio, string appSecret = Segredo, bool processamento = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Processamento ligado (a mensagem vai para a fila da IA), mas sem o trabalhador em segundo plano: os testes
            // tratam a fila quando querem.
            builder.UseSetting("WhatsApp:ProcessamentoHabilitado", processamento ? "true" : "false");
            builder.ConfigureTestServices(s => s.RemoveAll<Microsoft.Extensions.Hosting.IHostedService>());
            builder.UseSetting("ConnectionStrings:Supervisao", banco.ConnectionString("supervisao_teste"));
            builder.UseSetting("ConnectionStrings:App", banco.ConnectionString("app_teste"));
            builder.UseSetting("ConnectionStrings:Worker", banco.ConnectionString("worker_teste"));
            builder.UseSetting("WhatsApp:AppSecret", appSecret);
            builder.UseSetting("WhatsApp:TokenVerificacao", TokenVerificacao);
            builder.UseSetting("DadosProtegidos:ChavePublicaPem", AmbientePipeline.ChavePublicaPem);
            builder.ConfigureTestServices(s => s.AddSingleton(relogio));
        }
    }

    /// <summary>Corpo enviado em blocos, sem Content-Length.</summary>
    private sealed class SemTamanho(byte[] dados) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(dados).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class HttpFalso(HttpStatusCode status, string resposta) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Corpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Corpo = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(resposta, Encoding.UTF8, "application/json") };
        }
    }
}
