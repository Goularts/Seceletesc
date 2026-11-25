using Npgsql;
using Seceletesc.Api.Supervisao;
using Seceletesc.Api.WhatsApp;
using Seceletesc.Infraestrutura.Supervisao;

if (args.Length > 0 && ComandosAdministracao.EhComando(args[0]))
{
    return await ComandosAdministracao.ExecutarAsync(args).ConfigureAwait(false);
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
// Conexão resolvida na primeira requisição: a API sobe sem banco (verificação de saúde) e os testes trocam a configuração.
builder.Services.AddKeyedSingleton(EndpointsSupervisao.ChaveDataSource, (sp, _) =>
    NpgsqlDataSource.Create(sp.GetRequiredService<IConfiguration>().GetConnectionString("Supervisao")
        ?? throw new InvalidOperationException("ConnectionStrings:Supervisao não configurada (usuário no papel seceletesc_supervisao).")));
builder.Services.AddSingleton(sp => new ServicoSupervisao(
    sp.GetRequiredKeyedService<NpgsqlDataSource>(EndpointsSupervisao.ChaveDataSource), sp.GetRequiredService<TimeProvider>()));
builder.AdicionarWhatsApp();
builder.Services.AddSingleton(sp => new ServicoConhecimento(
    sp.GetRequiredKeyedService<NpgsqlDataSource>(EndpointsSupervisao.ChaveDataSource), sp.GetRequiredService<TimeProvider>()));

// Atrás de um proxy que termina o TLS, só os proxies listados podem informar o esquema original (HTTPS).
var proxies = builder.Configuration.GetValue<string>("Seguranca:ProxiesConfiaveis");
if (!string.IsNullOrWhiteSpace(proxies))
{
    builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
        foreach (var ip in proxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            o.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
        }
    });
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(proxies))
{
    app.UseForwardedHeaders();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.MapGet("/saude", () => Results.Ok(new { status = "ok" }));
app.UseRateLimiter();
app.MapSupervisao();
app.MapWhatsApp();

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Ponto de entrada visível para os testes (WebApplicationFactory).</summary>
public partial class Program;
