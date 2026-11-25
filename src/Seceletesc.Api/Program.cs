using Npgsql;
using Seceletesc.Api.Supervisao;
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

var app = builder.Build();

app.MapGet("/saude", () => Results.Ok(new { status = "ok" }));
app.MapSupervisao();

await app.RunAsync().ConfigureAwait(false);
return 0;

/// <summary>Ponto de entrada visível para os testes (WebApplicationFactory).</summary>
public partial class Program;
