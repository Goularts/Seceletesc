using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using Testcontainers.PostgreSql;

namespace Seceletesc.Integracao.Testes;

/// <summary>
/// PostgreSQL real em container, montado como em produção:
/// o administrador (superusuário do container) só provisiona os papéis e semeia dados de teste;
/// as migrações rodam com seceletesc_dono (sem superusuário e sem BYPASSRLS);
/// os testes usam usuários de login nos papéis seceletesc_app e seceletesc_worker.
/// Superusuário ignora RLS, por isso nenhum teste de isolamento usa o administrador.
/// </summary>
public sealed class BancoFixture : IAsyncLifetime
{
    public const string SenhaTeste = "senha-somente-teste";

    // Imagem fixada por digest (plano técnico, seção 4.10).
    private const string ImagemPostgres = "postgres:17-alpine@sha256:f9cf3e1f0066f0eb0c48707d864f0ac5941e9ff454a072077d97a4d278d07d73";

    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid ClienteA = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000c1");
    public static readonly Guid ClienteB = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000c2");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(ImagemPostgres).Build();

    public string ConnectionStringAdmin => _container.GetConnectionString();

    public string ConnectionStringDono => ConnectionString("seceletesc_dono");

    public NpgsqlDataSource App { get; private set; } = null!;

    public NpgsqlDataSource Worker { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await ExecutarComoAdminAsync(Provisionamento.ScriptPapeis());
        await ExecutarComoAdminAsync($$"""
            alter role seceletesc_dono password '{{SenhaTeste}}';
            create role app_teste login password '{{SenhaTeste}}' in role seceletesc_app;
            create role worker_teste login password '{{SenhaTeste}}' in role seceletesc_worker;
            """);

        await Migrador.AplicarAsync(ConnectionStringDono);

        // Dados de teste semeados pelo administrador (fora do RLS de propósito).
        await ExecutarComoAdminAsync($$"""
            insert into tenants (id, nome) values ('{{TenantA}}', 'Empresa A'), ('{{TenantB}}', 'Empresa B');
            insert into tenant_numeros_whatsapp (phone_number_id, tenant_id) values ('pn-a', '{{TenantA}}'), ('pn-b', '{{TenantB}}');
            insert into clientes (id, tenant_id, wa_id) values ('{{ClienteA}}', '{{TenantA}}', '5511900000001'), ('{{ClienteB}}', '{{TenantB}}', '5511900000002');
            insert into eventos_historico (tenant_id, tipo, dados) values ('{{TenantA}}', 'mensagem_recebida', '{"origem":"A"}'), ('{{TenantB}}', 'mensagem_recebida', '{"origem":"B"}');
            """);

        App = CriarDataSource("app_teste", maxPool: 1);
        Worker = CriarDataSource("worker_teste", maxPool: 1);
    }

    /// <param name="semResetAoDevolver">
    /// Desliga o DISCARD ALL que o Npgsql faz ao devolver a conexão ao pool (como atrás de um pooler externo).
    /// Assim o teste prova que o isolamento vem do SET LOCAL, e não da limpeza do driver.
    /// </param>
    public NpgsqlDataSource CriarDataSource(string usuario, int maxPool, bool semResetAoDevolver = false)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString(usuario))
        {
            MaxPoolSize = maxPool,
            NoResetOnClose = semResetAoDevolver,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }

    public async Task<object?> ConsultarComoAdminAsync(string sql)
    {
        await using var conexao = new NpgsqlConnection(ConnectionStringAdmin);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexao);
        return await cmd.ExecuteScalarAsync();
    }

    public async Task DisposeAsync()
    {
        await App.DisposeAsync();
        await Worker.DisposeAsync();
        await _container.DisposeAsync();
    }

    private string ConnectionString(string usuario) =>
        new NpgsqlConnectionStringBuilder(ConnectionStringAdmin) { Username = usuario, Password = SenhaTeste }.ConnectionString;

    private async Task ExecutarComoAdminAsync(string sql)
    {
        await using var conexao = new NpgsqlConnection(ConnectionStringAdmin);
        await conexao.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conexao);
        await cmd.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Nome)]
public sealed class ColecaoBanco : ICollectionFixture<BancoFixture>
{
    public const string Nome = "banco";
}
