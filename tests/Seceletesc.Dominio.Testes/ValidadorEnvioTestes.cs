using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Envio;
using static Seceletesc.Dominio.Testes.Cenario;

namespace Seceletesc.Dominio.Testes;

/// <summary>Validador determinístico de envio. Plano técnico, seção 4.3; matriz-rastreabilidade.md.</summary>
public sealed class ValidadorEnvioTestes
{
    private static readonly FonteConhecimento Horario = Fonte("horario", "Atendemos de segunda a sexta, das 9h às 18h.");
    private static readonly FonteConhecimento Precos = Fonte("precos", "A consulta custa R$ 150,00.");

    [Fact]
    [Trait("Regra", "INV-6")]
    public void Resposta_fundamentada_em_fonte_aprovada_e_aprovada()
    {
        var conversa = NovaConversa();
        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta, das 9h às 18h.", "horario", Horario), Contexto(conversa, Politica(), Horario));

        Assert.True(r.Aprovado, r.Motivo?.ToString());
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public void Rascunho_invalido_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(null, Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.RascunhoInvalido, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-09")]
    [Trait("Regra", "CTX-010")]
    public void Humano_assume_durante_a_geracao_e_a_resposta_e_bloqueada()
    {
        var conversa = NovaConversa();
        var ctx = Contexto(conversa, Politica(), Horario); // versão lida no início
        conversa.Assumir("humano assumiu pelo painel");    // muda durante a geração

        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta, das 9h às 18h.", "horario", Horario), ctx with { ConversaAtual = conversa });

        Assert.Equal(MotivoBloqueio.EstadoMudou, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "CTX-011")]
    [Trait("Regra", "INV-5")]
    public void Conversa_sob_controle_humano_bloqueia_envio_automatico_mesmo_com_versao_atual()
    {
        var conversa = NovaConversa();
        conversa.Assumir("humano");

        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta, das 9h às 18h.", "horario", Horario), Contexto(conversa, Politica(), Horario));

        Assert.Equal(MotivoBloqueio.EstadoMudou, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-07")]
    [Trait("Regra", "CTX-004")]
    [Trait("Regra", "INV-9")]
    public void Assunto_fora_da_lista_autorizada_e_bloqueado()
    {
        var fonte = Fonte("financiamento", "Trabalhamos com financiamento.");
        var r = ValidadorEnvio.Validar(Rascunho("Trabalhamos com financiamento.", "financiamento", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.AssuntoNaoAutorizado, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-01")]
    [Trait("Regra", "CTX-005")]
    public void Pedido_de_desconto_e_assunto_que_exige_humano()
    {
        var fonte = Fonte("desconto", "Descontos são avaliados pela equipe.");
        var r = ValidadorEnvio.Validar(Rascunho("Posso te dar 10% de desconto.", "desconto", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.AssuntoExigeHumano, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-01")]
    [Trait("Regra", "CTX-003")]
    public void Desconto_inventado_dentro_de_assunto_autorizado_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("A consulta custa R$ 150,00, mas faço por 10% a menos.", "precos", Precos), Contexto(NovaConversa(), Politica(), Precos));

        Assert.False(r.Aprovado);
        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-02")]
    [Trait("Regra", "CTX-003")]
    [Trait("Regra", "INV-8")]
    public void Preco_que_nao_consta_na_fonte_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("O exame custa R$ 90.", "precos", Precos), Contexto(NovaConversa(), Politica(), Precos));

        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-03")]
    [Trait("Regra", "CTX-002")]
    public void Horario_sem_disponibilidade_confirmada_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("Temos horário às 14h30.", "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-12")]
    [Trait("Regra", "CTX-009")]
    public void Compromisso_sem_numero_e_sem_fonte_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("Pode ficar tranquilo, está garantido para amanhã.", "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.CompromissoSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-14")]
    [Trait("Regra", "CTX-001")]
    public void Assunto_declarado_diferente_do_assunto_da_fonte_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("A consulta custa R$ 150,00.", "horario", Precos), Contexto(NovaConversa(), Politica(), Precos));

        Assert.Equal(MotivoBloqueio.AssuntoDivergeDaFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-15")]
    [Trait("Regra", "CTX-002")]
    public void Fonte_valida_mas_sem_relacao_com_o_texto_nao_sustenta_valores()
    {
        var r = ValidadorEnvio.Validar(Rascunho("O procedimento custa R$ 300.", "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-15")]
    public void Fonte_citada_que_nao_foi_entregue_ao_modelo_nesta_rodada_e_bloqueada()
    {
        var naoEntregue = Fonte("horario", "Atendemos de segunda a sexta, das 9h às 18h.");
        var ctx = Contexto(NovaConversa(), Politica(), Horario) with
        {
            FontesCitadasNoBanco = new Dictionary<ChaveFonte, FonteConhecimento> { [naoEntregue.Chave] = naoEntregue },
        };

        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta, das 9h às 18h.", "horario", naoEntregue), ctx);

        Assert.Equal(MotivoBloqueio.FonteNaoEntregue, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "CTX-002")]
    public void Resposta_sem_nenhuma_fonte_e_bloqueada()
    {
        var r = ValidadorEnvio.Validar(Rascunho("Olá! Como posso ajudar?", "horario"), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.SemFonte, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-10")]
    [Trait("Regra", "CTX-012")]
    [Trait("Regra", "CTX-014")]
    [Trait("Regra", "INV-20")]
    [InlineData(StatusConhecimento.Rev)]
    [InlineData(StatusConhecimento.Rejeitado)]
    [InlineData(StatusConhecimento.Revogado)]
    public void Conhecimento_nao_aprovado_nao_sustenta_resposta(StatusConhecimento status)
    {
        var fonte = Fonte("horario", "Atendemos de segunda a sexta, das 9h às 18h.", status);
        var r = ValidadorEnvio.Validar(Rascunho("Atendemos de segunda a sexta, das 9h às 18h.", "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.FonteInelegivel, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-08")]
    [Trait("Regra", "CTX-006")]
    public void Conhecimento_expirado_nao_sustenta_resposta()
    {
        var fonte = Fonte("horario", "Em dezembro atendemos até as 20h.", validoAte: Agora.AddMinutes(-1));
        var r = ValidadorEnvio.Validar(Rascunho("Em dezembro atendemos até as 20h.", "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.FonteInelegivel, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-05")]
    [Trait("Regra", "CTX-007")]
    [Trait("Regra", "INV-10")]
    public void Conhecimento_de_outro_cliente_ou_de_outra_empresa_nao_sustenta_resposta()
    {
        var deOutroCliente = Fonte("horario", "Seu horário é às 9h.", escopo: new EscopoConhecimento(TipoEscopo.Cliente, OutroCliente), classificacoes: [Classificacao.Publico]);
        var deOutraEmpresa = Fonte("horario", "Atendemos às 9h.", tenant: OutroTenant);

        var r1 = ValidadorEnvio.Validar(Rascunho("Seu horário é às 9h.", "horario", deOutroCliente), Contexto(NovaConversa(), Politica(), deOutroCliente));
        var r2 = ValidadorEnvio.Validar(Rascunho("Atendemos às 9h.", "horario", deOutraEmpresa), Contexto(NovaConversa(), Politica(), deOutraEmpresa));

        Assert.Equal(MotivoBloqueio.FonteInelegivel, r1.Motivo);
        Assert.Equal(MotivoBloqueio.FonteInelegivel, r2.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-04")]
    [Trait("Regra", "INV-11")]
    [InlineData(Classificacao.Interno)]
    [InlineData(Classificacao.Negociacao)]
    [InlineData(Classificacao.Restrito)]
    [InlineData(Classificacao.Excecao)]
    public void Conhecimento_confidencial_nao_sustenta_resposta_automatica(Classificacao classificacao)
    {
        var fonte = Fonte("precos", "Margem interna de 40%.", classificacoes: [classificacao]);
        var r = ValidadorEnvio.Validar(Rascunho("Margem interna de 40%.", "precos", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.FonteInelegivel, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-17")]
    [InlineData("Acesse https://promo-falsa.com para pagar.")]
    [InlineData("Veja em www.golpe.com.br")]
    [InlineData("Mande para contato@outro-site.net")]
    public void Url_fora_da_lista_permitida_e_bloqueada(string texto)
    {
        var fonte = Fonte("horario", texto);
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.UrlNaoPermitida, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-17")]
    public void Url_de_dominio_permitido_pela_politica_passa()
    {
        var fonte = Fonte("horario", "Agende em https://agenda.empresa.com.br");
        var politica = Politica(p => p.Dominios.Add("empresa.com.br"));

        var r = ValidadorEnvio.Validar(Rascunho("Agende em https://agenda.empresa.com.br", "horario", fonte), Contexto(NovaConversa(), politica, fonte));

        Assert.True(r.Aprovado, r.Motivo?.ToString());
    }

    [Fact]
    [Trait("Regra", "ADV-21")]
    [Trait("Regra", "CTX-007")]
    public void Telefone_de_outra_pessoa_no_texto_e_bloqueado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("Fale com a Maria no (11) 97777-6666.", "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.TelefoneNaoAutorizado, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-13")]
    [Trait("Regra", "CTX-015")]
    [Trait("Regra", "INV-13")]
    [InlineData("Pode confiar, eu sou uma pessoa real.")]
    [InlineData("Não sou um robô, fique tranquilo.")]
    [InlineData("Você está falando com um humano.")]
    [InlineData("ｓｏｕ ｈｕｍａｎｏ")] // largura total (homóglifo de compatibilidade)
    [InlineData("sou hu​mano")]   // caractere de largura zero
    public void Autodeclaracao_como_humano_e_bloqueada(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.AutodeclaracaoHumana, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "INV-1")]
    [InlineData("Escolha:\n1) Horários\n2) Preços")]
    [InlineData("Digite 1 para horários.")]
    [InlineData("Responda com a opção 2.")]
    public void Menu_ou_opcoes_numeradas_sao_bloqueados(string texto)
    {
        var fonte = Fonte("horario", texto);
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.MenuOuOpcoes, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-18")]
    [Trait("Regra", "CTX-011")]
    public void Aviso_de_transferencia_so_sai_em_transferencia_pendente_com_a_versao_esperada()
    {
        var conversa = NovaConversa();
        var transicao = conversa.IniciarTransferencia("bloqueio");
        var politica = Politica();

        Assert.True(ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, politica).Aprovado);

        conversa.Assumir("humano assumiu antes do aviso sair");
        Assert.Equal(MotivoBloqueio.EstadoMudou, ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, politica).Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-18")]
    public void Sem_texto_de_aviso_configurado_nenhum_aviso_sai()
    {
        var conversa = NovaConversa();
        var transicao = conversa.IniciarTransferencia("bloqueio");

        var r = ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, Politica(p => p.Aviso = ""));

        Assert.Equal(MotivoBloqueio.AvisoNaoConfigurado, r.Motivo);
    }
}
