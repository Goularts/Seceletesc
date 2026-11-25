namespace Seceletesc.Dominio.Tenants;

/// <summary>Empresa atendida pela plataforma. Todos os dados de negócio pertencem a um tenant.</summary>
public sealed class Tenant
{
    private Tenant()
    {
        Nome = string.Empty;
    }

    public Tenant(Guid id, string nome)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        Id = id;
        Nome = nome;
    }

    public Guid Id { get; private set; }

    public string Nome { get; private set; }

    public DateTimeOffset CriadoEm { get; private set; }
}
