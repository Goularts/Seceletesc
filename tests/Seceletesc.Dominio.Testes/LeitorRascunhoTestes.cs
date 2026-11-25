using Seceletesc.Dominio.Respostas;
using static Seceletesc.Dominio.Testes.Cenario;

namespace Seceletesc.Dominio.Testes;

/// <summary>Rascunho do modelo em schema estrito. Plano técnico, seção 4.3.</summary>
public sealed class LeitorRascunhoTestes
{
    [Fact]
    [Trait("Regra", "INV-14")]
    public void Rascunho_valido_e_lido()
    {
        var fonte = Fonte("horario", "x");
        var r = LeitorRascunho.TentarLer(RascunhoJson("Olá", "horario", fonte), out var erro);

        Assert.NotNull(r);
        Assert.Null(erro);
        Assert.Equal([fonte.Chave], r.Fontes);
    }

    [Theory]
    [Trait("Regra", "INV-14")]
    [InlineData("")]
    [InlineData("não é json")]
    [InlineData("[]")]
    [InlineData("""{"texto":"oi","fontes":[],"assunto":"horario","enviar_agora":true}""")]
    [InlineData("""{"texto":"oi","fontes":[]}""")]
    [InlineData("""{"texto":123,"fontes":[],"assunto":"horario"}""")]
    [InlineData("""{"texto":"oi","fontes":[{"item":"nao-e-uuid","versao":1}],"assunto":"horario"}""")]
    [InlineData("""{"texto":"oi","fontes":[{"item":"aaaaaaaa-0000-0000-0000-000000000001","versao":0}],"assunto":"horario"}""")]
    [InlineData("""{"texto":"oi","fontes":[{"item":"aaaaaaaa-0000-0000-0000-000000000001","versao":1,"status":"aprovado"}],"assunto":"horario"}""")]
    [InlineData("""{"texto":"oi","texto":"outro","fontes":[],"assunto":"horario"}""")]
    [InlineData("""{"texto":"   ","fontes":[],"assunto":"horario"}""")]
    public void Rascunho_fora_do_schema_e_rejeitado(string json)
    {
        Assert.Null(LeitorRascunho.TentarLer(json, out var erro));
        Assert.NotNull(erro);
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public void Texto_acima_do_limite_e_rejeitado()
    {
        var json = RascunhoJson(new string('a', LeitorRascunho.TamanhoMaximoTexto + 1), "horario", Fonte("horario", "x"));

        Assert.Null(LeitorRascunho.TentarLer(json, out _));
    }
}
