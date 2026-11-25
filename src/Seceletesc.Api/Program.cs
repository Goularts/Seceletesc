var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Verificação de saúde. As APIs de webhook, supervisão e conhecimento entram nas fases F2 a F4.
app.MapGet("/saude", () => Results.Ok(new { status = "ok" }));

await app.RunAsync().ConfigureAwait(false);
