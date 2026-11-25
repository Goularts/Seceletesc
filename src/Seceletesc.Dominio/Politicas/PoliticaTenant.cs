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

    /// <summary>Classificações que podem ir ao modelo e aparecer em resposta automática. Padrão: só PUB.</summary>
    public IReadOnlySet<Classificacao> ClassificacoesPermitidasRespostaAutomatica { get; init; } = new HashSet<Classificacao> { Classificacao.Publico };

    /// <summary>Texto fixo do aviso de transferência. Vazio = não envia aviso.</summary>
    public string TextoAvisoTransferencia { get; init; } = string.Empty;

    /// <summary>Termos de compromisso sem número ("aprovado", "garantido", "amanhã"): exigem fonte que os contenha.</summary>
    public IReadOnlyList<string> TermosCompromisso { get; init; } = TermosCompromissoPadrao;

    /// <summary>Domínios permitidos em URLs de respostas automáticas. Vazio = nenhuma URL.</summary>
    public IReadOnlySet<string> DominiosPermitidos { get; init; } = new HashSet<string>();

    /// <summary>Minimização: conhecimento CLI vai ao modelo? Padrão: não.</summary>
    public bool EnviarConhecimentoClienteAoModelo { get; init; }

    /// <summary>Validade de uma mensagem automática na fila de envio.</summary>
    public TimeSpan ValidadeMensagemAutomatica { get; init; } = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string> TermosCompromissoPadrao { get; } =
    [
        "aprovado", "aprovada", "garantido", "garantida", "garantimos", "confirmado", "confirmada",
        "reservado", "reservada", "prometo", "prometemos", "sem custo", "gratis", "gratuito", "desconto",
        "amanha", "hoje", "agora mesmo", "ainda hoje",
    ];
}
