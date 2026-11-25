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

/// <summary>Usuário humano autenticado. O tenant vem daqui, nunca da requisição.</summary>
public sealed record UsuarioSupervisao(Guid Id, Guid TenantId);

public enum SituacaoOperacao
{
    Ok,
    NaoEncontrada,
    Conflito,
    Invalida,
}

public sealed record ResultadoOperacao(SituacaoOperacao Situacao, string? Detalhe = null, long? Versao = null, Guid? Id = null)
{
    public static ResultadoOperacao NaoEncontrada { get; } = new(SituacaoOperacao.NaoEncontrada, "conversa não encontrada");

    public static ResultadoOperacao Conflito(string detalhe, long? versao = null) => new(SituacaoOperacao.Conflito, detalhe, versao);

    public static ResultadoOperacao Invalida(string detalhe) => new(SituacaoOperacao.Invalida, detalhe);
}

public sealed record ResumoConversa(
    Guid Id, string WaIdCliente, string Estado, long VersaoEstado, DateTimeOffset? UltimaMensagemClienteEm,
    bool DentroDaJanela24h, bool MarcadaRetomadaHumana);

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
        await using var cmd = new NpgsqlCommand("select usuario_id, tenant_id from autenticar_supervisor(@h)", c);
        cmd.Parameters.AddWithValue("h", HashChave(chave));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? new UsuarioSupervisao(r.GetGuid(0), r.GetGuid(1)) : null;
    }

    public Task<IReadOnlyList<ResumoConversa>> ListarConversasAsync(UsuarioSupervisao usuario, string? estado, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAsync<IReadOnlyList<ResumoConversa>>(usuario.TenantId, async (c, tx, t) =>
        {
            await using var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + """
                 where (@e::text is null or cv.estado = @e::text)
                 order by cv.ultima_mensagem_cliente_em desc nulls last
                 limit 200
                """);
            cmd.Parameters.AddWithValue("e", (object?)estado ?? DBNull.Value);
            return await LerResumosAsync(cmd, t).ConfigureAwait(false);
        }, ct);
    }

    public Task<DetalheConversa?> ObterConversaAsync(UsuarioSupervisao usuario, Guid conversaId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
        {
            ResumoConversa? resumo;
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + " where cv.id = @id"))
            {
                cmd.Parameters.AddWithValue("id", conversaId);
                resumo = (await LerResumosAsync(cmd, t).ConfigureAwait(false)).SingleOrDefault();
            }

            if (resumo is null)
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
        return _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            if (conversa.Estado == EstadoConversa.Humano)
            {
                return new ResultadoOperacao(SituacaoOperacao.Ok, "conversa já estava sob controle humano", conversa.VersaoEstado);
            }

            var transicao = conversa.Assumir("assumida pela supervisão");
            await AtualizarEstadoAsync(c, tx, conversa, t).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "conversa_assumida",
                new { usuario = usuario.Id, de = transicao.De.ToString(), versao = transicao.NovaVersao }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: transicao.NovaVersao);
        }, ct);
    }

    /// <summary>
    /// Liberação explícita (invariante 5). Exige a versão que o humano viu: se a conversa mudou nesse meio-tempo
    /// (outro humano, nova transferência), a liberação é recusada em vez de devolver à IA algo que ele não viu.
    /// </summary>
    public Task<ResultadoOperacao> LiberarAsync(UsuarioSupervisao usuario, Guid conversaId, long versaoEsperada, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
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
            await AtualizarEstadoAsync(c, tx, conversa, t).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "conversa_liberada",
                new { usuario = usuario.Id, versao = transicao.NovaVersao }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: transicao.NovaVersao);
        }, ct);
    }

    /// <summary>
    /// Resposta humana pelo mesmo número (invariante 4). Só com a conversa sob controle humano. Entra na fila
    /// com autor e é enviada pelo worker. Texto com dado protegido é recusado: o dado não fica guardado em claro.
    /// </summary>
    public async Task<ResultadoOperacao> ResponderAsync(UsuarioSupervisao usuario, Guid conversaId, string? texto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (ValidarTexto(texto) is { } invalido)
        {
            return invalido;
        }

        return await _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
        {
            var conversa = await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, conversaId, t).ConfigureAwait(false);
            if (conversa is null)
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
        if (ValidarTexto(texto) is { } invalido)
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

        return await _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
        {
            await using (var existe = RepositorioNucleo.Cmd(c, tx, "select 1 from conversas where id = @id"))
            {
                existe.Parameters.AddWithValue("id", conversaId);
                if (await existe.ExecuteScalarAsync(t).ConfigureAwait(false) is null)
                {
                    return ResultadoOperacao.NaoEncontrada;
                }
            }

            var itemId = Guid.NewGuid();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (@i, @t, @a, 'Rev', 1);
                insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem)
                values (@i, 1, @t, @c, '{}', 'Atendimento', @conv, null, @o);
                """))
            {
                cmd.Parameters.AddWithValue("i", itemId);
                cmd.Parameters.AddWithValue("t", usuario.TenantId);
                cmd.Parameters.AddWithValue("a", assunto.Trim());
                cmd.Parameters.AddWithValue("c", texto!);
                cmd.Parameters.AddWithValue("conv", conversaId);
                cmd.Parameters.AddWithValue("o", $"orientacao_interna:usuario:{usuario.Id}");
                await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaId, "orientacao_registrada",
                new { usuario = usuario.Id, item = itemId }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    public Task<PoliticaPublicada?> ObterPoliticaAsync(UsuarioSupervisao usuario, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
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
        PoliticaTenant politica;
        try
        {
            politica = PoliticaJson.Ler(usuario.TenantId, versaoEsperada + 1, conteudoJson ?? string.Empty);
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
            return await _executor.ExecutarAsync(usuario.TenantId, async (c, tx, t) =>
            {
                await using (var atual = RepositorioNucleo.Cmd(c, tx, "select coalesce(max(versao), 0) from politicas_tenant"))
                {
                    var vigente = (int)(await atual.ExecuteScalarAsync(t).ConfigureAwait(false))!;
                    if (vigente != versaoEsperada)
                    {
                        return ResultadoOperacao.Conflito("a política mudou desde a última consulta", vigente);
                    }
                }

                await using (var cmd = RepositorioNucleo.Cmd(c, tx, "insert into politicas_tenant (tenant_id, versao, conteudo) values (@t, @v, @c::jsonb)"))
                {
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

        if (p.ValidadeMensagemAutomatica < TimeSpan.FromSeconds(30) || p.ValidadeMensagemAutomatica > TimeSpan.FromHours(1))
        {
            return "validade da mensagem automática deve ficar entre 30 segundos e 1 hora";
        }

        return null;
    }

    private static ResultadoOperacao? ValidarTexto(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto) || texto.Length > TamanhoMaximoTexto)
        {
            return ResultadoOperacao.Invalida($"texto obrigatório, com até {TamanhoMaximoTexto} caracteres");
        }

        var mascarado = DetectorDadosProtegidos.Mascarar(texto!);
        if (mascarado.Ocorrencias.Count > 0)
        {
            var categorias = string.Join(", ", mascarado.Ocorrencias.Select(o => o.Categoria).Distinct());
            return ResultadoOperacao.Invalida($"o texto contém dado protegido ({categorias}); ele não é guardado em claro. Trate pelo celular.");
        }

        return null;
    }

    private static bool ContemDadoProtegido(string texto) => DetectorDadosProtegidos.Mascarar(texto).Ocorrencias.Count > 0;

    /// <summary>Só os campos que as transições da supervisão alteram (o papel não tem permissão para os demais).</summary>
    private static async Task AtualizarEstadoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Conversa conversa, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "update conversas set estado = @e, versao_estado = @v, liberada_em = @l where id = @id");
        cmd.Parameters.AddWithValue("e", conversa.Estado.ToString());
        cmd.Parameters.AddWithValue("v", conversa.VersaoEstado);
        cmd.Parameters.AddWithValue("l", (object?)conversa.LiberadaEm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", conversa.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private const string SelectResumo = """
        select cv.id, cl.wa_id, cv.estado, cv.versao_estado, cv.ultima_mensagem_cliente_em, cv.marcada_retomada_humana,
               cv.cliente_id, cv.tenant_id, cv.liberada_em
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
            lista.Add(new ResumoConversa(conversa.Id, r.GetString(1), estado, conversa.VersaoEstado, ultima, conversa.DentroDaJanela24h(agora), conversa.MarcadaRetomadaHumana));
        }

        return lista;
    }
}
