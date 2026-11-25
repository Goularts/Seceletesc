using Microsoft.EntityFrameworkCore;
using Npgsql;
using Seceletesc.Infraestrutura.Banco;

namespace Seceletesc.Infraestrutura.Persistencia;

/// <summary>
/// Abre conexão e transação, define o tenant com SET LOCAL e entrega um <see cref="SeceletescDbContext"/>
/// preso a essa transação. Sem tenant, não há contexto.
/// </summary>
public sealed class UnidadeDeTrabalhoTenant : IAsyncDisposable
{
    private readonly NpgsqlConnection _conexao;
    private readonly NpgsqlTransaction _tx;

    private UnidadeDeTrabalhoTenant(NpgsqlConnection conexao, NpgsqlTransaction tx, SeceletescDbContext contexto)
    {
        _conexao = conexao;
        _tx = tx;
        Contexto = contexto;
    }

    public SeceletescDbContext Contexto { get; }

    public static async Task<UnidadeDeTrabalhoTenant> IniciarAsync(NpgsqlDataSource dataSource, Guid tenantId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant não pode ser vazio.", nameof(tenantId));
        }

        var conexao = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var tx = await conexao.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecutorTenant.DefinirTenantAsync(conexao, tx, tenantId, ct).ConfigureAwait(false);

            var opcoes = new DbContextOptionsBuilder<SeceletescDbContext>()
                .UseNpgsql(conexao)
                .EnableSensitiveDataLogging(false)
                .Options;
            var contexto = new SeceletescDbContext(opcoes, tenantId);
            await contexto.Database.UseTransactionAsync(tx, ct).ConfigureAwait(false);

            return new UnidadeDeTrabalhoTenant(conexao, tx, contexto);
        }
        catch
        {
            await conexao.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ConfirmarAsync(CancellationToken ct = default)
    {
        await Contexto.SaveChangesAsync(ct).ConfigureAwait(false);
        await _tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await Contexto.DisposeAsync().ConfigureAwait(false);
        await _tx.DisposeAsync().ConfigureAwait(false);
        await _conexao.DisposeAsync().ConfigureAwait(false);
    }
}
