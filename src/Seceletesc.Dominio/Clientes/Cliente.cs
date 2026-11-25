namespace Seceletesc.Dominio.Clientes;

/// <summary>Cliente final de um tenant, identificado pelo wa_id do WhatsApp dentro do tenant.</summary>
public sealed class Cliente
{
    private Cliente()
    {
        WaId = string.Empty;
    }

    public Cliente(Guid id, Guid tenantId, string waId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waId);
        Id = id;
        TenantId = tenantId;
        WaId = waId;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public string WaId { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }
}
