using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Seceletesc.Integracao.Testes.Pipeline;

namespace Seceletesc.Integracao.Testes.Supervisao;

/// <summary>A API real, em memória, conectada ao banco do teste com o usuário do papel seceletesc_supervisao.</summary>
internal sealed class ApiSupervisao(BancoFixture banco, TimeProvider relogio) : ApiSupervisaoBase(banco, relogio);

internal abstract class ApiSupervisaoBase(BancoFixture banco, TimeProvider relogio) : WebApplicationFactory<Program>
{
    public HttpClient Cliente(string? chave)
    {
        var c = CreateClient();
        if (chave is not null)
        {
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", chave);
        }

        return c;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Supervisao", banco.ConnectionString("supervisao_teste"));
        builder.ConfigureTestServices(s => s.AddSingleton(relogio));
    }
}

internal static class ExtensoesTesteSupervisao
{
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    public static ApiSupervisao Api(this AmbientePipeline amb) => new(amb.Banco, amb.Relogio);

    /// <summary>Conversa em modo automático com uma resposta da IA já enviada (horário, fundamentada).</summary>
    public static async Task<Guid> ConversaAtendidaPelaIaAsync(this AmbientePipeline amb, string horario = SupervisaoApiTestes.Horario)
    {
        await amb.ConhecimentoAsync("horario", horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?"));
        await amb.Worker().ProcessarProximoAsync();
        return await amb.ConversaIdAsync();
    }

    /// <summary>Conversa na fila: a IA bloqueou uma resposta e a conversa aguarda um humano, sem responsável.</summary>
    public static async Task<Guid> ConversaNaFilaAsync(this AmbientePipeline amb, string horario = SupervisaoApiTestes.Horario)
    {
        var conversa = await amb.ConversaAtendidaPelaIaAsync(horario);
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).ProcessarAsync(amb.Mensagem("Quero falar com alguém."));
        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        return conversa;
    }
}
