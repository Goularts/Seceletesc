using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.Conhecimento;

/// <summary>Guarda tudo o que a API registra em log, em todos os níveis, para conferir que nada sensível vaza.</summary>
internal sealed class LogsCapturados : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _linhas = new();

    public string Texto() => string.Join("\n", _linhas);

    public ILogger CreateLogger(string categoryName) => new Registrador(categoryName, _linhas);

    public void Dispose()
    {
    }

    private sealed class Registrador(string categoria, ConcurrentQueue<string> linhas) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            linhas.Enqueue($"{logLevel} {categoria}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>A API de supervisão com todos os logs capturados (nível mínimo: Trace).</summary>
internal sealed class ApiSupervisaoComLogs(BancoFixture banco, TimeProvider relogio, LogsCapturados logs) : ApiSupervisaoBase(banco, relogio)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        builder.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs));
    }
}
