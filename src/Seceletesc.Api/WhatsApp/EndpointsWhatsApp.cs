using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;
using Seceletesc.Infraestrutura.Criptografia;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.WhatsApp;

namespace Seceletesc.Api.WhatsApp;

/// <summary>
/// Webhook da WhatsApp Cloud API (plano técnico, seção 4.8). A assinatura é conferida sobre o corpo BRUTO antes de
/// qualquer leitura do conteúdo. O webhook só registra (idempotente pelo wamid) e responde; a IA trata depois, pela
/// fila durável. Erro ao registrar responde 500 para a Meta reenviar (sem efeito duplicado).
/// Nenhuma rota daqui envia mensagem ao cliente.
/// </summary>
public static class EndpointsWhatsApp
{
    public const string ChaveDataSourceApp = "app";
    public const string ChaveDataSourceWorker = "worker";

    /// <summary>Limite do corpo do webhook. Um lote da Meta fica bem abaixo disso.</summary>
    public const int TamanhoMaximoCorpo = 1024 * 1024;

    private static readonly Action<ILogger, string, string?, Exception?> LogRecusado =
        LoggerMessage.Define<string, string?>(LogLevel.Warning, new EventId(2001, "WebhookRecusado"), "Webhook do WhatsApp recusado: {Motivo} (ip {Ip})");

    private static readonly Action<ILogger, Exception?> LogSemChave =
        LoggerMessage.Define(LogLevel.Error, new EventId(2003, "EnvioSemChave"),
            "WhatsApp:EnvioHabilitado está ligado, mas WhatsApp:ChaveCredenciais está ausente ou inválida: nenhuma mensagem sai; cada uma é cancelada e vai para a supervisão.");

    private static readonly Action<ILogger, int, int, int, Exception?> LogProcessado =
        LoggerMessage.Define<int, int, int>(LogLevel.Information, new EventId(2002, "WebhookProcessado"),
            "Webhook do WhatsApp: {Registrados} registrados, {Repetidos} repetidos, {Ignorados} ignorados");

    public static void AdicionarWhatsApp(this WebApplicationBuilder builder)
    {
        var opcoes = builder.Configuration.GetSection("WhatsApp").Get<OpcoesWhatsApp>() ?? new OpcoesWhatsApp();
        builder.Services.AddSingleton(opcoes);
        builder.Services.AddKeyedSingleton(ChaveDataSourceApp, (sp, _) =>
            NpgsqlDataSource.Create(sp.GetRequiredService<IConfiguration>().GetConnectionString("App")
                ?? throw new InvalidOperationException("ConnectionStrings:App não configurada (usuário no papel seceletesc_app).")));
        builder.Services.AddKeyedSingleton(ChaveDataSourceWorker, (sp, _) =>
            NpgsqlDataSource.Create(sp.GetRequiredService<IConfiguration>().GetConnectionString("Worker")
                ?? throw new InvalidOperationException("ConnectionStrings:Worker não configurada (usuário no papel seceletesc_worker).")));
        builder.Services.AddSingleton(sp => new CifradorDadosProtegidos(sp.GetRequiredService<IConfiguration>()["DadosProtegidos:ChavePublicaPem"]
            ?? throw new InvalidOperationException("DadosProtegidos:ChavePublicaPem não configurada: mensagens não são registradas sem cifrar dados protegidos.")));
        builder.Services.AddSingleton<Dominio.Contratos.IMotorIA, MotorIaIndisponivel>();
        builder.Services.AddSingleton(sp => new ProcessadorMensagemRecebida(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ChaveDataSourceApp), sp.GetRequiredService<Dominio.Contratos.IMotorIA>(),
            sp.GetRequiredService<CifradorDadosProtegidos>(), sp.GetRequiredService<TimeProvider>(), opcoes.ProcessamentoHabilitado));
        builder.Services.AddSingleton(sp => new ProcessadorWebhookWhatsApp(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ChaveDataSourceApp), sp.GetRequiredService<ProcessadorMensagemRecebida>()));
        builder.Services.AddSingleton(sp => new TrabalhadorIa(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(ChaveDataSourceApp), sp.GetRequiredService<ProcessadorMensagemRecebida>()));

        // Limite de taxa por IP no webhook (revisão da F4, S7): sem autenticação, cada pedido custa leitura e HMAC.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(PoliticaLimiteTaxa, h => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
                h.Connection.RemoteIpAddress?.ToString() ?? "desconhecido",
                _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
        });

        if (opcoes.ProcessamentoHabilitado)
        {
            builder.Services.AddHostedService<TrabalhadorIaHospedado>();
        }

        // Envio real: só com autorização (decisões 31 e 34). Mesmo ligado, cada número só envia com as três etapas cumpridas
        // no cadastro (conferidas no banco). Sem a chave das credenciais (revisão da M1, S2), o trabalhador roda com um
        // gateway que recusa tudo: cada mensagem é cancelada e vai para a supervisão, em vez de ficar parada sem aviso.
        if (opcoes.EnvioHabilitado)
        {
            if (CifradorCredenciais.DaConfiguracao(opcoes.ChaveCredenciais) is { } cifradorCredenciais)
            {
                builder.Services.AddHttpClient(nameof(GatewayWhatsAppCloud), c => c.Timeout = TimeSpan.FromSeconds(30));
                builder.Services.AddSingleton(sp => new RepositorioCredenciaisWhatsApp(sp.GetRequiredKeyedService<NpgsqlDataSource>(ChaveDataSourceWorker), cifradorCredenciais));
                builder.Services.AddSingleton<Aplicacao.Envio.IGatewayWhatsApp>(sp =>
                {
                    var credenciais = sp.GetRequiredService<RepositorioCredenciaisWhatsApp>();
                    return new GatewayWhatsAppCloud(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GatewayWhatsAppCloud)), opcoes, credenciais.ObterParaEnvioAsync);
                });
            }
            else
            {
                builder.Services.AddSingleton<Aplicacao.Envio.IGatewayWhatsApp, GatewaySemCredenciais>();
            }

            builder.Services.AddSingleton(sp => new ProcessadorEnvio(
                sp.GetRequiredKeyedService<NpgsqlDataSource>(ChaveDataSourceWorker), sp.GetRequiredService<Aplicacao.Envio.IGatewayWhatsApp>(),
                sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<CifradorDadosProtegidos>()));
            builder.Services.AddHostedService<TrabalhadorEnvioHospedado>();
        }
    }

    /// <summary>Lote maior que isso não é processado (revisão da F4, S7): a Meta envia lotes pequenos.</summary>
    public const int MaximoEventosPorLote = 1000;

    public const string PoliticaLimiteTaxa = "whatsapp";

    public static void MapWhatsApp(this WebApplication app)
    {
        if (app.Services.GetService<Aplicacao.Envio.IGatewayWhatsApp>() is GatewaySemCredenciais)
        {
            LogSemChave(app.Logger, null);
        }

        // Verificação do webhook pela Meta (assinatura do campo): devolve o desafio só com o token certo.
        app.MapGet("/whatsapp/webhook", (HttpContext h, OpcoesWhatsApp opcoes) =>
        {
            var q = h.Request.Query;
            var desafio = q["hub.challenge"].ToString();
            return q["hub.mode"] == "subscribe"
                   && IguaisEmTempoConstante(q["hub.verify_token"].ToString(), opcoes.TokenVerificacao)
                   && desafio.Length is > 0 and <= 64 && desafio.All(char.IsAsciiDigit)
                ? Results.Text(desafio, "text/plain")
                : Results.StatusCode(StatusCodes.Status403Forbidden);
        }).RequireRateLimiting(PoliticaLimiteTaxa);

        app.MapPost("/whatsapp/webhook", async (HttpContext h, OpcoesWhatsApp opcoes, ProcessadorWebhookWhatsApp processador, ILoggerFactory logs, CancellationToken ct) =>
        {
            var logger = logs.CreateLogger("Seceletesc.WhatsApp");
            var ip = h.Connection.RemoteIpAddress?.ToString();
            if (string.IsNullOrEmpty(opcoes.AppSecret))
            {
                LogRecusado(logger, "App Secret não configurado", ip, null);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var corpo = await LerCorpoAsync(h, ct);
            if (corpo is null)
            {
                LogRecusado(logger, "corpo grande demais", ip, null);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            // Assinatura sobre os bytes exatos recebidos, antes de interpretar qualquer coisa. Sem log do corpo.
            if (!AssinaturaWebhook.Valida(corpo, h.Request.Headers["X-Hub-Signature-256"].ToString(), opcoes.AppSecret))
            {
                LogRecusado(logger, "assinatura inválida ou ausente", ip, null);
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            IReadOnlyList<EventoWhatsApp> eventos;
            try
            {
                eventos = LeitorWebhookWhatsApp.Ler(Encoding.UTF8.GetString(corpo));
            }
            catch (JsonException)
            {
                LogRecusado(logger, "JSON inválido", ip, null);
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            }

            if (eventos.Count > MaximoEventosPorLote)
            {
                LogRecusado(logger, "lote grande demais", ip, null);
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            var r = await processador.ProcessarAsync(eventos, ct);
            LogProcessado(logger, r.Registrados, r.Repetidos, r.Ignorados + r.Recusados, null);
            return Results.Ok();
        }).RequireRateLimiting(PoliticaLimiteTaxa);
    }

    private static async Task<byte[]?> LerCorpoAsync(HttpContext h, CancellationToken ct)
    {
        if (h.Request.ContentLength > TamanhoMaximoCorpo)
        {
            return null;
        }

        if (h.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
        {
            limite.MaxRequestBodySize = TamanhoMaximoCorpo;
        }

        using var memoria = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int lidos;
        while ((lidos = await h.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (memoria.Length + lidos > TamanhoMaximoCorpo)
            {
                return null;
            }

            memoria.Write(buffer, 0, lidos);
        }

        return memoria.ToArray();
    }

    private static bool IguaisEmTempoConstante(string recebido, string? esperado) =>
        !string.IsNullOrEmpty(esperado)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(recebido)), SHA256.HashData(Encoding.UTF8.GetBytes(esperado)));
}

/// <summary>Trabalhador da fila da IA em segundo plano (só com WhatsApp:ProcessamentoHabilitado).</summary>
internal sealed partial class TrabalhadorIaHospedado(TrabalhadorIa trabalhador, ILogger<TrabalhadorIaHospedado> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await trabalhador.ProcessarProximoAsync(stoppingToken) is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // O laço não pode morrer: a mensagem volta à fila pelo lease.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogFalha(logger, e.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [LoggerMessage(3001, LogLevel.Error, "Falha no trabalhador da IA ({Tipo}); a mensagem volta à fila pelo lease")]
    private static partial void LogFalha(ILogger logger, string tipo);
}

/// <summary>Trabalhador de envio em segundo plano (só com envio habilitado e configurado).</summary>
internal sealed partial class TrabalhadorEnvioHospedado(ProcessadorEnvio processador, ILogger<TrabalhadorEnvioHospedado> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await processador.ProcessarProximoAsync(stoppingToken) == ResultadoCiclo.FilaVazia)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // O laço não pode morrer: o lease devolve a mensagem como incerta, nunca repetida.
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogFalha(logger, e.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [LoggerMessage(3002, LogLevel.Error, "Falha no trabalhador de envio ({Tipo})")]
    private static partial void LogFalha(ILogger logger, string tipo);
}
