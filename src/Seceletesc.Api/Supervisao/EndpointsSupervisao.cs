using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Api.Supervisao;

public sealed record PedidoLiberar(long VersaoEsperada);

public sealed record PedidoResponder(string? Texto);

public sealed record PedidoOrientar(string? Texto, string? Assunto);

public sealed record PedidoPolitica(int VersaoEsperada, JsonElement Conteudo);

/// <summary>
/// API de supervisão humana (plano técnico, seção 4.9). Todas as rotas exigem um usuário autenticado; o tenant vem
/// do usuário, nunca da requisição. Conversa de outra empresa responde 404, igual a uma que não existe.
/// </summary>
public static class EndpointsSupervisao
{
    public const string ChaveDataSource = "supervisao";

    /// <summary>Limite do corpo das requisições da supervisão (a maior é a política).</summary>
    public const long TamanhoMaximoCorpo = 64 * 1024;

    private const string ItemUsuario = "seceletesc.usuario";

    private static readonly Action<ILogger, string, string?, Exception?> LogFalhaAutenticacao =
        LoggerMessage.Define<string, string?>(LogLevel.Warning, new EventId(1001, "FalhaAutenticacaoSupervisao"),
            "Falha de autenticação na supervisão: {Motivo} (ip {Ip})");

    public static void MapSupervisao(this WebApplication app)
    {
        // Autentica ANTES do model binding: sem chave válida, o corpo nem é lido (nada de 400 que revele o formato).
        app.UseWhen(h => h.Request.Path.StartsWithSegments("/supervisao", StringComparison.Ordinal), b => b.Use(AutenticarAsync));

        var g = app.MapGroup("/supervisao");

        g.MapGet("/conversas", async (HttpContext h, ServicoSupervisao s, string? estado, CancellationToken ct) =>
            estado is null or "Automatico" or "TransferenciaPendente" or "Humano"
                ? Results.Ok(await s.ListarConversasAsync(Usuario(h), estado, ct))
                : Results.Problem("estado inválido", statusCode: StatusCodes.Status400BadRequest));

        g.MapGet("/conversas/{id:guid}", async (HttpContext h, ServicoSupervisao s, Guid id, CancellationToken ct) =>
            await s.ObterConversaAsync(Usuario(h), id, ct) is { } d ? Results.Ok(d) : Results.NotFound());

        g.MapPost("/conversas/{id:guid}/assumir", async (HttpContext h, ServicoSupervisao s, Guid id, CancellationToken ct) =>
            Responder(await s.AssumirAsync(Usuario(h), id, ct)));

        g.MapPost("/conversas/{id:guid}/liberar", async (HttpContext h, ServicoSupervisao s, Guid id, PedidoLiberar p, CancellationToken ct) =>
            Responder(await s.LiberarAsync(Usuario(h), id, p.VersaoEsperada, ct)));

        g.MapPost("/conversas/{id:guid}/responder", async (HttpContext h, ServicoSupervisao s, Guid id, PedidoResponder p, CancellationToken ct) =>
            Responder(await s.ResponderAsync(Usuario(h), id, p.Texto, ct), StatusCodes.Status202Accepted));

        g.MapPost("/conversas/{id:guid}/orientar", async (HttpContext h, ServicoSupervisao s, Guid id, PedidoOrientar p, CancellationToken ct) =>
            Responder(await s.OrientarAsync(Usuario(h), id, p.Texto, p.Assunto, ct), StatusCodes.Status201Created));

        g.MapGet("/politica", async (HttpContext h, ServicoSupervisao s, CancellationToken ct) =>
            await s.ObterPoliticaAsync(Usuario(h), ct) is { } p ? Results.Ok(p) : Results.NotFound());

        g.MapPut("/politica", async (HttpContext h, ServicoSupervisao s, PedidoPolitica p, CancellationToken ct) =>
            Responder(await s.PublicarPoliticaAsync(Usuario(h), p.VersaoEsperada, p.Conteudo.ValueKind == JsonValueKind.Object ? p.Conteudo.GetRawText() : null, ct)));
    }

    private static async Task AutenticarAsync(HttpContext http, RequestDelegate next)
    {
        if (http.Request.ContentLength > TamanhoMaximoCorpo)
        {
            http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limite)
        {
            limite.MaxRequestBodySize = TamanhoMaximoCorpo;
        }

        var cabecalho = http.Request.Headers.Authorization.ToString();
        var chave = cabecalho.StartsWith("Bearer ", StringComparison.Ordinal) ? cabecalho["Bearer ".Length..].Trim() : null;
        var usuario = await http.RequestServices.GetRequiredService<ServicoSupervisao>().AutenticarAsync(chave, http.RequestAborted);
        if (usuario is null)
        {
            // Sem a chave no log: só o motivo e a origem, para detectar força bruta ou chave vazada.
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Seceletesc.Supervisao");
            LogFalhaAutenticacao(logger, chave is null ? "sem chave Bearer" : "chave desconhecida ou desativada", http.Connection.RemoteIpAddress?.ToString(), null);
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        http.Items[ItemUsuario] = usuario;
        await next(http);
    }

    private static UsuarioSupervisao Usuario(HttpContext h) =>
        h.Items[ItemUsuario] as UsuarioSupervisao ?? throw new InvalidOperationException("Rota de supervisão sem autenticação.");

    private static IResult Responder(ResultadoOperacao r, int statusOk = StatusCodes.Status200OK) => r.Situacao switch
    {
        SituacaoOperacao.Ok => Results.Json(new { versao = r.Versao, id = r.Id, detalhe = r.Detalhe }, statusCode: statusOk),
        SituacaoOperacao.NaoEncontrada => Results.NotFound(),
        SituacaoOperacao.Conflito => Results.Problem(r.Detalhe, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["versaoAtual"] = r.Versao }),
        _ => Results.Problem(r.Detalhe, statusCode: StatusCodes.Status422UnprocessableEntity),
    };
}
