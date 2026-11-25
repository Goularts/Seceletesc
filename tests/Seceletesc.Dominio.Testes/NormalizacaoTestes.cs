using Seceletesc.Dominio.Comum;

namespace Seceletesc.Dominio.Testes;

/// <summary>
/// A normalização sustenta todas as regras de texto do validador. Se o ambiente não normalizar Unicode
/// (ex.: InvariantGlobalization), estes testes falham antes de qualquer regra ficar cega.
/// </summary>
public sealed class NormalizacaoTestes
{
    [Theory]
    [Trait("Regra", "INV-14")]
    [InlineData("Não sou um robô", "nao sou um robo")]
    [InlineData("ＳＯＵ ＨＵＭＡＮＯ", "sou humano")]
    [InlineData("sou​ hu‍mano", "sou humano")]
    [InlineData("Opção   2", "opcao 2")]
    public void Acentos_largura_total_invisiveis_e_espacos_sao_normalizados(string entrada, string esperado)
    {
        Assert.Equal(esperado, Normalizacao.ParaComparacao(entrada));
    }

    [Fact]
    [Trait("Regra", "INV-14")]
    public void Numeros_ignoram_zeros_a_esquerda()
    {
        Assert.Equal(new HashSet<string> { "5", "12", "2026" }, Normalizacao.Numeros("05/12/2026"));
    }
}
