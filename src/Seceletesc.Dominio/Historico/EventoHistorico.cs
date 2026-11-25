namespace Seceletesc.Dominio.Historico;

/// <summary>Evento do histórico de atendimento. Append-only: nunca é alterado nem removido (invariante 15).</summary>
public sealed class EventoHistorico
{
    private EventoHistorico()
    {
        Tipo = string.Empty;
        Dados = "{}";
    }

    public EventoHistorico(Guid tenantId, Guid? conversaId, string tipo, string dadosJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tipo);
        ArgumentException.ThrowIfNullOrWhiteSpace(dadosJson);
        TenantId = tenantId;
        ConversaId = conversaId;
        Tipo = tipo;
        Dados = dadosJson;
    }

    public long Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid? ConversaId { get; private set; }

    public string Tipo { get; private set; }

    /// <summary>Payload JSON do evento.</summary>
    public string Dados { get; private set; }

    public DateTimeOffset OcorridoEm { get; private set; }
}
