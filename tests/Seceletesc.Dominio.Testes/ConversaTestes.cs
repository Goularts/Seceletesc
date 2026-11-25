using Seceletesc.Dominio.Conversas;
using static Seceletesc.Dominio.Testes.Cenario;

namespace Seceletesc.Dominio.Testes;

/// <summary>Máquina de estados da conversa. Plano técnico, seção 4.1.</summary>
public sealed class ConversaTestes
{
    [Fact]
    [Trait("Regra", "INV-5")]
    public void Toda_transicao_incrementa_a_versao_do_estado()
    {
        var c = NovaConversa();
        var v0 = c.VersaoEstado;

        var t1 = c.IniciarTransferencia("bloqueio");
        var t2 = c.Assumir("humano");
        var t3 = c.Liberar(Agora, "liberado");

        Assert.Equal([v0 + 1, v0 + 2, v0 + 3], new[] { t1.NovaVersao, t2.NovaVersao, t3.NovaVersao });
        Assert.Equal(EstadoConversa.Automatico, c.Estado);
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public void So_a_liberacao_explicita_volta_ao_modo_automatico()
    {
        var c = NovaConversa();
        c.IniciarTransferencia("bloqueio");

        // De TransferenciaPendente não se libera: precisa estar sob controle humano.
        Assert.Throws<InvalidOperationException>(() => c.Liberar(Agora, "tentativa"));

        c.Assumir("humano");
        c.Liberar(Agora, "liberado");
        Assert.Equal(EstadoConversa.Automatico, c.Estado);
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public void Humano_pode_assumir_a_partir_de_qualquer_estado()
    {
        var automatica = NovaConversa();
        var pendente = NovaConversa();
        pendente.IniciarTransferencia("bloqueio");

        automatica.Assumir("humano");
        pendente.Assumir("humano");

        Assert.Equal(EstadoConversa.Humano, automatica.Estado);
        Assert.Equal(EstadoConversa.Humano, pendente.Estado);
    }

    [Fact]
    public void Transferencia_so_e_iniciada_a_partir_do_modo_automatico()
    {
        var c = NovaConversa();
        c.Assumir("humano");

        Assert.Throws<InvalidOperationException>(() => c.IniciarTransferencia("bloqueio"));
    }

    [Fact]
    [Trait("Regra", "INV-5")]
    public void Apos_a_liberacao_a_IA_nao_trata_mensagens_recebidas_durante_o_controle_humano()
    {
        var c = NovaConversa();
        c.Assumir("humano");
        var duranteControleHumano = Agora.AddMinutes(-10);
        c.Liberar(Agora.AddMinutes(-5), "liberado");

        Assert.False(c.IaPodeTratarMensagemRecebidaEm(duranteControleHumano));
        Assert.True(c.IaPodeTratarMensagemRecebidaEm(Agora));
    }

    [Fact]
    public void Janela_de_24h_conta_a_partir_da_ultima_mensagem_do_cliente()
    {
        var c = NovaConversa();
        Assert.False(c.DentroDaJanela24h(Agora)); // sem mensagem do cliente, sem janela

        c.RegistrarMensagemDoCliente(Agora.AddHours(-23));
        Assert.True(c.DentroDaJanela24h(Agora));

        Assert.False(c.DentroDaJanela24h(Agora.AddHours(2)));
    }

    [Fact]
    public void Marcacao_de_retomada_humana_nao_e_apagada_por_nova_mensagem_do_cliente()
    {
        var c = NovaConversa();
        c.MarcarRetomadaHumana();

        c.RegistrarMensagemDoCliente(Agora);

        Assert.True(c.MarcadaRetomadaHumana);
    }
}
