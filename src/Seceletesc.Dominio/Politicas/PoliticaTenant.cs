using Seceletesc.Dominio.Conhecimento;

namespace Seceletesc.Dominio.Politicas;

/// <summary>
/// Política de atendimento de um tenant, em versão imutável, definida por humanos (plano técnico, seção 4.6).
/// As regras do validador vêm daqui, nunca da conversa (CTX-016). Os padrões são conservadores.
/// </summary>
public sealed record PoliticaTenant
{
    public required Guid TenantId { get; init; }

    public required int Versao { get; init; }

    /// <summary>Assuntos que a IA pode responder sozinha. Lista fechada.</summary>
    public required IReadOnlySet<string> AssuntosAutonomos { get; init; }

    /// <summary>Assuntos que sempre exigem humano. Prevalece sobre <see cref="AssuntosAutonomos"/>.</summary>
    public IReadOnlySet<string> AssuntosExigemHumano { get; init; } = new HashSet<string>();

    private readonly IReadOnlySet<Classificacao> _classificacoes = new HashSet<Classificacao> { Classificacao.Publico };
    private readonly IReadOnlyList<string> _termos = TermosCompromissoPadrao;

    /// <summary>
    /// Classificações que podem ir ao modelo e aparecer em resposta automática. Padrão: só PUB.
    /// Piso (políticas fundamentais, invariante 25): INT e RES nunca entram, mesmo que a política peça.
    /// </summary>
    public IReadOnlySet<Classificacao> ClassificacoesPermitidasRespostaAutomatica
    {
        get => _classificacoes;
        init => _classificacoes = (value ?? throw new ArgumentNullException(nameof(value)))
            .Where(c => !ClassificacoesNuncaAutomaticas.Contains(c)).ToHashSet();
    }

    /// <summary>Informação interna e restrita nunca vai para resposta automática (invariante 25).</summary>
    public static IReadOnlySet<Classificacao> ClassificacoesNuncaAutomaticas { get; } = new HashSet<Classificacao> { Classificacao.Interno, Classificacao.Restrito };

    /// <summary>Texto fixo do aviso de transferência. Vazio = não envia aviso.</summary>
    public string TextoAvisoTransferencia { get; init; } = string.Empty;

    /// <summary>
    /// Termos de compromisso sem número ("aprovado", "garantido", "amanhã"): exigem fonte que os contenha.
    /// Piso (invariante 25): a lista padrão sempre vale; a política só acrescenta termos.
    /// </summary>
    public IReadOnlyList<string> TermosCompromisso
    {
        get => _termos;
        init => _termos = TermosCompromissoPadrao.Concat(value ?? throw new ArgumentNullException(nameof(value)))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Termos que a política acrescentou ao piso (o que é gravado; o piso vem do código).</summary>
    public IReadOnlyList<string> TermosCompromissoAdicionais => _termos.Except(TermosCompromissoPadrao, StringComparer.Ordinal).ToList();

    /// <summary>
    /// E-mails institucionais autorizados (decisão 29), em minúsculas. Qualquer outro e-mail é pessoal: não entra em
    /// conhecimento nem sai em resposta automática.
    /// </summary>
    public IReadOnlySet<string> EmailsInstitucionais { get; init; } = new HashSet<string>();

    /// <summary>
    /// Prazo máximo de validade, em dias, por classificação (decisão 28). Sem prazo definido para uma classificação,
    /// vale só a regra geral (não-PUB exige validade). Aplicado na aprovação.
    /// </summary>
    public IReadOnlyDictionary<Classificacao, int> ValidadeMaximaDias { get; init; } = new Dictionary<Classificacao, int>();

    /// <summary>
    /// CEPs de endereços institucionais da empresa (decisão do Victor, 2026-11-25), só os 8 dígitos. Endereço
    /// institucional pode ser conhecimento público aprovado; qualquer outro CEP é de cliente e fica protegido.
    /// </summary>
    public IReadOnlySet<string> CepsInstitucionais { get; init; } = new HashSet<string>();

    /// <summary>Mensagem parada na fila da IA além disso gera registro de atraso (monitoramento).</summary>
    public TimeSpan AtrasoAvisoFila { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Mensagem parada na fila da IA além disso vai para a supervisão (escalonamento), sem resposta automática.</summary>
    public TimeSpan AtrasoEscalonamentoFila { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>CEP institucional autorizado (compara só os dígitos).</summary>
    public bool EhCepInstitucional(string cep) =>
        CepsInstitucionais.Contains(new string((cep ?? throw new ArgumentNullException(nameof(cep))).Where(char.IsAsciiDigit).ToArray()));

    /// <summary>E-mail institucional autorizado (comparação sem diferenciar maiúsculas).</summary>
    public bool EhEmailInstitucional(string email) =>
        EmailsInstitucionais.Contains((email ?? throw new ArgumentNullException(nameof(email))).Trim().ToLowerInvariant());

    /// <summary>Domínios permitidos em URLs de respostas automáticas. Vazio = nenhuma URL.</summary>
    public IReadOnlySet<string> DominiosPermitidos { get; init; } = new HashSet<string>();

    /// <summary>Minimização: conhecimento CLI vai ao modelo? Padrão: não.</summary>
    public bool EnviarConhecimentoClienteAoModelo { get; init; }

    /// <summary>Validade de uma mensagem automática na fila de envio.</summary>
    public TimeSpan ValidadeMensagemAutomatica { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Padrão técnico conservador (só restringe). O conteúdo precisa ser validado pelo Victor antes do piloto (F7).
    /// Um termo da lista só pode aparecer numa resposta se aparecer numa fonte citada.
    /// </summary>
    public static IReadOnlyList<string> TermosCompromissoPadrao { get; } =
    [
        // Compromisso e aprovação
        "aprovado", "aprovada", "garantido", "garantida", "garantimos", "garantia", "assegurado", "assegurada",
        "asseguramos", "confirmado", "confirmada", "confirmamos", "reservado", "reservada", "liberado", "liberada",
        "prometo", "prometemos", "compromisso", "certeza",
        // Condições comerciais
        "sem custo", "custo zero", "gratis", "gratuito", "gratuita", "de graca", "isento", "isenta", "isencao",
        "desconto", "promocao", "cortesia", "reembolso", "estorno", "devolucao", "parcelamento", "sem juros",
        // Tempo relativo e datas por extenso
        "amanha", "hoje", "agora mesmo", "ainda hoje", "semana que vem", "proxima semana", "mes que vem", "proximo mes",
        "janeiro", "fevereiro", "marco", "abril", "maio", "junho", "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
        "segunda", "terca", "quarta", "quinta", "sexta", "sabado", "domingo", "feriado",
    ];
}
