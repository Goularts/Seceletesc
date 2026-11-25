using NetArchTest.Rules;
using Seceletesc.Aplicacao;
using Seceletesc.MotorIA;

namespace Seceletesc.Arquitetura.Testes;

/// <summary>Fronteiras entre projetos. Plano técnico, seção 3.</summary>
public sealed class FronteirasTestes
{
    [Fact]
    [Trait("Regra", "INV-14")]
    public void Dominio_nao_depende_de_infraestrutura_banco_ou_web()
    {
        var resultado = Types.InAssembly(typeof(Seceletesc.Dominio.Tenants.Tenant).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Seceletesc.Aplicacao",
                "Seceletesc.Infraestrutura",
                "Seceletesc.MotorIA",
                "Seceletesc.Api",
                "Microsoft.EntityFrameworkCore",
                "Npgsql",
                "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(resultado.IsSuccessful, Falhas(resultado));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public void Motor_de_IA_nao_acessa_banco_nem_infraestrutura_de_envio()
    {
        var assembly = typeof(MarcadorMotorIA).Assembly;

        var resultado = Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Seceletesc.Infraestrutura",
                "Seceletesc.Aplicacao",
                "Seceletesc.Api",
                "Microsoft.EntityFrameworkCore",
                "Npgsql")
            .GetResult();

        Assert.True(resultado.IsSuccessful, Falhas(resultado));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public void Aplicacao_nao_depende_de_infraestrutura()
    {
        var resultado = Types.InAssembly(typeof(MarcadorAplicacao).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny("Seceletesc.Infraestrutura", "Seceletesc.Api", "Npgsql", "Microsoft.EntityFrameworkCore")
            .GetResult();

        Assert.True(resultado.IsSuccessful, Falhas(resultado));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-14")]
    public void So_o_gateway_fala_com_a_Meta()
    {
        // Único componente com a credencial de envio (plano técnico, seção 4.4): nenhum outro tipo da infraestrutura
        // usa HTTP de saída. A API só recebe o webhook.
        var resultado = Types.InAssembly(typeof(Seceletesc.Infraestrutura.WhatsApp.GatewayWhatsAppCloud).Assembly)
            .That().DoNotHaveName(nameof(Seceletesc.Infraestrutura.WhatsApp.GatewayWhatsAppCloud))
            .ShouldNot().HaveDependencyOnAny("System.Net.Http.HttpClient", "System.Net.Http.HttpMessageInvoker")
            .GetResult();

        Assert.True(resultado.IsSuccessful, Falhas(resultado));
    }

    private static string Falhas(TestResult resultado) =>
        "Dependências proibidas: " + string.Join(", ", resultado.FailingTypeNames ?? []);
}
