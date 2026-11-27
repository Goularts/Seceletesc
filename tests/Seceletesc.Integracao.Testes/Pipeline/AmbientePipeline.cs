using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Seceletesc.Infraestrutura.Banco;
using NpgsqlTypes;
using Seceletesc.Aplicacao.Envio;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Contratos;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Infraestrutura.Criptografia;
using Seceletesc.Infraestrutura.Nucleo;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Integracao.Testes.Pipeline;

/// <summary>Relógio controlado pelo teste.</summary>
internal sealed class RelogioFalso(DateTimeOffset inicio) : TimeProvider
{
    public DateTimeOffset Agora { get; set; } = inicio;

    public override DateTimeOffset GetUtcNow() => Agora;
}

/// <summary>Motor de IA simulado: devolve o que o teste mandar e guarda cada contexto recebido.</summary>
internal sealed class MotorFalso(Func<ContextoModelo, Task<string>> responder) : IMotorIA
{
    public List<ContextoModelo> Contextos { get; } = [];

    public async Task<string> GerarRascunhoAsync(ContextoModelo contexto, CancellationToken ct)
    {
        Contextos.Add(contexto);
        return await responder(contexto).ConfigureAwait(false);
    }

    /// <summary>Rascunho citando todos os conhecimentos do assunto recebidos no contexto.</summary>
    public static string Rascunho(ContextoModelo ctx, string texto, string assunto) =>
        JsonSerializer.Serialize(new
        {
            texto,
            fontes = ctx.Conhecimentos.Where(k => k.Assunto == assunto).Select(k => new { item = k.ItemId, versao = k.Versao }),
            assunto,
        });
}

/// <summary>Gateway simulado: registra o que foi enviado e devolve os resultados configurados.</summary>
internal sealed class GatewayFalso : IGatewayWhatsApp
{
    public Queue<Func<ResultadoEnvio>> Respostas { get; } = new();

    public List<(string WaId, string Texto)> Enviadas { get; } = [];

    /// <summary>Número de origem recebido em cada chamada (M1: o número em que o cliente escreveu por último).</summary>
    public List<string?> NumerosOrigem { get; } = [];

    public Task<ResultadoEnvio> EnviarTextoAsync(Guid tenantId, string? numeroOrigem, string waIdDestino, string texto, CancellationToken ct)
    {
        NumerosOrigem.Add(numeroOrigem);
        var r = Respostas.Count > 0 ? Respostas.Dequeue()() : new ResultadoEnvio(SituacaoEnvio.Enviada, "wamid.saida." + Guid.NewGuid(), null);
        if (r.Situacao is SituacaoEnvio.Enviada or SituacaoEnvio.Incerta)
        {
            Enviadas.Add((waIdDestino, texto));
        }

        return Task.FromResult(r);
    }
}

/// <summary>
/// Monta um tenant isolado por teste, com política e conhecimento semeados pelo administrador
/// (como faria a supervisão humana), e expõe o orquestrador e o worker com os papéis reais da aplicação.
/// </summary>
internal sealed class AmbientePipeline : IAsyncDisposable
{
    public static readonly DateTimeOffset Inicio = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    public const string WaCliente = "5511988887777";

    private static readonly Lazy<(string Publica, string Privada)> ChavesRsa = new(() =>
    {
        using var rsa = RSA.Create(3072);
        return (rsa.ExportSubjectPublicKeyInfoPem(), rsa.ExportPkcs8PrivateKeyPem());
    });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Credenciais = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> CredencialAdministrador = new();

    private readonly BancoFixture _banco;
    private readonly CifradorDadosProtegidos _cifrador;

    /// <summary>
    /// Chave para comprovar identidade nos testes que escrevem direto no banco com o papel da supervisão: a do próprio
    /// usuário, se ele foi criado por este ambiente; senão, a do administrador do tenant (para testar a regra do
    /// trigger, e não a falta de identidade).
    /// </summary>
    public static string CredencialPara(Guid tenant, Guid? usuario) =>
        usuario is { } u && Credenciais.TryGetValue(u, out var k) ? k : CredencialAdministrador[tenant];

    /// <summary>
    /// Executa no banco com o papel do data source. Com o papel da supervisão, comprova a identidade antes
    /// (ver <see cref="CredencialPara"/>), como a API faz; os demais papéis não têm identidade humana.
    /// </summary>
    public static Task<T> ExecutarComPapelAsync<T>(
        NpgsqlDataSource ds, Guid tenant, Guid? usuario, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> trabalho)
    {
        ArgumentNullException.ThrowIfNull(ds);
        var executor = new ExecutorTenant(ds);
        return new NpgsqlConnectionStringBuilder(ds.ConnectionString).Username == "supervisao_teste"
            ? executor.ExecutarAutenticadoAsync(tenant, CredencialPara(tenant, usuario), trabalho)
            : executor.ExecutarAsync(tenant, trabalho);
    }

    private AmbientePipeline(BancoFixture banco, Guid tenantId, Guid supervisorId, string chaveSupervisor)
    {
        _banco = banco;
        TenantId = tenantId;
        _cifrador = new CifradorDadosProtegidos(ChavesRsa.Value.Publica);
        App = banco.CriarDataSource("app_teste", maxPool: 4);
        WorkerDs = banco.CriarDataSource("worker_teste", maxPool: 4);
        SupervisaoDs = banco.CriarDataSource("supervisao_teste", maxPool: 4);
        Supervisor = new UsuarioSupervisao(supervisorId, tenantId, PapelSupervisao.Administrador) { Credencial = chaveSupervisor };
        ChaveSupervisor = chaveSupervisor;
        Credenciais[supervisorId] = chaveSupervisor;
        CredencialAdministrador[tenantId] = chaveSupervisor;
        Supervisao = new ServicoSupervisao(SupervisaoDs, Relogio);
        Conhecimento = new ServicoConhecimento(SupervisaoDs, Relogio);
    }

    public Guid TenantId { get; }

    public RelogioFalso Relogio { get; } = new(Inicio);

    public GatewayFalso Gateway { get; } = new();

    public NpgsqlDataSource App { get; }

    public NpgsqlDataSource WorkerDs { get; }

    /// <summary>Papel real da supervisão humana (seceletesc_supervisao), não o administrador.</summary>
    public NpgsqlDataSource SupervisaoDs { get; }

    public ServicoSupervisao Supervisao { get; }

    public ServicoConhecimento Conhecimento { get; }

    /// <summary>Usuário padrão do ambiente: administrador (tem todas as permissões). Papéis menores: CriarUsuarioAsync.</summary>
    public UsuarioSupervisao Supervisor { get; }

    /// <summary>Chave de API do usuário padrão (administrador) deste tenant.</summary>
    public string ChaveSupervisor { get; }

    public BancoFixture Banco => _banco;

    public static string ChavePrivadaPem => ChavesRsa.Value.Privada;

    public static string ChavePublicaPem => ChavesRsa.Value.Publica;

    public static async Task<AmbientePipeline> CriarAsync(BancoFixture banco, Action<PoliticaTenantBuilder>? ajustePolitica = null)
    {
        var tenantId = Guid.NewGuid();
        var b = new PoliticaTenantBuilder();
        ajustePolitica?.Invoke(b);
        var politica = new PoliticaTenant
        {
            TenantId = tenantId,
            Versao = 1,
            AssuntosAutonomos = b.Autonomos,
            AssuntosExigemHumano = b.ExigemHumano,
            ClassificacoesPermitidasRespostaAutomatica = b.Classificacoes,
            TextoAvisoTransferencia = b.Aviso,
            EmailsInstitucionais = b.Emails,
        };

        await using var c = new NpgsqlConnection(banco.ConnectionStringAdmin);
        await c.OpenAsync();
        // A fila de envio é global (o worker atende todas as empresas). Para isolar os testes,
        // itens deixados por testes anteriores ficam indisponíveis.
        await using (var cmd = new NpgsqlCommand("""
            update fila_envio set disponivel_em = now() + interval '100 years';
            update fila_ia set disponivel_em = now() + interval '100 years';
            insert into tenants (id, nome) values (@t, 'Empresa de teste');
            """, c))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            await cmd.ExecuteNonQueryAsync();
        }

        if (!b.SemPolitica)
        {
            await using var pol = new NpgsqlCommand("insert into politicas_tenant (tenant_id, versao, conteudo) values (@t, 1, @c::jsonb)", c);
            pol.Parameters.AddWithValue("t", tenantId);
            pol.Parameters.AddWithValue("c", PoliticaJson.Serializar(politica));
            await pol.ExecuteNonQueryAsync();
        }

        var supervisorId = Guid.NewGuid();
        var chave = ServicoSupervisao.GerarChave();
        await using (var sup = new NpgsqlCommand("insert into usuarios_supervisao (id, tenant_id, nome, chave_sha256, papel) values (@id, @t, 'Administrador de teste', @h, 'Administrador')", c))
        {
            sup.Parameters.AddWithValue("id", supervisorId);
            sup.Parameters.AddWithValue("t", tenantId);
            sup.Parameters.AddWithValue("h", ServicoSupervisao.HashChave(chave));
            await sup.ExecuteNonQueryAsync();
        }

        return new AmbientePipeline(banco, tenantId, supervisorId, chave);
    }

    /// <summary>Cifrador com a chave pública de teste (o mesmo do pipeline e do worker).</summary>
    public CifradorDadosProtegidos Cifrador => _cifrador;

    public ProcessadorMensagemRecebida Orquestrador(IMotorIA motor, TimeSpan? idadeMaximaNaFila = null) =>
        new(App, motor, _cifrador, Relogio, idadeMaximaNaFila: idadeMaximaNaFila);

    public ProcessadorEnvio Worker() => new(WorkerDs, Gateway, Relogio, _cifrador);

    /// <summary>Novo usuário da supervisão neste tenant, com o papel pedido, e a chave de API dele.</summary>
    public async Task<(UsuarioSupervisao Usuario, string Chave)> CriarUsuarioAsync(PapelSupervisao papel)
    {
        var (id, chave) = await AdministracaoSupervisao.CriarSupervisorAsync(_banco.ConnectionStringDono, TenantId, "Usuário " + papel, papel);
        Credenciais[id] = chave;
        return (new UsuarioSupervisao(id, TenantId, papel) { Credencial = chave }, chave);
    }

    public MensagemRecebida Mensagem(string texto, string? wamid = null, DateTimeOffset? em = null) =>
        new(TenantId, WaCliente, texto, wamid ?? "wamid.entrada." + Guid.NewGuid(), em ?? Relogio.Agora);

    /// <summary>
    /// Semeia conhecimento pelo mesmo ciclo da supervisão (migração 0004): sugestão Rev (versão 1, proposta) e, se
    /// pedido, versão 2 aprovada por um administrador da empresa. Devolve a versão vigente.
    /// </summary>
    public async Task<ChaveFonte> ConhecimentoAsync(
        string assunto, string conteudo, Classificacao[]? classificacoes = null, TipoEscopo escopo = TipoEscopo.Empresa,
        Guid? escopoRef = null, DateTimeOffset? validoAte = null, string status = "Aprovado", Guid? tenant = null)
    {
        var itemId = Guid.NewGuid();
        var t = tenant ?? TenantId;
        await using var c = new NpgsqlConnection(_banco.ConnectionStringAdmin);
        await c.OpenAsync();
        await DefinirTenantNaSessaoAsync(c, t);
        var aprovador = await AdministradorDoTenantAsync(c, t);
        await using var tx = await c.BeginTransactionAsync();
        await ComprovarIdentidadeAsync(c, tx, aprovador, t);

        async Task Executar(string sql, int versao)
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            cmd.Parameters.AddWithValue("i", itemId);
            cmd.Parameters.AddWithValue("t", t);
            cmd.Parameters.AddWithValue("a", assunto);
            cmd.Parameters.AddWithValue("c", conteudo);
            cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = (classificacoes ?? [Classificacao.Publico]).Select(x => x.ToString()).ToArray() });
            cmd.Parameters.AddWithValue("e", escopo.ToString());
            cmd.Parameters.AddWithValue("er", (object?)escopoRef ?? DBNull.Value);
            cmd.Parameters.AddWithValue("v", (object?)validoAte ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", aprovador);
            cmd.Parameters.AddWithValue("ver", versao);
            await cmd.ExecuteNonQueryAsync();
        }

        await Executar("""
            insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (@i, @t, @a, 'Rev', 1);
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem)
            values (@i, 1, @t, @c, @cl, @e, @er, @v, 'teste');
            """, 1);
        if (status == "Rev")
        {
            await tx.CommitAsync();
            return new ChaveFonte(itemId, 1);
        }

        if (status == "Rejeitado")
        {
            await Executar("update itens_conhecimento set status = 'Rejeitado', alterado_por = @u where id = @i", 1);
            await tx.CommitAsync();
            return new ChaveFonte(itemId, 1);
        }

        await Executar("""
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em)
            values (@i, @ver, @t, @c, @cl, @e, @er, @v, 'teste', @u, now());
            update itens_conhecimento set status = 'Aprovado', versao_vigente = @ver, alterado_por = @u where id = @i;
            """, 2);
        if (status == "Revogado")
        {
            await Executar("update itens_conhecimento set status = 'Revogado', alterado_por = @u where id = @i", 2);
        }

        await tx.CommitAsync();
        return new ChaveFonte(itemId, 2);
    }

    public async Task RevogarAsync(ChaveFonte fonte)
    {
        await using var c = new NpgsqlConnection(_banco.ConnectionStringAdmin);
        await c.OpenAsync();
        var tenant = (Guid)(await new NpgsqlCommand($"select tenant_id from itens_conhecimento where id = '{fonte.ItemId}'", c).ExecuteScalarAsync())!;
        await DefinirTenantNaSessaoAsync(c, tenant);
        var admin = await AdministradorDoTenantAsync(c, tenant);
        await using var tx = await c.BeginTransactionAsync();
        await ComprovarIdentidadeAsync(c, tx, admin, tenant);
        await using var cmd = new NpgsqlCommand("update itens_conhecimento set status = 'Revogado', alterado_por = @u where id = @x", c, tx);
        cmd.Parameters.AddWithValue("u", admin);
        cmd.Parameters.AddWithValue("x", fonte.ItemId);
        await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>
    /// A semeadura roda como superusuário e não tem a chave do administrador semeado: grava direto a identidade da
    /// transação (migração 0005), como faria autenticar_transacao. Os papéis da aplicação não conseguem fazer isso.
    /// </summary>
    internal static async Task ComprovarIdentidadeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid usuario, Guid tenant)
    {
        await using var cmd = new NpgsqlCommand("insert into autenticacoes_transacao (xid, usuario_id, tenant_id) values (pg_current_xact_id(), @u, @t)", c, tx);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("t", tenant);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>As funções de papel só respondem pela empresa da transação (migração 0004), também na semeadura.</summary>
    private static async Task DefinirTenantNaSessaoAsync(NpgsqlConnection c, Guid tenant)
    {
        await using var cmd = new NpgsqlCommand("select set_config('app.tenant_id', @t, false)", c);
        cmd.Parameters.AddWithValue("t", tenant.ToString());
        await cmd.ExecuteScalarAsync();
    }

    /// <summary>Um administrador ativo da empresa (criado se a empresa ainda não tiver).</summary>
    private static async Task<Guid> AdministradorDoTenantAsync(NpgsqlConnection c, Guid tenant)
    {
        await using (var sel = new NpgsqlCommand("select id from usuarios_supervisao where tenant_id = @t and papel = 'Administrador' and ativo limit 1", c))
        {
            sel.Parameters.AddWithValue("t", tenant);
            if (await sel.ExecuteScalarAsync() is Guid existente)
            {
                return existente;
            }
        }

        var id = Guid.NewGuid();
        await using var ins = new NpgsqlCommand("insert into usuarios_supervisao (id, tenant_id, nome, chave_sha256, papel) values (@id, @t, 'Administrador semeado', @h, 'Administrador')", c);
        ins.Parameters.AddWithValue("id", id);
        ins.Parameters.AddWithValue("t", tenant);
        ins.Parameters.AddWithValue("h", ServicoSupervisao.HashChave(ServicoSupervisao.GerarChave()));
        await ins.ExecuteNonQueryAsync();
        return id;
    }

    public async Task<Guid> ConversaIdAsync() =>
        (await ConsultarAsync<Guid?>("select id from conversas where tenant_id = @tenant")) ?? throw new InvalidOperationException("Sem conversa.");

    public Task<long> VersaoConversaAsync() => ConsultarAsync<long>("select versao_estado from conversas where tenant_id = @tenant");

    /// <summary>Humano assume a conversa pela supervisão (papel seceletesc_supervisao).</summary>
    public async Task AssumirAsync()
    {
        var r = await Supervisao.AssumirAsync(Supervisor, await ConversaIdAsync());
        Assert.Equal(SituacaoOperacao.Ok, r.Situacao);
    }

    /// <summary>Humano libera a conversa de volta para a IA pela supervisão.</summary>
    public async Task LiberarAsync()
    {
        var r = await Supervisao.LiberarAsync(Supervisor, await ConversaIdAsync(), await VersaoConversaAsync());
        Assert.Equal(SituacaoOperacao.Ok, r.Situacao);
    }

    public async Task<T?> ConsultarAsync<T>(string sql)
    {
        await using var c = new NpgsqlConnection(_banco.ConnectionStringAdmin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql.Replace("@tenant", $"'{TenantId}'", StringComparison.Ordinal), c);
        var r = await cmd.ExecuteScalarAsync();
        return r is null or DBNull ? default : (T)r;
    }

    public Task<string?> EstadoConversaAsync() => ConsultarAsync<string>("select estado from conversas where tenant_id = @tenant");

    public Task<long> ContarAsync(string tabelaEFiltro) => ConsultarAsync<long>($"select count(*) from {tabelaEFiltro}");

    public async ValueTask DisposeAsync()
    {
        _cifrador.Dispose();
        await App.DisposeAsync();
        await WorkerDs.DisposeAsync();
        await SupervisaoDs.DisposeAsync();
    }

    private async Task AdminAsync(string sql, Guid x)
    {
        await using var c = new NpgsqlConnection(_banco.ConnectionStringAdmin);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        cmd.Parameters.AddWithValue("x", x);
        await cmd.ExecuteNonQueryAsync();
    }
}

internal sealed class PoliticaTenantBuilder
{
    public HashSet<string> Autonomos { get; } = ["horario", "precos"];

    public HashSet<string> ExigemHumano { get; } = ["desconto"];

    public HashSet<Classificacao> Classificacoes { get; } = [Classificacao.Publico];

    public string Aviso { get; set; } = "Vou encaminhar sua mensagem para nossa equipe.";

    public bool SemPolitica { get; set; }

    public HashSet<string> Emails { get; } = [];
}
