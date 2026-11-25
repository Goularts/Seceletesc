using Microsoft.EntityFrameworkCore;
using Seceletesc.Dominio.Clientes;
using Seceletesc.Dominio.Historico;
using Seceletesc.Dominio.Tenants;

namespace Seceletesc.Infraestrutura.Persistencia;

/// <summary>
/// Contexto EF Core vinculado a um único tenant. O filtro global por tenant é a segunda camada de
/// isolamento; a primeira é o RLS do PostgreSQL. Use sempre via <see cref="UnidadeDeTrabalhoTenant"/>.
/// </summary>
public sealed class SeceletescDbContext : DbContext
{
    public SeceletescDbContext(DbContextOptions<SeceletescDbContext> options, Guid tenantId)
        : base(options)
    {
        TenantId = tenantId;
    }

    public Guid TenantId { get; }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<Cliente> Clientes => Set<Cliente>();

    public DbSet<EventoHistorico> Eventos => Set<EventoHistorico>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Nome).HasColumnName("nome");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.Id == TenantId);
        });

        modelBuilder.Entity<Cliente>(e =>
        {
            e.ToTable("clientes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.WaId).HasColumnName("wa_id");
            e.Property(x => x.CriadoEm).HasColumnName("criado_em").ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.TenantId == TenantId);
        });

        modelBuilder.Entity<EventoHistorico>(e =>
        {
            e.ToTable("eventos_historico");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ConversaId).HasColumnName("conversa_id");
            e.Property(x => x.Tipo).HasColumnName("tipo");
            e.Property(x => x.Dados).HasColumnName("dados").HasColumnType("jsonb");
            e.Property(x => x.OcorridoEm).HasColumnName("ocorrido_em").ValueGeneratedOnAdd();
            e.HasQueryFilter(x => x.TenantId == TenantId);
        });
    }
}
