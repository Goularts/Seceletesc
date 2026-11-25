using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Seceletesc.Dominio.Conversas;
using Seceletesc.Dominio.DadosProtegidos;
using Seceletesc.Dominio.Envio;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Nucleo;

namespace Seceletesc.Infraestrutura.Supervisao;

/// <summary>
/// Papéis humanos (decisão do Victor, 2026-11-25, F3). Cada papel inclui as permissões do anterior.
/// Atendente: vê conversas, assume, responde, orienta e sugere conhecimento.
/// Supervisor: libera conversas, encerra pendências de retomada, revisa, edita, rejeita e aprova conhecimento comum.
/// Administrador: publica a política, aprova conhecimento de maior risco (NEG, EXC, RES, CLI) e revoga.
/// </summary>
public enum PapelSupervisao
{
    Atendente,
    Supervisor,
    Administrador,
}

/// <summary>
/// Usuário humano autenticado. O tenant e o papel vêm daqui, nunca da requisição. A credencial (chave apresentada
/// na requisição) é reapresentada ao banco em cada transação, que comprova a identidade (migração 0005); ela não
/// aparece no ToString nem em logs.
/// </summary>
public sealed record UsuarioSupervisao(Guid Id, Guid TenantId, PapelSupervisao Papel)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Credencial { get; init; }

    public bool PodeGerirAtendimento => Papel >= PapelSupervisao.Supervisor;

    public bool EhAdministrador => Papel == PapelSupervisao.Administrador;

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"Id = {Id}, TenantId = {TenantId}, Papel = {Papel}");
        return true;
    }
}

public enum SituacaoOperacao
{
    Ok,
    NaoEncontrada,
    Conflito,
    Invalida,
    Proibida,
}

public sealed record ResultadoOperacao(SituacaoOperacao Situacao, string? Detalhe = null, long? Versao = null, Guid? Id = null)
{
    public static ResultadoOperacao NaoEncontrada { get; } = new(SituacaoOperacao.NaoEncontrada, "conversa não encontrada");

    public static ResultadoOperacao Conflito(string detalhe, long? versao = null) => new(SituacaoOperacao.Conflito, detalhe, versao);

    public static ResultadoOperacao Invalida(string detalhe) => new(SituacaoOperacao.Invalida, detalhe);

    public static ResultadoOperacao Proibida(string detalhe) => new(SituacaoOperacao.Proibida, detalhe);
}

public sealed record ResumoConversa(
    Guid Id, string WaIdCliente, string Estado, long VersaoEstado, DateTimeOffset? UltimaMensagemClienteEm,
    bool DentroDaJanela24h, bool MarcadaRetomadaHumana, DateTimeOffset? RetomadaEncerradaEm, string? RetomadaMotivo, Guid? AtribuidaA)
{
    /// <summary>Na fila: precisa de humano e ninguém é responsável.</summary>
    public bool NaFila => AtribuidaA is null && (Estado != nameof(EstadoConversa.Automatico) || MarcadaRetomadaHumana);
}

public sealed record MensagemConversa(string Direcao, string Texto, DateTimeOffset OcorridaEm);

public sealed record DecisaoConversa(string Tipo, string Resultado, string? Motivo, DateTimeOffset CriadaEm);

public sealed record EnvioConversa(Guid Id, string Origem, string Status, string? Motivo, DateTimeOffset CriadaEm);

public sealed record DetalheConversa(
    ResumoConversa Conversa, IReadOnlyList<MensagemConversa> Mensagens, IReadOnlyList<DecisaoConversa> Decisoes, IReadOnlyList<EnvioConversa> Envios);

public sealed record PoliticaPublicada(int Versao, JsonElement Conteudo);

/// <summary>
/// Operações da supervisão humana (plano técnico, seção 4.9). Roda com o papel seceletesc_supervisao.
/// Toda operação acontece numa transação com o tenant do usuário autenticado (SET LOCAL): uma conversa de outra
/// empresa simplesmente não existe para o usuário (RLS), e a resposta é "não encontrada".
/// </summary>
public sealed class ServicoSupervisao(NpgsqlDataSource dataSourceSupervisao, TimeProvider relogio)
{
    /// <summary>Limite do WhatsApp para mensagem de texto.</summary>
    public const int TamanhoMaximoTexto = 4096;

    public const int TamanhoMaximoAssunto = 100;

    /// <summary>Validade de uma resposta humana na fila: se o worker não enviar até lá, ela é cancelada.</summary>
    public static readonly TimeSpan ValidadeRespostaHumana = TimeSpan.FromMinutes(10);

    private readonly ExecutorTenant _executor = new(dataSourceSupervisao);

    public static string HashChave(string chave) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(chave)));

    /// <summary>Nova chave de API: 256 bits aleatórios. Só o hash vai para o banco.</summary>
    public static string GerarChave() => "sec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<UsuarioSupervisao?> AutenticarAsync(string? chave, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chave) || chave.Length > 200)
        {
            return null;
        }

        await using var c = await dataSourceSupervisao.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("select usuario_id, tenant_id, papel from autenticar_supervisor(@h)", c);
        cmd.Parameters.AddWithValue("h", HashChave(chave));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ? new UsuarioSupervisao(r.GetGuid(0), r.GetGuid(1), Enum.Parse<PapelSupervisao>(r.GetString(2))) { Credencial = chave }
            : null;
    }

    public Task<IReadOnlyList<ResumoConversa>> ListarConversasAsync(UsuarioSupervisao usuario, string? estado, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync<IReadOnlyList<ResumoConversa>>(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            // Atendente: só as conversas atribuídas a ele e a fila (decisão 27). Supervisor e administrador: a empresa toda.
            await using var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + """
                 where (@e::text is null or cv.estado = @e::text)
                   and (not @restrito or cv.atribuida_a = @u or (cv.atribuida_a is null and (cv.estado <> 'Automatico' or cv.marcada_retomada_humana)))
                 order by cv.ultima_mensagem_cliente_em desc nulls last
                 limit 200
                """);
            cmd.Parameters.AddWithValue("e", (object?)estado ?? DBNull.Value);
            cmd.Parameters.AddWithValue("restrito", !usuario.PodeGerirAtendimento);
            cmd.Parameters.AddWithValue("u", usuario.Id);
            var resumos = await LerResumosAsync(cmd, t).ConfigureAwait(false);

            // Da fila, o atendente vê o telefone só parcialmente: o dado do cliente é de quem pegar o atendimento (G4).
            return usuario.PodeGerirAtendimento
                ? resumos
                : resumos.Select(r => r.AtribuidaA == usuario.Id ? r : r with { WaIdCliente = OcultarTelefone(r.WaIdCliente) }).ToList();
        }, ct);
    }

    internal static string OcultarTelefone(string waId) => waId.Length <= 4 ? new string('*', waId.Length) : new string('*', waId.Length - 4) + waId[^4..];

    public Task<DetalheConversa?> ObterConversaAsync(UsuarioSupervisao usuario, Guid conversaId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            ResumoConversa? resumo;
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + " where cv.id = @id"))
            {
                cmd.Parameters.AddWithValue("id", conversaId);
                resumo = (await LerResumosAsync(cmd, t).ConfigureAwait(false)).SingleOrDefault();
            }

            // Atendente lê só o que está atribuído a ele. Da fila, vê o resumo na listagem e pega o atendimento para ler.
            if (resumo is null || !PodeAtuar(usuario, resumo.AtribuidaA))
            {
                return null;
            }

            var mensagens = new List<MensagemConversa>();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                select direcao, texto_mascarado, ocorrida_em from (
                  select direcao, texto_mascarado, ocorrida_em, criada_em from mensagens where conversa_id = @id
                   order by ocorrida_em desc, criada_em desc limit 100) m
                 order by ocorrida_em, criada_em
                """))
            {
                cmd.Parameters.AddWithValue("id", conversaId);
                await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
                while (await r.ReadAsync(t).ConfigureAwait(false))
                {
                    mensagens.Add(new MensagemConversa(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
                }
            }

            var decisoes = new List<DecisaoConversa>();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                select tipo, resultado, motivo, criada_em from decisoes_envio where conversa_id = @id order by criada_em desc limit 20
                """))
            {
                cmd.Parameters.AddWithValue("id", conversaId);
                await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
                while (await r.ReadAsync(t).ConfigureAwait(false))
                {
                    decisoes.Add(new DecisaoConversa(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)));
                }
            }

            var envios = new List<EnvioConversa>();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                select id, case when autor_id is null then 'automatica' else 'humana' end, status, motivo, criada_em
                  from outbox where conversa_id = @id order by criada_em desc limit 20
                """))
            {
                cmd.Parameters.AddWithValue("id", conversaId);
                await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
                while (await r.ReadAsync(t).ConfigureAwait(false))
                {
                    envios.Add(new EnvioConversa(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)));
                }
            }

            return new DetalheConversa(resumo, mensagens, decisoes, envios);
        }, ct);
    }

    /// <summary>Humano assume a conversa (invariante 5). Vale a partir de qualquer estado; a IA fica muda.</summary>
    public Task<ResultadoOperacao> AssumirAsync(UsuarioSupervisao usuario, Guid conversaId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            var atribuida = await AtribuidaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (!usuario.PodeGerirAtendimento)
            {
                // Atendente: só o que é dele ou está na fila (precisa de humano e não tem responsável).
                if (atribuida is { } outro && outro != usuario.Id)
                {
                    return ResultadoOperacao.NaoEncontrada;
                }

                if (atribuida is null && conversa.Estado == EstadoConversa.Automatico && !conversa.MarcadaRetomadaHumana)
                {
                    return ResultadoOperacao.Proibida("o atendente assume atendimentos da fila; para outras conversas, peça a um supervisor");
                }
            }

            var versao = conversa.VersaoEstado;
            string? detalhe = "conversa já estava sob controle humano";
            if (conversa.Estado != EstadoConversa.Humano)
            {
                var transicao = conversa.Assumir("assumida pela supervisão");
                await AtualizarEstadoAsync(c, tx, conversa, t).ConfigureAwait(false);
                await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "conversa_assumida",
                    new { usuario = usuario.Id, de = transicao.De.ToString(), versao = transicao.NovaVersao }, t).ConfigureAwait(false);
                versao = transicao.NovaVersao;
                detalhe = null;
            }

            // Quem assume um atendimento sem responsável passa a ser o responsável (pegar da fila).
            if (atribuida is null)
            {
                await GravarAtribuicaoAsync(c, tx, usuario, conversaId, usuario.Id, t).ConfigureAwait(false);
            }

            return new ResultadoOperacao(SituacaoOperacao.Ok, detalhe, versao);
        }, ct);
    }

    /// <summary>
    /// Liberação explícita (invariante 5). Exige a versão que o humano viu: se a conversa mudou nesse meio-tempo
    /// (outro humano, nova transferência), a liberação é recusada em vez de devolver à IA algo que ele não viu.
    /// </summary>
    public async Task<ResultadoOperacao> LiberarAsync(UsuarioSupervisao usuario, Guid conversaId, long versaoEsperada, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("liberar a conversa para a IA exige supervisor ou administrador");
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            if (conversa.Estado != EstadoConversa.Humano)
            {
                return ResultadoOperacao.Conflito("a conversa não está sob controle humano: assuma antes de liberar", conversa.VersaoEstado);
            }

            if (conversa.VersaoEstado != versaoEsperada)
            {
                return ResultadoOperacao.Conflito("a conversa mudou desde a última consulta", conversa.VersaoEstado);
            }

            // Resposta humana ainda na fila: liberar agora deixaria a IA responder sem ela no histórico e fora de ordem.
            await using (var pendente = RepositorioNucleo.Cmd(c, tx,
                "select 1 from outbox where conversa_id = @id and autor_id is not null and status in ('pendente', 'enviando') limit 1"))
            {
                pendente.Parameters.AddWithValue("id", conversaId);
                if (await pendente.ExecuteScalarAsync(t).ConfigureAwait(false) is not null)
                {
                    return ResultadoOperacao.Conflito("há resposta humana aguardando envio: libere depois que ela sair", conversa.VersaoEstado);
                }
            }

            var transicao = conversa.Liberar(relogio.GetUtcNow(), "liberada pela supervisão");
            await AtualizarEstadoAsync(c, tx, conversa, t, liberadaPor: usuario.Id).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "conversa_liberada",
                new { usuario = usuario.Id, versao = transicao.NovaVersao }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: transicao.NovaVersao);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Encerra a pendência de retomada pelo celular. Só supervisor ou administrador, com motivo; o banco carimba a
    /// data e confere o papel. Liberar a conversa não encerra a pendência.
    /// </summary>
    public async Task<ResultadoOperacao> EncerrarRetomadaAsync(UsuarioSupervisao usuario, Guid conversaId, string? motivo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("encerrar a pendência de retomada exige supervisor ou administrador");
        }

        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500)
        {
            return ResultadoOperacao.Invalida("motivo obrigatório, com até 500 caracteres");
        }

        // O motivo fica no histórico em claro: nenhum dado protegido, nem comum.
        if (ContemDadoProtegido(motivo))
        {
            return ResultadoOperacao.Invalida("o motivo contém dado protegido");
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            if (!conversa.MarcadaRetomadaHumana)
            {
                return ResultadoOperacao.Conflito("a conversa não tem pendência de retomada ativa", conversa.VersaoEstado);
            }

            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                update conversas set marcada_retomada_humana = false, retomada_encerrada_por = @u, retomada_motivo = @m where id = @id
                """))
            {
                cmd.Parameters.AddWithValue("u", usuario.Id);
                cmd.Parameters.AddWithValue("m", motivo.Trim());
                cmd.Parameters.AddWithValue("id", conversaId);
                await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "retomada_encerrada",
                new { usuario = usuario.Id, motivo = motivo.Trim() }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: conversa.VersaoEstado);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resposta humana pelo mesmo número (invariante 4). Só com a conversa sob controle humano. Entra na fila
    /// com autor e é enviada pelo worker. Dado protegido com tratamento proporcional (ver ValidarTextoHumano).
    /// </summary>
    public async Task<ResultadoOperacao> ResponderAsync(UsuarioSupervisao usuario, Guid conversaId, string? texto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (ValidarTextoHumano(texto) is { } invalido)
        {
            return invalido;
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            if (!PodeAtuar(usuario, await AtribuidaAsync(c, tx, conversaId, t).ConfigureAwait(false)))
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            if (conversa.Estado != EstadoConversa.Humano)
            {
                return ResultadoOperacao.Conflito("assuma a conversa antes de responder", conversa.VersaoEstado);
            }

            // Sem templates pela API (invariante 24): fora da janela, a retomada é pelo celular.
            if (!conversa.DentroDaJanela24h(relogio.GetUtcNow()))
            {
                return ResultadoOperacao.Conflito("fora da janela de 24h do WhatsApp: retome a conversa pelo celular", conversa.VersaoEstado);
            }

            var outboxId = Guid.NewGuid();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                insert into outbox (id, tenant_id, conversa_id, decisao_id, autor_id, texto, status, expira_em)
                values (@id, @t, @c, null, @a, @tx, 'pendente', @exp)
                """))
            {
                cmd.Parameters.AddWithValue("id", outboxId);
                cmd.Parameters.AddWithValue("t", usuario.TenantId);
                cmd.Parameters.AddWithValue("c", conversaId);
                cmd.Parameters.AddWithValue("a", usuario.Id);
                cmd.Parameters.AddWithValue("tx", texto!);
                cmd.Parameters.AddWithValue("exp", relogio.GetUtcNow() + ValidadeRespostaHumana);
                await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "resposta_humana_enfileirada",
                new { usuario = usuario.Id, outbox = outboxId }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: conversa.VersaoEstado, Id: outboxId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Orientação interna (invariante 4; decisão 6a). Não vira fonte: entra como sugestão REV, sem classificação,
    /// com escopo neste atendimento, e só é usada depois de avaliada e classificada por um humano.
    /// Não muda o estado da conversa: a IA continua muda até a liberação (decisão 5).
    /// </summary>
    public async Task<ResultadoOperacao> OrientarAsync(UsuarioSupervisao usuario, Guid conversaId, string? texto, string? assunto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (ValidarTextoHumano(texto) is { } invalido)
        {
            return invalido;
        }

        if (string.IsNullOrWhiteSpace(assunto) || assunto.Length > TamanhoMaximoAssunto)
        {
            return ResultadoOperacao.Invalida($"assunto obrigatório, com até {TamanhoMaximoAssunto} caracteres");
        }

        if (ContemDadoProtegido(assunto))
        {
            return ResultadoOperacao.Invalida("o assunto contém dado protegido");
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            if (!await PodeAtuarNaConversaAsync(c, tx, usuario, conversaId, t).ConfigureAwait(false))
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            // A orientação fica em claro na base de sugestões e é sobre esta conversa: dado pessoal não entra
            // (invariante 23; decisão 29). Só e-mail institucional listado na política.
            var politica = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false)
                ?? new PoliticaTenant { TenantId = usuario.TenantId, Versao = 0, AssuntosAutonomos = new HashSet<string>() };
            if (ServicoConhecimento.DadoPessoalEmConhecimento(texto!, politica) is { } pessoal)
            {
                return ResultadoOperacao.Invalida("a orientação não pode conter dado pessoal; descreva sem o dado (" + pessoal + ")");
            }

            var itemId = Guid.NewGuid();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (@i, @t, @a, 'Rev', 1);
                insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, proposta_por)
                values (@i, 1, @t, @c, '{}', 'Atendimento', @conv, null, @o, @u);
                """))
            {
                cmd.Parameters.AddWithValue("i", itemId);
                cmd.Parameters.AddWithValue("t", usuario.TenantId);
                cmd.Parameters.AddWithValue("a", assunto.Trim());
                cmd.Parameters.AddWithValue("c", texto!);
                cmd.Parameters.AddWithValue("conv", conversaId);
                cmd.Parameters.AddWithValue("o", $"orientacao_interna:usuario:{usuario.Id}");
                cmd.Parameters.AddWithValue("u", usuario.Id);
                await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "orientacao_registrada",
                new { usuario = usuario.Id, item = itemId }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gestão da fila (decisão 27): supervisor e administrador atribuem, reatribuem ou retiram o responsável
    /// (responsável nulo devolve à fila). O banco confere o papel de quem atribui e que o responsável é desta empresa.
    /// </summary>
    public async Task<ResultadoOperacao> AtribuirAsync(UsuarioSupervisao usuario, Guid conversaId, Guid? responsavel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("gerenciar a fila exige supervisor ou administrador");
        }

        try
        {
            return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
            {
                var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
                if (conversa is null)
                {
                    return ResultadoOperacao.NaoEncontrada;
                }

                // Conversa com a IA (mesmo com pendência de retomada) não tem responsável humano: assuma antes.
                if (conversa.Estado == EstadoConversa.Automatico)
                {
                    return ResultadoOperacao.Conflito("a conversa está com a IA: assuma antes de atribuir", conversa.VersaoEstado);
                }

                if (responsavel is { } r)
                {
                    await using var ativo = RepositorioNucleo.Cmd(c, tx, "select usuario_ativo_da_empresa(@t, @r)");
                    ativo.Parameters.AddWithValue("t", usuario.TenantId);
                    ativo.Parameters.AddWithValue("r", r);
                    if (await ativo.ExecuteScalarAsync(t).ConfigureAwait(false) is not true)
                    {
                        return ResultadoOperacao.Invalida("responsável não encontrado nesta empresa ou desativado");
                    }
                }

                await GravarAtribuicaoAsync(c, tx, usuario, conversaId, responsavel, t).ConfigureAwait(false);
                return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: conversa.VersaoEstado);
            }, ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            return ResultadoOperacao.Invalida("responsável não encontrado nesta empresa");
        }
    }

    public Task<PoliticaPublicada?> ObterPoliticaAsync(UsuarioSupervisao usuario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            await using var cmd = RepositorioNucleo.Cmd(c, tx, "select versao, conteudo::text from politicas_tenant order by versao desc limit 1");
            await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
            if (!await r.ReadAsync(t).ConfigureAwait(false))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(r.GetString(1));
            return new PoliticaPublicada(r.GetInt32(0), doc.RootElement.Clone());
        }, ct);
    }

    /// <summary>
    /// Publica uma nova versão da política (versões imutáveis, auditadas). Exige a versão vigente que o humano viu
    /// (0 se ainda não há política). Grava a forma canônica: campos desconhecidos são descartados.
    /// </summary>
    public async Task<ResultadoOperacao> PublicarPoliticaAsync(UsuarioSupervisao usuario, int versaoEsperada, string? conteudoJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        // Classificações liberadas, termos de compromisso e envio de conhecimento de cliente são restrições críticas.
        if (!usuario.EhAdministrador)
        {
            return ResultadoOperacao.Proibida("publicar a política exige administrador");
        }

        PoliticaTenant politica;
        try
        {
            politica = PoliticaJson.Ler(usuario.TenantId, versaoEsperada + 1, conteudoJson ?? string.Empty);

            // Piso (invariante 25): pedir INT ou RES em resposta automática é recusado, não ignorado em silêncio.
            if (PoliticaJson.ClassificacoesPedidas(conteudoJson!).FirstOrDefault(c =>
                    PoliticaTenant.ClassificacoesNuncaAutomaticas.Select(x => x.ToString()).Contains(c, StringComparer.Ordinal)) is { } proibida)
            {
                return ResultadoOperacao.Invalida($"a classificação {proibida} nunca vai para resposta automática (políticas fundamentais)");
            }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            return ResultadoOperacao.Invalida("política em formato inválido: " + e.Message);
        }

        if (ValidarPolitica(politica) is { } erro)
        {
            return ResultadoOperacao.Invalida(erro);
        }

        var canonica = PoliticaJson.Serializar(politica);
        try
        {
            return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
            {
                await using (var atual = RepositorioNucleo.Cmd(c, tx, "select coalesce(max(versao), 0) from politicas_tenant"))
                {
                    var vigente = (int)(await atual.ExecuteScalarAsync(t).ConfigureAwait(false))!;
                    if (vigente != versaoEsperada)
                    {
                        return ResultadoOperacao.Conflito("a política mudou desde a última consulta", vigente);
                    }
                }

                await using (var cmd = RepositorioNucleo.Cmd(c, tx, "insert into politicas_tenant (tenant_id, versao, conteudo, publicada_por) values (@t, @v, @c::jsonb, @u)"))
                {
                    cmd.Parameters.AddWithValue("u", usuario.Id);
                    cmd.Parameters.AddWithValue("t", usuario.TenantId);
                    cmd.Parameters.AddWithValue("v", versaoEsperada + 1);
                    cmd.Parameters.AddWithValue("c", canonica);
                    await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
                }

                await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, null, "politica_publicada",
                    new { usuario = usuario.Id, versao = versaoEsperada + 1 }, t).ConfigureAwait(false);
                return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: versaoEsperada + 1);
            }, ct).ConfigureAwait(false);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Duas publicações simultâneas sobre a mesma versão: só uma vence.
            return ResultadoOperacao.Conflito("a política mudou desde a última consulta");
        }
    }

    private static string? ValidarPolitica(PoliticaTenant p)
    {
        var assuntos = p.AssuntosAutonomos.Concat(p.AssuntosExigemHumano).ToList();
        if (assuntos.Any(a => string.IsNullOrWhiteSpace(a) || a.Length > TamanhoMaximoAssunto))
        {
            return $"assuntos não podem ser vazios e têm até {TamanhoMaximoAssunto} caracteres";
        }

        if (p.AssuntosAutonomos.Intersect(p.AssuntosExigemHumano, StringComparer.Ordinal).FirstOrDefault() is { } ambos)
        {
            return $"o assunto '{ambos}' não pode ser autônomo e exigir humano ao mesmo tempo";
        }

        if (p.TermosCompromisso.Any(string.IsNullOrWhiteSpace) || p.DominiosPermitidos.Any(string.IsNullOrWhiteSpace))
        {
            return "termos de compromisso e domínios permitidos não podem ser vazios";
        }

        // A lista aceita subdomínios: "com" ou "br" liberariam qualquer site. Exige um domínio de verdade.
        if (p.DominiosPermitidos.FirstOrDefault(d => !d.Trim('.').Contains('.', StringComparison.Ordinal)) is { } generico)
        {
            return $"domínio permitido genérico demais: '{generico}'";
        }

        // Nenhum texto da política guarda dado protegido em claro (invariante 23).
        if (assuntos.Concat(p.TermosCompromisso).Concat(p.DominiosPermitidos).Any(ContemDadoProtegido))
        {
            return "a política contém dado protegido";
        }

        if (p.TextoAvisoTransferencia.Length > TamanhoMaximoTexto)
        {
            return "aviso de transferência muito longo";
        }

        // O aviso é uma mensagem automática: mesmas regras de conteúdo, sem fonte (sem prazo, valor, compromisso,
        // dado protegido, telefone, URL fora da lista ou autodeclaração humana). O pipeline confere de novo ao enfileirar.
        if (p.TextoAvisoTransferencia.Length > 0
            && ValidadorEnvio.ValidarTextoFixo(p.TextoAvisoTransferencia, p) is { Aprovado: false } bloqueio)
        {
            return $"aviso de transferência recusado pela validação do envio: {bloqueio.Motivo}" + (bloqueio.Detalhe is { } d ? $" ({d})" : string.Empty);
        }

        // E-mails institucionais: endereços completos, um por item (decisão 29).
        foreach (var email in p.EmailsInstitucionais)
        {
            var ocorrencias = DetectorDadosProtegidos.Mascarar(email).Ocorrencias;
            if (ocorrencias.Count != 1 || ocorrencias[0].Categoria != CategoriaDadoProtegido.Email || ocorrencias[0].Valor.Length != email.Length)
            {
                return "cada e-mail institucional precisa ser um endereço de e-mail completo";
            }
        }

        if (p.ValidadeMaximaDias.Values.Any(d => d is < 1 or > 3650))
        {
            return "a validade máxima por classificação fica entre 1 e 3650 dias";
        }

        if (p.ValidadeMensagemAutomatica < TimeSpan.FromSeconds(30) || p.ValidadeMensagemAutomatica > TimeSpan.FromHours(1))
        {
            return "validade da mensagem automática deve ficar entre 30 segundos e 1 hora";
        }

        return null;
    }

    /// <summary>
    /// Texto escrito por um humano. Tratamento proporcional ao risco: dado comum (e-mail, CEP) é aceito, porque o
    /// humano autorizado decide se é necessário e pertinente. Numa resposta enviada, o worker o mascara e cifra no
    /// histórico. Numa sugestão avulsa de conhecimento, fica em claro de propósito: é dado da empresa que precisa
    /// estar na fonte para sair na resposta (orientação e sugestão ligadas a conversa recusam qualquer dado).
    /// Dado de identificação (CPF etc.) exige finalidade definida, que ainda não tem mecanismo, e dado financeiro
    /// (cartão) nunca é reproduzido: os dois são recusados.
    /// A mensagem de erro cita só a categoria, nunca o valor.
    /// </summary>
    internal static ResultadoOperacao? ValidarTextoHumano(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > TamanhoMaximoTexto)
        {
            return ResultadoOperacao.Invalida($"texto obrigatório, com até {TamanhoMaximoTexto} caracteres");
        }

        var graves = DetectorDadosProtegidos.Mascarar(texto).Ocorrencias.Where(o => o.Nivel != NivelDadoProtegido.Comum).ToList();
        if (graves.Count > 0)
        {
            var categorias = string.Join(", ", graves.Select(o => o.Categoria).Distinct());
            return ResultadoOperacao.Invalida($"o texto contém dado protegido ({categorias}) sem finalidade autorizada para envio. Trate pelo celular.");
        }

        return null;
    }

    private static bool ContemDadoProtegido(string texto) => DetectorDadosProtegidos.Mascarar(texto).Ocorrencias.Count > 0;

    /// <summary>Atendente atua só no que está atribuído a ele; supervisor e administrador, em toda a empresa.</summary>
    internal static bool PodeAtuar(UsuarioSupervisao usuario, Guid? atribuidaA) => usuario.PodeGerirAtendimento || atribuidaA == usuario.Id;

    /// <summary>A conversa existe nesta empresa (RLS) e o usuário pode atuar nela.</summary>
    internal static async Task<bool> PodeAtuarNaConversaAsync(NpgsqlConnection c, NpgsqlTransaction tx, UsuarioSupervisao usuario, Guid conversaId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "select atribuida_a from conversas where id = @id");
        cmd.Parameters.AddWithValue("id", conversaId);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is not null && PodeAtuar(usuario, r is Guid g ? g : null);
    }

    private static async Task<Guid?> AtribuidaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "select atribuida_a from conversas where id = @id");
        cmd.Parameters.AddWithValue("id", conversaId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g ? g : null;
    }

    private static async Task GravarAtribuicaoAsync(NpgsqlConnection c, NpgsqlTransaction tx, UsuarioSupervisao usuario, Guid conversaId, Guid? responsavel, CancellationToken ct)
    {
        await using (var cmd = RepositorioNucleo.Cmd(c, tx, "update conversas set atribuida_a = @r where id = @id"))
        {
            cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)responsavel ?? DBNull.Value });
            cmd.Parameters.AddWithValue("id", conversaId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "conversa_atribuida",
            new { usuario = usuario.Id, responsavel }, ct).ConfigureAwait(false);
    }

    /// <summary>Só os campos que as transições da supervisão alteram (o papel não tem permissão para os demais).</summary>
    private static async Task AtualizarEstadoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Conversa conversa, CancellationToken ct, Guid? liberadaPor = null)
    {
        // Na liberação, o banco exige o responsável (supervisor ou administrador) e confere o papel.
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            update conversas set estado = @e, versao_estado = @v, liberada_em = @l,
                   liberada_por = case when @lp::uuid is null then liberada_por else @lp::uuid end
             where id = @id
            """);
        cmd.Parameters.Add(new NpgsqlParameter("lp", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)liberadaPor ?? DBNull.Value });
        cmd.Parameters.AddWithValue("e", conversa.Estado.ToString());
        cmd.Parameters.AddWithValue("v", conversa.VersaoEstado);
        cmd.Parameters.AddWithValue("l", (object?)conversa.LiberadaEm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", conversa.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string SelectResumo = """
        select cv.id, cl.wa_id, cv.estado, cv.versao_estado, cv.ultima_mensagem_cliente_em, cv.marcada_retomada_humana,
               cv.cliente_id, cv.tenant_id, cv.liberada_em, cv.retomada_encerrada_em, cv.retomada_motivo, cv.atribuida_a
          from conversas cv join clientes cl on cl.id = cv.cliente_id
        """;

    private async Task<List<ResumoConversa>> LerResumosAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var agora = relogio.GetUtcNow();
        var lista = new List<ResumoConversa>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var ultima = r.IsDBNull(4) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(4);
            var estado = r.GetString(2);
            var conversa = Conversa.Restaurar(r.GetGuid(0), r.GetGuid(7), r.GetGuid(6), Enum.Parse<EstadoConversa>(estado), r.GetInt64(3),
                ultima, r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetBoolean(5));
            lista.Add(new ResumoConversa(conversa.Id, r.GetString(1), estado, conversa.VersaoEstado, ultima, conversa.DentroDaJanela24h(agora), conversa.MarcadaRetomadaHumana,
                r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetGuid(11)));
        }

        return lista;
    }
}
