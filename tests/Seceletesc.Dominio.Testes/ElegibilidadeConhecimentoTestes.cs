using Seceletesc.Dominio.Conhecimento;
using static Seceletesc.Dominio.Testes.Cenario;

namespace Seceletesc.Dominio.Testes;

/// <summary>O que pode ir ao modelo e ser citado. Invariantes 10, 11, 19, 20 e 21.</summary>
public sealed class ElegibilidadeConhecimentoTestes
{
    [Fact]
    [Trait("Regra", "INV-20")]
    public void Conhecimento_publico_aprovado_vigente_da_empresa_e_elegivel()
    {
        Assert.Null(ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x"), Politica(), NovaConversa(), Agora));
    }

    [Fact]
    [Trait("Regra", "INV-20")]
    [Trait("Regra", "CTX-014")]
    public void Pendente_rejeitado_revogado_e_expirado_nao_sao_elegiveis()
    {
        var c = NovaConversa();
        var p = Politica();

        Assert.Equal(MotivoInelegivel.NaoAprovado, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", StatusConhecimento.Rev), p, c, Agora));
        Assert.Equal(MotivoInelegivel.NaoAprovado, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", StatusConhecimento.Rejeitado), p, c, Agora));
        Assert.Equal(MotivoInelegivel.NaoAprovado, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", StatusConhecimento.Revogado), p, c, Agora));
        Assert.Equal(MotivoInelegivel.Expirado, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", validoAte: Agora), p, c, Agora));
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public void Publico_sem_validade_e_elegivel_validade_indefinida()
    {
        Assert.Null(ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", validoAte: null), Politica(), NovaConversa(), Agora.AddYears(10)));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    [Trait("Regra", "CTX-007")]
    public void Escopo_de_outro_cliente_de_outra_conversa_ou_de_outra_empresa_nao_e_elegivel()
    {
        var c = NovaConversa();
        var p = Politica();

        Assert.Equal(MotivoInelegivel.ForaDoEscopo, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", escopo: new EscopoConhecimento(TipoEscopo.Cliente, OutroCliente)), p, c, Agora));
        Assert.Equal(MotivoInelegivel.ForaDoEscopo, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", escopo: new EscopoConhecimento(TipoEscopo.Atendimento, Guid.NewGuid())), p, c, Agora));
        Assert.Equal(MotivoInelegivel.OutroTenant, ElegibilidadeConhecimento.Avaliar(Fonte("horario", "x", tenant: OutroTenant), p, c, Agora));
    }

    [Fact]
    [Trait("Regra", "INV-21")]
    public void Excecao_so_e_elegivel_se_a_politica_permitir_e_dentro_do_escopo_aprovado()
    {
        var c = NovaConversa();
        var excecaoDoAtendimento = Fonte("precos", "x", classificacoes: [Classificacao.Excecao], escopo: new EscopoConhecimento(TipoEscopo.Atendimento, c.Id), validoAte: Agora.AddDays(30));
        var excecaoDeOutroAtendimento = Fonte("precos", "x", classificacoes: [Classificacao.Excecao], escopo: new EscopoConhecimento(TipoEscopo.Atendimento, Guid.NewGuid()), validoAte: Agora.AddDays(30));
        var politicaPermiteExc = Politica(p => p.Classificacoes.Add(Classificacao.Excecao));

        Assert.Equal(MotivoInelegivel.ClassificacaoNaoPermitida, ElegibilidadeConhecimento.Avaliar(excecaoDoAtendimento, Politica(), c, Agora));
        Assert.Null(ElegibilidadeConhecimento.Avaliar(excecaoDoAtendimento, politicaPermiteExc, c, Agora));
        Assert.Equal(MotivoInelegivel.ForaDoEscopo, ElegibilidadeConhecimento.Avaliar(excecaoDeOutroAtendimento, politicaPermiteExc, c, Agora));
    }

    [Fact]
    [Trait("Regra", "INV-11")]
    [Trait("Regra", "CTX-007")]
    public void Conhecimento_de_cliente_e_minimizado_por_padrao_mesmo_que_a_classificacao_seja_permitida()
    {
        var c = NovaConversa();
        var cli = Fonte("horario", "x", classificacoes: [Classificacao.Cliente], escopo: new EscopoConhecimento(TipoEscopo.Cliente, Cliente), validoAte: Agora.AddDays(30));
        var politica = Politica(p => p.Classificacoes.Add(Classificacao.Cliente));

        Assert.Equal(MotivoInelegivel.ConhecimentoDeClienteMinimizado, ElegibilidadeConhecimento.Avaliar(cli, politica, c, Agora));
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public void Classificacoes_coexistem_e_todas_precisam_ser_permitidas()
    {
        var pubEInt = Fonte("horario", "x", classificacoes: [Classificacao.Publico, Classificacao.Interno], validoAte: Agora.AddDays(30));

        Assert.Equal(MotivoInelegivel.ClassificacaoNaoPermitida, ElegibilidadeConhecimento.Avaliar(pubEInt, Politica(), NovaConversa(), Agora));
    }

    [Fact]
    [Trait("Regra", "INV-19")]
    public void Conhecimento_sem_classificacao_nao_e_elegivel()
    {
        var semClassificacao = Fonte("horario", "x", classificacoes: [], validoAte: Agora.AddDays(30));

        Assert.Equal(MotivoInelegivel.ClassificacaoNaoPermitida, ElegibilidadeConhecimento.Avaliar(semClassificacao, Politica(), NovaConversa(), Agora));
    }

    [Fact]
    [Trait("Regra", "CTX-007")]
    [Trait("Regra", "INV-10")]
    public void Conhecimento_de_cliente_com_escopo_de_empresa_nao_vale_para_outros_clientes()
    {
        var cliEmpresa = Fonte("horario", "x", classificacoes: [Classificacao.Cliente], escopo: EscopoConhecimento.Empresa, validoAte: Agora.AddDays(30));
        var politica = Politica(p => p.Classificacoes.Add(Classificacao.Cliente)) with { EnviarConhecimentoClienteAoModelo = true };

        Assert.Equal(MotivoInelegivel.ForaDoEscopo, ElegibilidadeConhecimento.Avaliar(cliEmpresa, politica, NovaConversa(), Agora));
    }

    [Theory]
    [Trait("Regra", "CTX-006")]
    [Trait("Regra", "INV-19")]
    [InlineData(Classificacao.Temporario)]
    [InlineData(Classificacao.Excecao)]
    [InlineData(Classificacao.Cliente)]
    public void So_conhecimento_publico_pode_ficar_sem_validade(Classificacao classificacao)
    {
        var semValidade = Fonte("horario", "x", classificacoes: [classificacao], validoAte: null);
        var politica = Politica(p => p.Classificacoes.Add(classificacao));

        Assert.Equal(MotivoInelegivel.SemValidade, ElegibilidadeConhecimento.Avaliar(semValidade, politica, NovaConversa(), Agora));
    }

    [Fact]
    [Trait("Regra", "ADV-08")]
    [Trait("Regra", "CTX-014")]
    public void Versao_que_nao_e_a_vigente_nao_e_elegivel()
    {
        var antiga = Fonte("horario", "x") with { EhVersaoVigente = false };

        Assert.Equal(MotivoInelegivel.VersaoNaoVigente, ElegibilidadeConhecimento.Avaliar(antiga, Politica(), NovaConversa(), Agora));
    }
}
