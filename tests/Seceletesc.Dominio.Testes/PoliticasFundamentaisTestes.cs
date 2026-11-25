using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Envio;
using Seceletesc.Dominio.Politicas;
using static Seceletesc.Dominio.Testes.Cenario;

namespace Seceletesc.Dominio.Testes;

/// <summary>Políticas fundamentais (invariante 25): um piso que a política de uma empresa não desativa.</summary>
public sealed class PoliticasFundamentaisTestes
{
    [Fact]
    [Trait("Regra", "INV-25")]
    public void Termos_de_compromisso_padrao_valem_mesmo_com_lista_vazia_na_politica()
    {
        var politica = Politica() with { TermosCompromisso = [] };

        Assert.Superset(new HashSet<string>(PoliticaTenant.TermosCompromissoPadrao), new HashSet<string>(politica.TermosCompromisso));
        Assert.Empty(politica.TermosCompromissoAdicionais);
    }

    [Fact]
    [Trait("Regra", "INV-25")]
    public void Politica_so_acrescenta_termos_ao_piso()
    {
        var politica = Politica() with { TermosCompromisso = ["brinde"] };

        Assert.Contains("garantido", politica.TermosCompromisso);
        Assert.Contains("brinde", politica.TermosCompromisso);
        Assert.Equal(["brinde"], politica.TermosCompromissoAdicionais);
    }

    [Fact]
    [Trait("Regra", "INV-25")]
    [Trait("Regra", "INV-8")]
    public void Compromisso_padrao_sem_fonte_bloqueia_mesmo_se_a_politica_tentou_tira_lo()
    {
        var fonte = Fonte("horario", "Atendemos de segunda a sexta.");
        var politica = Politica() with { TermosCompromisso = [] };

        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta. Garantimos o retorno.", "horario", fonte), Contexto(NovaConversa(), politica, fonte));

        Assert.Equal(MotivoBloqueio.CompromissoSemFonte, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "INV-19")]
    [Trait("Regra", "INV-20")]
    [InlineData(30, true)]
    [InlineData(5, false)]
    public void Conhecimento_aprovado_com_validade_acima_da_politica_atual_nao_e_usado(int diasDeValidade, bool excede)
    {
        var politica = Politica(p => p.Classificacoes.Add(Classificacao.Temporario)) with
        {
            ValidadeMaximaDias = new Dictionary<Classificacao, int> { [Classificacao.Temporario] = 7 },
        };
        var fonte = Fonte("horario", "Feriado.", classificacoes: [Classificacao.Temporario], validoAte: Agora.AddDays(diasDeValidade)) with { AprovadaEm = Agora.AddDays(-1) };

        var motivo = ElegibilidadeConhecimento.Avaliar(fonte, politica, NovaConversa(), Agora);

        Assert.Equal(excede ? MotivoInelegivel.ValidadeAcimaDaPolitica : null, motivo);
    }

    [Fact]
    [Trait("Regra", "INV-20")]
    public void Sem_data_de_aprovacao_e_com_prazo_na_politica_falha_fechado()
    {
        // Revisão da F4.5 (S8): sem saber quando foi aprovado, não dá para garantir o prazo: não usa.
        var politica = Politica(p => p.Classificacoes.Add(Classificacao.Temporario)) with
        {
            ValidadeMaximaDias = new Dictionary<Classificacao, int> { [Classificacao.Temporario] = 7 },
        };
        var fonte = Fonte("horario", "Feriado.", classificacoes: [Classificacao.Temporario], validoAte: Agora.AddDays(3));

        Assert.Equal(MotivoInelegivel.ValidadeAcimaDaPolitica, ElegibilidadeConhecimento.Avaliar(fonte, politica, NovaConversa(), Agora));
    }

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("88015-200", true)]
    [InlineData("01310-100", false)]
    public void Cep_na_resposta_automatica_so_se_for_institucional(string cep, bool aprovado)
    {
        var texto = "Estamos no CEP " + cep + ".";
        var fonte = Fonte("horario", texto);
        var politica = Politica() with { CepsInstitucionais = new HashSet<string> { "88015200" } };

        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), politica, fonte));

        Assert.Equal(aprovado, r.Aprovado);
    }

    [Theory]
    [Trait("Regra", "INV-25")]
    [Trait("Regra", "INV-11")]
    [InlineData(Classificacao.Interno)]
    [InlineData(Classificacao.Restrito)]
    public void Interno_e_restrito_nunca_vao_para_resposta_automatica(Classificacao classificacao)
    {
        var politica = Politica(p => p.Classificacoes.Add(classificacao));
        var fonte = Fonte("horario", "Margem interna de 40%.", classificacoes: [classificacao], validoAte: Agora.AddDays(30));

        Assert.DoesNotContain(classificacao, politica.ClassificacoesPermitidasRespostaAutomatica);
        Assert.Equal(MotivoInelegivel.ClassificacaoNaoPermitida, ElegibilidadeConhecimento.Avaliar(fonte, politica, NovaConversa(), Agora));
    }
}
