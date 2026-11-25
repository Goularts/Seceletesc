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
    [InlineData("cpf 529 982 247 25", CategoriaDadoProtegido.Cpf)]
    [InlineData("cpf 529.982.247\u201325", CategoriaDadoProtegido.Cpf)]
    [InlineData("CEP 01.310-100", CategoriaDadoProtegido.Cep)]
    [InlineData("cartão 4111.1111.1111.1111", CategoriaDadoProtegido.Cartao)]
    [InlineData("RG 12345678-9", CategoriaDadoProtegido.Rg)]
    [InlineData("pix 123e4567-e89b-12d3-a456-426614174000", CategoriaDadoProtegido.ChavePix)]
    [InlineData("email fulano arroba gmail ponto com", CategoriaDadoProtegido.Email)]
    [InlineData("sus 898 0012 3456 7890", CategoriaDadoProtegido.Documento)]
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
    [InlineData("CPF \u0665\u0662\u0669\u0669\u0668\u0662\u0662\u0664\u0667\u0662\u0665")] // dígitos árabe-índicos
    [InlineData("CPF 529.982\u2066.247-25")] // isolamento bidi no meio
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
    public void Numero_de_cartao_invalido_pelo_luhn_nao_e_marcado_como_cartao_mas_continua_protegido()
    {
        var r = DetectorDadosProtegidos.Mascarar("pedido 4111 1111 1111 1112");

        Assert.Equal(CategoriaDadoProtegido.Documento, Assert.Single(r.Ocorrencias).Categoria);
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
    public void Impressao_da_ocorrencia_nunca_mostra_o_valor()
    {
        var r = DetectorDadosProtegidos.Mascarar("cpf 529.982.247-25");

        Assert.DoesNotContain("529", Assert.Single(r.Ocorrencias).ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("529", r.ToString(), StringComparison.Ordinal);
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
