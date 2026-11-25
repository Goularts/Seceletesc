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
    [Trait("Regra", "CTX-003")]
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
    [Trait("Regra", "CTX-003")]
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

    [Fact]
    [Trait("Regra", "CTX-007")]
    public void Nenhum_telefone_sai_em_resposta_automatica_nem_o_que_consta_numa_fonte()
    {
        var fonte = Fonte("horario", "Ligue (11) 3333-4444 de segunda a sexta.");
        var r = ValidadorEnvio.Validar(Rascunho("Ligue (11) 3333-4444 de segunda a sexta.", "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

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
    [Trait("Regra", "INV-6")]
    public void Sem_texto_de_aviso_configurado_nenhum_aviso_sai()
    {
        var conversa = NovaConversa();
        var transicao = conversa.IniciarTransferencia("bloqueio");

        var r = ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, Politica(p => p.Aviso = ""));

        Assert.Equal(MotivoBloqueio.AvisoNaoConfigurado, r.Motivo);
    }

    // Revisão da F2 (G1): o aviso é mensagem automática e passa pelas regras de conteúdo, sem fonte.
    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData("Retornamos em 10 minutos.", MotivoBloqueio.ValorSemFonte)]
    [InlineData("Em meia hora alguém fala com você.", MotivoBloqueio.ValorSemFonte)]
    [InlineData("Nossa equipe responde ainda hoje.", MotivoBloqueio.CompromissoSemFonte)]
    [InlineData("Escreva para contato@empresa.com.br.", MotivoBloqueio.DadoProtegidoNaResposta)]
    [InlineData("Continue em www.outro-site.com.", MotivoBloqueio.UrlNaoPermitida)]
    [InlineData("Ligue para (11) 98888-7777.", MotivoBloqueio.TelefoneNaoAutorizado)]
    [InlineData("Me chamo Ana e vou te atender.", MotivoBloqueio.AutodeclaracaoHumana)]
    [InlineData("Responda:\n1 - Financeiro\n2 - Agenda", MotivoBloqueio.MenuOuOpcoes)]
    public void Aviso_de_transferencia_com_conteudo_proibido_nao_sai(string aviso, MotivoBloqueio esperado)
    {
        var conversa = NovaConversa();
        var transicao = conversa.IniciarTransferencia("bloqueio");

        var r = ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, Politica(p => p.Aviso = aviso));

        Assert.Equal(esperado, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public void Aviso_neutro_sem_prazo_nem_compromisso_e_aprovado()
    {
        var politica = Politica(p => p.Aviso = "Vou encaminhar sua mensagem para nossa equipe.");

        Assert.True(ValidadorEnvio.ValidarTextoFixo(politica.TextoAvisoTransferencia, politica).Aprovado);
    }

    // F3 (decisão do Victor, 2026-11-25): tratamento proporcional ao risco. Identificação e financeiro nunca saem,
    // mesmo que estejam numa fonte; dado comum (e-mail) só sai se constar numa fonte citada.
    [Theory]
    [Trait("Regra", "INV-23")]
    [Trait("Regra", "INV-11")]
    [InlineData("Seu CPF cadastrado é 529.982.247-25.")]
    [InlineData("O cartão 4111 1111 1111 1111 está cadastrado.")]
    public void Dado_de_identificacao_ou_financeiro_nunca_sai_mesmo_estando_na_fonte(string texto)
    {
        var fonte = Fonte("horario", texto);

        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.DadoProtegidoNaResposta, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    public void Email_que_consta_na_fonte_citada_e_autorizado_e_seu_dominio_nao_conta_como_url()
    {
        const string texto = "Para segunda via, escreva para financeiro@empresa.com.br.";
        var fonte = Fonte("horario", "Segunda via de boleto: financeiro@empresa.com.br.");

        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(p => p.Emails.Add("financeiro@empresa.com.br")), fonte));

        Assert.True(r.Aprovado, r.Motivo?.ToString());
    }

    [Fact]
    [Trait("Regra", "INV-23")]
    [Trait("Regra", "INV-25")]
    public void Email_da_fonte_fora_da_lista_institucional_e_pessoal_e_bloqueado()
    {
        // Decisão 29: só os e-mails institucionais da política são da empresa, mesmo que uma fonte aprovada traga outro.
        const string texto = "Para segunda via, escreva para joao.silva@gmail.com.";
        var fonte = Fonte("horario", "Segunda via de boleto: joao.silva@gmail.com.");

        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(p => p.Emails.Add("financeiro@empresa.com.br")), fonte));

        Assert.Equal(MotivoBloqueio.DadoProtegidoNaResposta, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "INV-23")]
    [InlineData("Escreva para outro@empresa.com.br.")]
    [InlineData("Escreva para financeiro@outra-empresa.com.br.")]
    // Revisão da F3 (G1): parte de um e-mail autorizado não é o e-mail autorizado.
    [InlineData("Escreva para ro@empresa.com.br.")]
    [InlineData("Escreva para financeiro@empresa.com.")]
    [InlineData("Escreva para financeiro@empresa.com.br.golpe.com.")]
    public void Email_que_nao_consta_na_fonte_citada_e_bloqueado(string texto)
    {
        var fonte = Fonte("horario", "Segunda via de boleto: financeiro@empresa.com.br.");
        // Todos os endereços na lista institucional: o bloqueio vem só da comparação exata com a fonte.
        var politica = Politica(p => p.Emails.UnionWith(["financeiro@empresa.com.br", "outro@empresa.com.br", "financeiro@outra-empresa.com.br",
            "ro@empresa.com.br", "financeiro@empresa.com", "financeiro@empresa.com.br.golpe.com"]));

        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), politica, fonte));

        Assert.Equal(MotivoBloqueio.DadoProtegidoNaResposta, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "INV-8")]
    [InlineData("Seu pedido já foi resolvido.")]
    [InlineData("Está tudo certo com o seu cadastro.")]
    [InlineData("O atendimento foi concluído.")]
    public void Com_pendencia_de_retomada_a_IA_nao_afirma_que_o_assunto_foi_resolvido(string texto)
    {
        var fonte = Fonte("horario", texto);
        var comPendencia = NovaConversa();
        comPendencia.MarcarRetomadaHumana();

        var bloqueado = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(comPendencia, Politica(), fonte));
        var semPendencia = ValidadorEnvio.Validar(Rascunho(texto, "horario", fonte), Contexto(NovaConversa(), Politica(), fonte));

        Assert.Equal(MotivoBloqueio.ResolucaoComPendencia, bloqueado.Motivo);
        Assert.True(semPendencia.Aprovado, semPendencia.Motivo?.ToString());
    }

    // ---- Achados da revisão de segurança da F1a ----

    [Theory]
    [Trait("Regra", "ADV-13")]
    [Trait("Regra", "CTX-016")]
    [InlineData("s\u043Eu humano")]          // o cirílico
    [InlineData("est\u0430 g\u0430rantido")] // a cirílico
    [InlineData("sou hu\u2066mano")]         // isolamento bidi
    [InlineData("sou hu\U000E0041mano")]     // caractere de tag
    [InlineData("linha um\u2028linha dois")] // separador de linha Unicode
    [InlineData("sou hu\u200Bmano")]         // largura zero
    [InlineData("garant\u0131do")]           // i sem pingo (Latin Extended-A)
    public void Caracteres_fora_do_portugues_tornam_a_resposta_suspeita(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.CaracterNaoPermitido, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-02")]
    [Trait("Regra", "CTX-003")]
    [InlineData("A consulta custa R$ 150.000,00.")]  // números soltos (150 e 0) estão na fonte
    [InlineData("A consulta custa R$ 150.")]          // valor diferente do que está na fonte
    [InlineData("A consulta custa cento e vinte reais.")]
    [InlineData("A consulta sai dez por cento mais barata.")]
    public void Valores_recombinados_ou_por_extenso_sem_fonte_sao_bloqueados(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "precos", Precos), Contexto(NovaConversa(), Politica(), Precos));

        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "ADV-03")]
    [Trait("Regra", "CTX-003")]
    public void Mesmo_numero_com_outra_unidade_nao_e_sustentado_pela_fonte()
    {
        var r = ValidadorEnvio.Validar(Rascunho("Parcelamos em 18 vezes.", "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.ValorSemFonte, r.Motivo);
    }

    [Fact]
    [Trait("Regra", "INV-8")]
    public void Valor_identico_ao_da_fonte_seguido_de_texto_comum_e_aprovado()
    {
        var r = ValidadorEnvio.Validar(Rascunho("A consulta custa R$ 150,00 e pode ser paga no local.", "precos", Precos), Contexto(NovaConversa(), Politica(), Precos));

        Assert.True(r.Aprovado, r.Motivo + " " + r.Detalhe);
    }

    [Theory]
    [Trait("Regra", "ADV-12")]
    [Trait("Regra", "CTX-009")]
    [InlineData("Está assegurado.")]
    [InlineData("Pode vir que é de graça.")]
    [InlineData("Você está isento da taxa.")]
    [InlineData("Te atendemos semana que vem.")]
    [InlineData("Fazemos o reembolso integral.")]
    public void Compromissos_por_sinonimo_sem_fonte_sao_bloqueados(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.CompromissoSemFonte, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-13")]
    [Trait("Regra", "CTX-015")]
    [InlineData("Sou de carne e osso, pode confiar.")]
    [InlineData("Não sou robozinho, viu?")]
    [InlineData("Olá! Meu nome é Ana.")]
    public void Autodeclaracao_humana_disfarcada_e_bloqueada(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.AutodeclaracaoHumana, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "ADV-17")]
    [InlineData("Pague em https://empresa.com.br.golpe.top/pix")] // sufixo enganoso do domínio permitido
    [InlineData("Veja em golpe.top")]
    [InlineData("Veja em golpe.click")]
    [InlineData("Acesse golpe[.]com")]
    [InlineData("Acesse golpe ponto com")]
    public void Urls_disfarcadas_ou_com_dominio_enganoso_sao_bloqueadas(string texto)
    {
        var politica = Politica(p => p.Dominios.Add("empresa.com.br"));
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), politica, Horario));

        Assert.Equal(MotivoBloqueio.UrlNaoPermitida, r.Motivo);
    }

    [Theory]
    [Trait("Regra", "INV-1")]
    [InlineData("Temos:\n- horários\n- preços")]
    [InlineData("Escolha:\nf) horários\ng) preços")]
    [InlineData("Marque a letra b.")]
    public void Listas_com_marcadores_ou_letras_sao_menu(string texto)
    {
        var r = ValidadorEnvio.Validar(Rascunho(texto, "horario", Horario), Contexto(NovaConversa(), Politica(), Horario));

        Assert.Equal(MotivoBloqueio.MenuOuOpcoes, r.Motivo);
    }
}
