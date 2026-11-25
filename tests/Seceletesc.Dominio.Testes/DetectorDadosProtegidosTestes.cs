using Seceletesc.Dominio.DadosProtegidos;

namespace Seceletesc.Dominio.Testes;

/// <summary>Dados protegidos nunca vão ao modelo (invariante 23). Dados sintéticos.</summary>
public sealed class DetectorDadosProtegidosTestes
{
    // CPF e CNPJ sintéticos com dígitos verificadores válidos (gerados para teste).
    private const string CpfValido = "52998224725";
    private const string CnpjValido = "11222333000181";

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("Meu CPF é 529.982.247-25", CategoriaDadoProtegido.Cpf)]
    [InlineData("cpf 52998224725 ok", CategoriaDadoProtegido.Cpf)]
    [InlineData("CNPJ 11.222.333/0001-81", CategoriaDadoProtegido.Cnpj)]
    [InlineData("cnpj 11222333000181", CategoriaDadoProtegido.Cnpj)]
    [InlineData("RG 12.345.678-9", CategoriaDadoProtegido.Rg)]
    [InlineData("cartão 4111 1111 1111 1111", CategoriaDadoProtegido.Cartao)]
    [InlineData("email fulano.teste@exemplo.com.br", CategoriaDadoProtegido.Email)]
    [InlineData("CEP 01310-100", CategoriaDadoProtegido.Cep)]
    [InlineData("cep: 01310100", CategoriaDadoProtegido.Cep)]
    public void Dado_protegido_e_substituido_por_marcador(string texto, CategoriaDadoProtegido categoria)
    {
        var r = DetectorDadosProtegidos.Mascarar(texto);

        var ocorrencia = Assert.Single(r.Ocorrencias);
        Assert.Equal(categoria, ocorrencia.Categoria);
        Assert.Contains($"[DADO PROTEGIDO:{categoria.ToString().ToUpperInvariant()}]", r.Texto, StringComparison.Ordinal);
        Assert.DoesNotContain(ocorrencia.Valor, r.Texto, StringComparison.Ordinal);
    }

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("CPF ５２９.９８２.２４７-２５")]      // dígitos de largura total
    [InlineData("CPF 529.982​.247-25")]          // caractere de largura zero no meio
    public void Truques_de_unicode_nao_escondem_o_dado(string texto)
    {
        var r = DetectorDadosProtegidos.Mascarar(texto);

        Assert.Equal(CategoriaDadoProtegido.Cpf, Assert.Single(r.Ocorrencias).Categoria);
        Assert.DoesNotContain(r.Texto, char.IsDigit); // nenhum dígito, ASCII ou de largura total, sobra
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public void Telefone_de_11_digitos_nao_e_confundido_com_cpf()
    {
        var r = DetectorDadosProtegidos.Mascarar("me liga no 11987654321");

        Assert.Empty(r.Ocorrencias);
        Assert.Equal("me liga no 11987654321", r.Texto);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public void Numero_de_cartao_invalido_pelo_luhn_nao_e_marcado()
    {
        Assert.Empty(DetectorDadosProtegidos.Mascarar("pedido 4111 1111 1111 1112").Ocorrencias);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public void Varios_dados_na_mesma_mensagem_sao_todos_retirados()
    {
        var r = DetectorDadosProtegidos.Mascarar($"cpf {CpfValido}, cnpj {CnpjValido} e email a@b.com");

        Assert.Equal(3, r.Ocorrencias.Count);
        Assert.DoesNotContain(CpfValido, r.Texto, StringComparison.Ordinal);
        Assert.DoesNotContain(CnpjValido, r.Texto, StringComparison.Ordinal);
        Assert.DoesNotContain("a@b.com", r.Texto, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public void Texto_sem_dado_protegido_fica_igual()
    {
        var r = DetectorDadosProtegidos.Mascarar("Qual o horário de vocês?");

        Assert.Empty(r.Ocorrencias);
        Assert.Equal("Qual o horário de vocês?", r.Texto);
    }
}
