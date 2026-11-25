using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.DadosProtegidos;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Nucleo;

namespace Seceletesc.Infraestrutura.Supervisao;

/// <summary>Classificação, escopo e validade de uma versão. Na sugestão, são o que se propõe; na aprovação, o que vale.</summary>
public sealed record PermissoesConhecimento(IReadOnlyList<string>? Classificacoes, string? EscopoTipo, Guid? EscopoRef, DateTimeOffset? ValidoAte);

public sealed record ResumoConhecimento(
    Guid Id, string Assunto, string Status, int VersaoVigente, int UltimaVersao, bool TemEdicaoPendente, DateTimeOffset CriadoEm,
    bool PrecisaReavaliacao = false);

public sealed record VersaoConhecimento(
    int Versao, string Conteudo, IReadOnlyList<string> Classificacoes, string EscopoTipo, Guid? EscopoRef, DateTimeOffset? ValidoAte,
    string Origem, Guid? AprovadaPor, DateTimeOffset? AprovadaEm, DateTimeOffset CriadaEm);

public sealed record EventoConhecimento(string Tipo, JsonElement Dados, DateTimeOffset OcorridoEm);

public sealed record DetalheConhecimento(ResumoConhecimento Item, IReadOnlyList<VersaoConhecimento> Versoes, IReadOnlyList<EventoConhecimento> Auditoria);

/// <summary>
/// Governança do conhecimento reutilizável (F3; governanca-conhecimento.md). Fluxo:
/// sugestão (Rev, versão proposta) → revisão humana (editar gera nova versão proposta) → aprovar (gera nova versão
/// aprovada, com autor, data e permissões explícitas) ou rejeitar → revogar, se preciso. Nada é publicado sem um
/// humano autorizado. Só a versão aprovada e vigente chega ao agente. Tudo é registrado no histórico.
/// O banco confere de novo o papel de quem aprova e revoga (migração 0004).
/// </summary>
public sealed class ServicoConhecimento(NpgsqlDataSource dataSourceSupervisao, TimeProvider relogio)
{
    /// <summary>Classificações de maior risco: só o administrador aprova.</summary>
    public static readonly IReadOnlySet<Classificacao> AltoRisco = new HashSet<Classificacao>
    {
        Classificacao.Negociacao, Classificacao.Excecao, Classificacao.Restrito, Classificacao.Cliente,
    };

    /// <summary>Condições comerciais excepcionais: exigem cliente ou atendimento definidos e validade.</summary>
    public static readonly IReadOnlySet<Classificacao> Comerciais = new HashSet<Classificacao> { Classificacao.Negociacao, Classificacao.Excecao };

    private readonly ExecutorTenant _executor = new(dataSourceSupervisao);

    /// <summary>Qualquer papel sugere. A sugestão nasce pendente (Rev) e nunca é usada pelo agente.</summary>
    public async Task<ResultadoOperacao> SugerirAsync(
        UsuarioSupervisao usuario, string? assunto, string? conteudo, PermissoesConhecimento? sugeridas, Guid? conversaOrigem, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (string.IsNullOrWhiteSpace(assunto) || assunto.Length > ServicoSupervisao.TamanhoMaximoAssunto
            || DetectorDadosProtegidos.Mascarar(assunto).Ocorrencias.Count > 0)
        {
            return ResultadoOperacao.Invalida($"assunto obrigatório, sem dado protegido, com até {ServicoSupervisao.TamanhoMaximoAssunto} caracteres");
        }

        if (ServicoSupervisao.ValidarTextoHumano(conteudo) is { } invalido)
        {
            return invalido;
        }

        var permissoes = sugeridas ?? new PermissoesConhecimento(null, null, null, null);
        if (ValidarPermissoes(permissoes, aprovacao: false) is { } erro)
        {
            return ResultadoOperacao.Invalida(erro);
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            // Atendente sugere a partir das conversas atribuídas a ele (decisão 27).
            if (conversaOrigem is { } conv && !await ServicoSupervisao.PodeAtuarNaConversaAsync(c, tx, usuario, conv, t).ConfigureAwait(false))
            {
                return ResultadoOperacao.NaoEncontrada;
            }

            var politica = await PoliticaAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false);
            if (DadoPessoalEmConhecimento(conteudo!, politica) is { } pessoal)
            {
                return ResultadoOperacao.Invalida(pessoal);
            }

            var escopoTipo = permissoes.EscopoTipo ?? (conversaOrigem is null ? nameof(TipoEscopo.Empresa) : nameof(TipoEscopo.Atendimento));
            var escopoRef = permissoes.EscopoTipo is null ? conversaOrigem : permissoes.EscopoRef;
            if (conversaOrigem is { } conversaDaSugestao && !await EscopoDaConversaAsync(c, tx, conversaDaSugestao, escopoTipo, escopoRef, t).ConfigureAwait(false))
            {
                return ResultadoOperacao.Invalida("sugestão ligada a uma conversa fica no escopo desse atendimento ou do cliente dele");
            }
            if (await ConferirReferenciaAsync(c, tx, escopoTipo, escopoRef, t).ConfigureAwait(false) is { } refInvalida)
            {
                return ResultadoOperacao.Invalida(refInvalida);
            }

            var itemId = Guid.NewGuid();
            await using (var item = RepositorioNucleo.Cmd(c, tx, "insert into itens_conhecimento (id, tenant_id, assunto, status, versao_vigente) values (@i, @t, @a, 'Rev', 1)"))
            {
                item.Parameters.AddWithValue("i", itemId);
                item.Parameters.AddWithValue("t", usuario.TenantId);
                item.Parameters.AddWithValue("a", assunto.Trim());
                await item.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            var origem = $"sugestao:usuario:{usuario.Id}" + (conversaOrigem is { } co ? $":conversa:{co}" : string.Empty);
            await InserirVersaoAsync(c, tx, usuario.TenantId, itemId, 1, conteudo!, permissoes.Classificacoes ?? [], escopoTipo, escopoRef,
                permissoes.ValidoAte, origem, aprovador: null, t, proponente: usuario.Id).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, conversaOrigem, "conhecimento_sugerido",
                new { usuario = usuario.Id, item = itemId, versao = 1 }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: 1, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ResumoConhecimento>> ListarAsync(UsuarioSupervisao usuario, string? status, CancellationToken ct = default) =>
        ListarAsync(usuario, status, false, ct);

    /// <summary>Lista o conhecimento visível ao usuário. Com <paramref name="soReavaliacao"/>, só o que aguarda reavaliação.</summary>
    public Task<IReadOnlyList<ResumoConhecimento>> ListarAsync(UsuarioSupervisao usuario, string? status, bool soReavaliacao, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync<IReadOnlyList<ResumoConhecimento>>(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            await using var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + " where (@s::text is null or i.status = @s::text) and " + FiltroVisibilidade
                + " order by i.criado_em desc limit @limite");
            cmd.Parameters.AddWithValue("s", (object?)status ?? DBNull.Value);
            // A lista de reavaliação filtra antes de limitar (revisão da F4.5, G5): nenhum item antigo fica escondido.
            cmd.Parameters.AddWithValue("limite", soReavaliacao ? 100_000 : 500);
            AdicionarParametrosVisibilidade(cmd, usuario);
            var politica = await PoliticaAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false);
            var lista = await LerResumosAsync(cmd, politica, t).ConfigureAwait(false);
            return soReavaliacao ? lista.Where(i => i.PrecisaReavaliacao).ToList() : lista;
        }, ct);
    }

    public Task<DetalheConhecimento?> ObterAsync(UsuarioSupervisao usuario, Guid itemId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        return _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            ResumoConhecimento? resumo;
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + " where i.id = @id and " + FiltroVisibilidade))
            {
                cmd.Parameters.AddWithValue("id", itemId);
                AdicionarParametrosVisibilidade(cmd, usuario);
                resumo = (await LerResumosAsync(cmd, await PoliticaAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false), t).ConfigureAwait(false)).SingleOrDefault();
            }

            if (resumo is null)
            {
                return null;
            }

            var versoes = new List<VersaoConhecimento>();
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                select versao, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em, criada_em
                  from versoes_conhecimento
                 where item_id = @id and (not @atendente or aprovada_por is not null or proposta_por = @u)
                 order by versao
                """))
            {
                // Atendente vê as versões aprovadas e as que ele mesmo propôs, não as edições pendentes de outros.
                cmd.Parameters.AddWithValue("id", itemId);
                cmd.Parameters.AddWithValue("atendente", usuario.Papel == PapelSupervisao.Atendente);
                cmd.Parameters.AddWithValue("u", usuario.Id);
                await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
                while (await r.ReadAsync(t).ConfigureAwait(false))
                {
                    versoes.Add(new VersaoConhecimento(r.GetInt32(0), r.GetString(1), r.GetFieldValue<string[]>(2), r.GetString(3),
                        r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5), r.GetString(6),
                        r.IsDBNull(7) ? null : r.GetGuid(7), r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9)));
                }
            }

            var auditoria = new List<EventoConhecimento>();
            // Atendente: só os próprios eventos e os de aprovação, não os de edições e rejeições de outros (S6).
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                select tipo, dados::text, ocorrido_em from eventos_historico
                 where dados->>'item' = @id
                   and (not @atendente or dados->>'usuario' = @u::text or tipo in ('conhecimento_aprovado', 'conhecimento_revogado'))
                 order by id
                """))
            {
                cmd.Parameters.AddWithValue("id", itemId.ToString());
                cmd.Parameters.AddWithValue("atendente", usuario.Papel == PapelSupervisao.Atendente);
                cmd.Parameters.AddWithValue("u", usuario.Id);
                await using var r = await cmd.ExecuteReaderAsync(t).ConfigureAwait(false);
                while (await r.ReadAsync(t).ConfigureAwait(false))
                {
                    using var doc = JsonDocument.Parse(r.GetString(1));
                    auditoria.Add(new EventoConhecimento(r.GetString(0), doc.RootElement.Clone(), r.GetFieldValue<DateTimeOffset>(2)));
                }
            }

            return new DetalheConhecimento(resumo, versoes, auditoria);
        }, ct);
    }

    /// <summary>
    /// Revisão: grava uma nova versão proposta (texto e permissões sugeridas). Não muda o que o agente usa: num item
    /// aprovado, a versão vigente continua valendo até a nova ser aprovada. Exige a última versão que o revisor viu.
    /// </summary>
    public async Task<ResultadoOperacao> EditarAsync(
        UsuarioSupervisao usuario, Guid itemId, int versaoBase, string? conteudo, PermissoesConhecimento? permissoes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("editar conhecimento exige supervisor ou administrador");
        }

        if (ServicoSupervisao.ValidarTextoHumano(conteudo) is { } invalido)
        {
            return invalido;
        }

        var p = permissoes ?? new PermissoesConhecimento(null, null, null, null);
        if (ValidarPermissoes(p, aprovacao: false) is { } erro)
        {
            return ResultadoOperacao.Invalida(erro);
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var item = await LerItemComTravaAsync(c, tx, usuario, itemId, t).ConfigureAwait(false);
            if (item is null)
            {
                return ResultadoOperacao.NaoEncontrada with { Detalhe = "conhecimento não encontrado" };
            }

            if (item.Value.Status is "Rejeitado" or "Revogado")
            {
                return ResultadoOperacao.Conflito($"conhecimento {item.Value.Status.ToLowerInvariant()} não é mais editado: registre uma nova sugestão", item.Value.UltimaVersao);
            }

            if (item.Value.UltimaVersao != versaoBase)
            {
                return ResultadoOperacao.Conflito("o conhecimento mudou desde a última consulta", item.Value.UltimaVersao);
            }

            var escopoTipo = p.EscopoTipo ?? item.Value.EscopoTipoUltima;
            var escopoRef = p.EscopoTipo is null ? item.Value.EscopoRefUltima : p.EscopoRef;
            if (await ConferirReferenciaAsync(c, tx, escopoTipo, escopoRef, t).ConfigureAwait(false) is { } refInvalida)
            {
                return ResultadoOperacao.Invalida(refInvalida);
            }

            var politica = await PoliticaAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false);
            if (DadoPessoalEmConhecimento(conteudo!, politica) is { } pessoal)
            {
                return ResultadoOperacao.Invalida(pessoal);
            }

            var nova = item.Value.UltimaVersao + 1;
            await InserirVersaoAsync(c, tx, usuario.TenantId, itemId, nova, conteudo!, p.Classificacoes ?? [], escopoTipo, escopoRef, p.ValidoAte,
                $"edicao:usuario:{usuario.Id}:base:{versaoBase}", aprovador: null, t, proponente: usuario.Id).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, null, "conhecimento_editado",
                new { usuario = usuario.Id, item = itemId, versao = nova }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: nova, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Aprovação: cria uma versão aprovada a partir da última proposta, com permissões explícitas definidas pelo
    /// revisor (nunca herdadas da sugestão da IA), autor e data. Supervisor aprova conhecimento comum; NEG, EXC, RES e
    /// CLI exigem administrador; NEG e EXC exigem cliente ou atendimento e validade.
    /// </summary>
    public async Task<ResultadoOperacao> AprovarAsync(
        UsuarioSupervisao usuario, Guid itemId, int versaoProposta, PermissoesConhecimento? permissoes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("aprovar conhecimento exige supervisor ou administrador");
        }

        if (permissoes is null)
        {
            return ResultadoOperacao.Invalida("permissões explícitas são obrigatórias na aprovação");
        }

        if (ValidarPermissoes(permissoes, aprovacao: true) is { } erro)
        {
            return ResultadoOperacao.Invalida(erro);
        }

        var classificacoes = permissoes.Classificacoes!.Select(Enum.Parse<Classificacao>).ToHashSet();
        if (classificacoes.Overlaps(AltoRisco) && !usuario.EhAdministrador)
        {
            return ResultadoOperacao.Proibida("conhecimento NEG, EXC, RES ou CLI só é aprovado por administrador");
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var item = await LerItemComTravaAsync(c, tx, usuario, itemId, t).ConfigureAwait(false);
            if (item is null)
            {
                return ResultadoOperacao.NaoEncontrada with { Detalhe = "conhecimento não encontrado" };
            }

            if (item.Value.Status is "Rejeitado" or "Revogado")
            {
                return ResultadoOperacao.Conflito($"conhecimento {item.Value.Status.ToLowerInvariant()} não pode ser aprovado", item.Value.UltimaVersao);
            }

            if (item.Value.UltimaVersao != versaoProposta)
            {
                return ResultadoOperacao.Conflito("o conhecimento mudou desde a última consulta", item.Value.UltimaVersao);
            }

            if (item.Value.UltimaAprovada)
            {
                return ResultadoOperacao.Conflito("a última versão já está aprovada: edite para propor mudanças", item.Value.UltimaVersao);
            }

            if (item.Value.UltimaRejeitada)
            {
                return ResultadoOperacao.Conflito("a última versão foi rejeitada: edite para propor de novo", item.Value.UltimaVersao);
            }

            // Rebaixar conhecimento de maior risco (ex.: negociação individual virando condição geral) exige administrador.
            if (item.Value.VigenteAltoRisco && !usuario.EhAdministrador)
            {
                return ResultadoOperacao.Proibida("alterar conhecimento NEG, EXC, RES ou CLI aprovado exige administrador");
            }

            // Conhecimento que nasceu de um cliente ou atendimento: só o administrador amplia ou troca o escopo.
            if (!usuario.EhAdministrador
                && item.Value.OrigemEscopoTipo is nameof(TipoEscopo.Cliente) or nameof(TipoEscopo.Atendimento)
                && (permissoes.EscopoTipo != item.Value.OrigemEscopoTipo || permissoes.EscopoRef != item.Value.OrigemEscopoRef))
            {
                return ResultadoOperacao.Proibida("ampliar ou trocar o escopo de conhecimento vindo de um cliente ou atendimento exige administrador");
            }

            if (ServicoSupervisao.ValidarTextoHumano(item.Value.ConteudoUltima) is { } conteudoInvalido)
            {
                return conteudoInvalido;
            }

            var politica = await PoliticaAsync(c, tx, usuario.TenantId, t).ConfigureAwait(false);
            if (DadoPessoalEmConhecimento(item.Value.ConteudoUltima, politica) is { } pessoal)
            {
                return ResultadoOperacao.Invalida(pessoal);
            }

            // Mesma referência de tempo do uso: o prazo conta a partir da aprovação carimbada pelo banco (revisão da F4.5, G3).
            DateTimeOffset agoraDoBanco;
            await using (var agora = RepositorioNucleo.Cmd(c, tx, "select now()"))
            {
                var valor = await agora.ExecuteScalarAsync(t).ConfigureAwait(false);
                agoraDoBanco = valor is DateTimeOffset d ? d : new DateTimeOffset(DateTime.SpecifyKind((DateTime)valor!, DateTimeKind.Utc));
            }

            if (ValidadeForaDoPrazo(classificacoes, permissoes.ValidoAte, politica, agoraDoBanco) is { } prazo)
            {
                return ResultadoOperacao.Invalida(prazo);
            }

            if (await ConferirReferenciaAsync(c, tx, permissoes.EscopoTipo!, permissoes.EscopoRef, t).ConfigureAwait(false) is { } refInvalida)
            {
                return ResultadoOperacao.Invalida(refInvalida);
            }

            var nova = versaoProposta + 1;
            await InserirVersaoAsync(c, tx, usuario.TenantId, itemId, nova, item.Value.ConteudoUltima, permissoes.Classificacoes!, permissoes.EscopoTipo!,
                permissoes.EscopoRef, permissoes.ValidoAte, $"aprovacao:usuario:{usuario.Id}:base:{versaoProposta}", aprovador: usuario.Id, t).ConfigureAwait(false);
            await AtualizarItemAsync(c, tx, itemId, "Aprovado", nova, usuario.Id, t).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, null, "conhecimento_aprovado",
                new { usuario = usuario.Id, item = itemId, versao = nova, classificacoes = permissoes.Classificacoes, escopo = permissoes.EscopoTipo, validoAte = permissoes.ValidoAte }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: nova, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rejeita a versão pendente. Numa sugestão (Rev), o item fica Rejeitado (final). Num conhecimento aprovado com
    /// edição pendente, só a edição é rejeitada: a versão vigente continua valendo e a rejeitada nunca é aprovada.
    /// </summary>
    public async Task<ResultadoOperacao> RejeitarAsync(UsuarioSupervisao usuario, Guid itemId, string? motivo, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!usuario.PodeGerirAtendimento)
        {
            return ResultadoOperacao.Proibida("rejeitar conhecimento exige supervisor ou administrador");
        }

        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500 || DetectorDadosProtegidos.Mascarar(motivo).Ocorrencias.Count > 0)
        {
            return ResultadoOperacao.Invalida("motivo obrigatório, sem dado protegido, com até 500 caracteres");
        }

        var edicao = await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var item = await LerItemComTravaAsync(c, tx, usuario, itemId, t).ConfigureAwait(false);
            if (item is not { Status: "Aprovado" } i)
            {
                return (ResultadoOperacao?)null; // segue pelo caminho da sugestão (ou devolve não encontrada/conflito lá)
            }

            if (i.UltimaVersao == i.VersaoVigente || i.UltimaAprovada || i.UltimaRejeitada)
            {
                return ResultadoOperacao.Conflito("não há edição pendente para rejeitar", i.UltimaVersao);
            }

            await using (var cmd = RepositorioNucleo.Cmd(c, tx,
                "insert into rejeicoes_versao (item_id, versao, tenant_id, rejeitada_por, motivo) values (@i, @v, @t, @u, @m)"))
            {
                cmd.Parameters.AddWithValue("i", itemId);
                cmd.Parameters.AddWithValue("v", i.UltimaVersao);
                cmd.Parameters.AddWithValue("t", usuario.TenantId);
                cmd.Parameters.AddWithValue("u", usuario.Id);
                cmd.Parameters.AddWithValue("m", motivo.Trim());
                await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, null, "edicao_rejeitada",
                new { usuario = usuario.Id, item = itemId, versao = i.UltimaVersao, motivo = motivo.Trim() }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: i.VersaoVigente, Id: itemId);
        }, ct).ConfigureAwait(false);

        return edicao ?? await EncerrarAsync(usuario, itemId, motivo, "Rejeitado", "Rev", permitido: true, string.Empty, "conhecimento_rejeitado", ct).ConfigureAwait(false);
    }

    /// <summary>Revoga conhecimento aprovado: a partir daqui, nenhuma resposta nova nem mensagem na fila o usa.</summary>
    public Task<ResultadoOperacao> RevogarAsync(UsuarioSupervisao usuario, Guid itemId, string? motivo, CancellationToken ct = default) =>
        EncerrarAsync(usuario, itemId, motivo, "Revogado", "Aprovado", usuario is { EhAdministrador: true }, "revogar conhecimento exige administrador", "conhecimento_revogado", ct);

    private async Task<ResultadoOperacao> EncerrarAsync(
        UsuarioSupervisao usuario, Guid itemId, string? motivo, string novoStatus, string statusExigido, bool permitido, string proibido, string evento, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usuario);
        if (!permitido)
        {
            return ResultadoOperacao.Proibida(proibido);
        }

        if (string.IsNullOrWhiteSpace(motivo) || motivo.Length > 500 || DetectorDadosProtegidos.Mascarar(motivo).Ocorrencias.Count > 0)
        {
            return ResultadoOperacao.Invalida("motivo obrigatório, sem dado protegido, com até 500 caracteres");
        }

        return await _executor.ExecutarAutenticadoAsync(usuario.TenantId, usuario.Credencial, async (c, tx, t) =>
        {
            var item = await LerItemComTravaAsync(c, tx, usuario, itemId, t).ConfigureAwait(false);
            if (item is null)
            {
                return ResultadoOperacao.NaoEncontrada with { Detalhe = "conhecimento não encontrado" };
            }

            if (item.Value.Status != statusExigido)
            {
                return ResultadoOperacao.Conflito($"conhecimento está {item.Value.Status}; esperado {statusExigido}", item.Value.UltimaVersao);
            }

            await AtualizarItemAsync(c, tx, itemId, novoStatus, item.Value.VersaoVigente, usuario.Id, t).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, usuario.TenantId, null, evento,
                new { usuario = usuario.Id, item = itemId, motivo = motivo.Trim() }, t).ConfigureAwait(false);
            return new ResultadoOperacao(SituacaoOperacao.Ok, Versao: item.Value.VersaoVigente, Id: itemId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Regras que não dependem do banco. Na aprovação, tudo é explícito e completo.</summary>
    private string? ValidarPermissoes(PermissoesConhecimento p, bool aprovacao)
    {
        var nomes = p.Classificacoes ?? [];
        if (nomes.Any(n => !Enum.GetNames<Classificacao>().Contains(n, StringComparer.Ordinal)))
        {
            return "classificação desconhecida (use o nome exato: " + string.Join(", ", Enum.GetNames<Classificacao>()) + ")";
        }

        if (p.EscopoTipo is not null && !Enum.GetNames<TipoEscopo>().Contains(p.EscopoTipo, StringComparer.Ordinal))
        {
            return "escopo desconhecido";
        }

        if (p.EscopoTipo == nameof(TipoEscopo.Proposta))
        {
            return "escopo de proposta está fora de uso (decisão 18: secretária de atendimento)";
        }

        if (p.ValidoAte is { } v && v <= relogio.GetUtcNow())
        {
            return "a validade precisa ser futura";
        }

        if (!aprovacao)
        {
            return null;
        }

        var classificacoes = nomes.Select(Enum.Parse<Classificacao>).ToHashSet();
        if (classificacoes.Count == 0)
        {
            return "a aprovação exige ao menos uma classificação";
        }

        if (p.EscopoTipo is null)
        {
            return "a aprovação exige o escopo";
        }

        // Só PUB pode ficar sem validade (governanca-conhecimento.md, seção 3).
        if (p.ValidoAte is null && classificacoes.Any(c => c != Classificacao.Publico))
        {
            return "só conhecimento exclusivamente PUB pode ficar sem validade";
        }

        if (classificacoes.Overlaps(Comerciais)
            && (p.EscopoTipo is not (nameof(TipoEscopo.Cliente) or nameof(TipoEscopo.Atendimento)) || p.EscopoRef is null || p.ValidoAte is null))
        {
            return "NEG e EXC exigem escopo de cliente ou atendimento e validade";
        }

        return null;
    }

    /// <summary>
    /// Dado pessoal não vira conhecimento (decisão 29; governanca-conhecimento.md, seção 3). Só e-mail institucional,
    /// listado na política, é aceito. Qualquer outro dado protegido (CEP/endereço, documentos, cartão) é recusado em
    /// todo conhecimento, qualquer que seja o escopo (revisão pós-F3, G1/S5). A mensagem cita só a categoria.
    /// </summary>
    internal static string? DadoPessoalEmConhecimento(string texto, PoliticaTenant politica)
    {
        foreach (var o in DetectorDadosProtegidos.Mascarar(texto).Ocorrencias)
        {
            if (o.Categoria == CategoriaDadoProtegido.Email)
            {
                if (!politica.EhEmailInstitucional(o.Valor))
                {
                    return "e-mail fora da lista institucional da política é dado pessoal e não entra em conhecimento";
                }
            }
            else if (o.Categoria == CategoriaDadoProtegido.Cep)
            {
                // Endereço institucional pode ser conhecimento público aprovado; endereço de cliente, não (decisão do Victor).
                if (!politica.EhCepInstitucional(o.Valor))
                {
                    return "CEP fora da lista institucional da política é endereço particular e não entra em conhecimento";
                }
            }
            else
            {
                return $"dado pessoal ({o.Categoria}) não entra em conhecimento";
            }
        }

        return null;
    }

    /// <summary>Prazo máximo de validade por classificação, definido na política (decisão 28).</summary>
    private static string? ValidadeForaDoPrazo(IReadOnlySet<Classificacao> classificacoes, DateTimeOffset? validoAte, PoliticaTenant politica, DateTimeOffset aprovacao)
    {
        var prazos = classificacoes.Where(politica.ValidadeMaximaDias.ContainsKey).Select(c => (c, dias: politica.ValidadeMaximaDias[c])).ToList();
        if (prazos.Count == 0)
        {
            return null;
        }

        var (classificacao, dias) = prazos.MinBy(x => x.dias);
        return validoAte is { } v && v <= aprovacao.AddDays(dias)
            ? null
            : $"a política limita a validade de {classificacao} a {dias} dias";
    }

    private static async Task<PoliticaTenant> PoliticaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenant, CancellationToken ct) =>
        await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, tenant, ct).ConfigureAwait(false)
        ?? new PoliticaTenant { TenantId = tenant, Versao = 0, AssuntosAutonomos = new HashSet<string>() };

    /// <summary>
    /// Menor privilégio (decisão 24; governanca-conhecimento.md, seção 3a). Vale a classificação APROVADA: um item
    /// aparece só se todas as versões aprovadas estão dentro do que o papel vê (a classificação sugerida numa proposta
    /// não esconde nem libera nada). Administrador: tudo. Supervisor: sem NEG, EXC e RES aprovados. Atendente: só PUB e
    /// TMP, de escopo da empresa ou de conversa atribuída a ele, e apenas itens já aprovados ou sugeridos por ele.
    /// </summary>
    private const string FiltroVisibilidade = """
        (@papel = 'Administrador'
         or (@papel = 'Supervisor' and not exists (
               select 1 from versoes_conhecimento x
                where x.item_id = i.id and x.aprovada_por is not null and x.classificacoes && array['Negociacao', 'Excecao', 'Restrito']))
         or (@papel = 'Atendente'
             and not exists (
               select 1 from versoes_conhecimento x
                where x.item_id = i.id
                  and ((x.aprovada_por is not null and x.classificacoes && array['Interno', 'Cliente', 'Negociacao', 'Excecao', 'Restrito'])
                       or (x.escopo_tipo = 'Atendimento' and not exists (select 1 from conversas cv where cv.id = x.escopo_ref and cv.atribuida_a = @u))
                       or (x.escopo_tipo = 'Cliente' and not exists (select 1 from conversas cv where cv.cliente_id = x.escopo_ref and cv.atribuida_a = @u))
                       or x.escopo_tipo = 'Proposta'))
             and (i.status = 'Aprovado'
                  or (i.status = 'Rev' and exists (
                        select 1 from versoes_conhecimento x where x.item_id = i.id and x.versao = 1 and x.proposta_por = @u)))))
        """;

    private static void AdicionarParametrosVisibilidade(NpgsqlCommand cmd, UsuarioSupervisao usuario)
    {
        cmd.Parameters.AddWithValue("papel", usuario.Papel.ToString());
        cmd.Parameters.AddWithValue("u", usuario.Id);
    }

    private static async Task<string?> ConferirReferenciaAsync(NpgsqlConnection c, NpgsqlTransaction tx, string escopoTipo, Guid? escopoRef, CancellationToken ct)
    {
        switch (escopoTipo)
        {
            case nameof(TipoEscopo.Empresa):
                return escopoRef is null ? null : "escopo de empresa não leva referência";
            case nameof(TipoEscopo.Cliente):
                return escopoRef is { } cli && await ExisteAsync(c, tx, "clientes", cli, ct).ConfigureAwait(false) ? null : "cliente do escopo não encontrado";
            case nameof(TipoEscopo.Atendimento):
                return escopoRef is { } conv && await ExisteAsync(c, tx, "conversas", conv, ct).ConfigureAwait(false) ? null : "atendimento do escopo não encontrado";
            default:
                return "escopo inválido";
        }
    }

    /// <summary>Escopo da própria conversa de origem ou do cliente dela.</summary>
    private static async Task<bool> EscopoDaConversaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversa, string escopoTipo, Guid? escopoRef, CancellationToken ct)
    {
        if (escopoTipo == nameof(TipoEscopo.Atendimento))
        {
            return escopoRef == conversa;
        }

        if (escopoTipo != nameof(TipoEscopo.Cliente) || escopoRef is null)
        {
            return false;
        }

        await using var cmd = RepositorioNucleo.Cmd(c, tx, "select 1 from conversas where id = @c and cliente_id = @cl");
        cmd.Parameters.AddWithValue("c", conversa);
        cmd.Parameters.AddWithValue("cl", escopoRef.Value);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    // Tabela vem só de literais internos. Com RLS, registro de outra empresa "não existe".
    private static async Task<bool> ExisteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string tabela, Guid id, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, $"select 1 from {tabela} where id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static async Task InserirVersaoAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, Guid itemId, int versao, string conteudo, IReadOnlyList<string> classificacoes,
        string escopoTipo, Guid? escopoRef, DateTimeOffset? validoAte, string origem, Guid? aprovador, CancellationToken ct, Guid? proponente = null)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            insert into versoes_conhecimento (item_id, versao, tenant_id, conteudo, classificacoes, escopo_tipo, escopo_ref, valido_ate, origem, aprovada_por, aprovada_em, proposta_por)
            values (@i, @v, @t, @c, @cl, @e, @er, @va, @o, @ap, case when @ap::uuid is null then null else now() end, @pp)
            """);
        cmd.Parameters.Add(new NpgsqlParameter("pp", NpgsqlDbType.Uuid) { Value = (object?)proponente ?? DBNull.Value });
        cmd.Parameters.AddWithValue("i", itemId);
        cmd.Parameters.AddWithValue("v", versao);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", conteudo);
        cmd.Parameters.Add(new NpgsqlParameter("cl", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = classificacoes.Distinct(StringComparer.Ordinal).ToArray() });
        cmd.Parameters.AddWithValue("e", escopoTipo);
        cmd.Parameters.AddWithValue("er", (object?)escopoRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("va", (object?)validoAte ?? DBNull.Value);
        cmd.Parameters.AddWithValue("o", origem);
        cmd.Parameters.Add(new NpgsqlParameter("ap", NpgsqlDbType.Uuid) { Value = (object?)aprovador ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task AtualizarItemAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid itemId, string status, int versaoVigente, Guid usuario, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "update itens_conhecimento set status = @s, versao_vigente = @v, alterado_por = @u where id = @id");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("v", versaoVigente);
        cmd.Parameters.AddWithValue("u", usuario);
        cmd.Parameters.AddWithValue("id", itemId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private readonly record struct ItemTravado(
        string Status, int VersaoVigente, int UltimaVersao, bool UltimaAprovada, string ConteudoUltima, string EscopoTipoUltima, Guid? EscopoRefUltima,
        bool UltimaRejeitada, bool VigenteAltoRisco, string OrigemEscopoTipo, Guid? OrigemEscopoRef);

    /// <summary>FOR UPDATE no item: aprovar, editar, rejeitar e revogar o mesmo item acontecem um de cada vez.</summary>
    private static async Task<ItemTravado?> LerItemComTravaAsync(NpgsqlConnection c, NpgsqlTransaction tx, UsuarioSupervisao usuario, Guid itemId, CancellationToken ct)
    {
        // Só se opera sobre o que se vê (revisão pós-F3, S6): item fora da visibilidade do papel "não existe".
        await using (var visivel = RepositorioNucleo.Cmd(c, tx, "select 1 from itens_conhecimento i where i.id = @id and " + FiltroVisibilidade))
        {
            visivel.Parameters.AddWithValue("id", itemId);
            AdicionarParametrosVisibilidade(visivel, usuario);
            if (await visivel.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                return null;
            }
        }

        // Trava consultiva exclusiva: o worker pega a compartilhada ao revalidar as fontes citadas, então revogar e
        // enviar não se cruzam (resta só a chamada HTTP já iniciada, como no resto do envio).
        await using (var consultiva = RepositorioNucleo.Cmd(c, tx, "select pg_advisory_xact_lock(hashtextextended(@id::text, 0))"))
        {
            consultiva.Parameters.AddWithValue("id", itemId);
            await consultiva.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }

        await using (var trava = RepositorioNucleo.Cmd(c, tx, "select 1 from itens_conhecimento where id = @id for update"))
        {
            trava.Parameters.AddWithValue("id", itemId);
            if (await trava.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
            {
                return null;
            }
        }

        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            select i.status, i.versao_vigente, v.versao, v.aprovada_por is not null, v.conteudo, v.escopo_tipo, v.escopo_ref,
                   exists (select 1 from rejeicoes_versao r where r.item_id = v.item_id and r.versao = v.versao),
                   exists (select 1 from versoes_conhecimento a
                            where a.item_id = i.id and a.versao = i.versao_vigente and a.aprovada_por is not null
                              and a.classificacoes && array['Negociacao', 'Excecao', 'Restrito', 'Cliente']),
                   o.escopo_tipo, o.escopo_ref
              from itens_conhecimento i
              join versoes_conhecimento o on o.item_id = i.id and o.versao = 1
              join versoes_conhecimento v on v.item_id = i.id
             where i.id = @id
             order by v.versao desc limit 1
            """);
        cmd.Parameters.AddWithValue("id", itemId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new ItemTravado(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetBoolean(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6),
            r.GetBoolean(7), r.GetBoolean(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetGuid(10));
    }

    private const string SelectResumo = """
        select i.id, i.assunto, i.status, i.versao_vigente, u.versao, i.criado_em,
               exists (select 1 from rejeicoes_versao r where r.item_id = i.id and r.versao = u.versao),
               vg.classificacoes, vg.valido_ate, vg.aprovada_em
          from itens_conhecimento i
          cross join lateral (select max(v.versao) as versao from versoes_conhecimento v where v.item_id = i.id) u
          left join versoes_conhecimento vg on vg.item_id = i.id and vg.versao = i.versao_vigente and vg.aprovada_por is not null
        """;

    /// <summary>Itens aprovados cuja validade passa do prazo da política informada (para registrar a reavaliação).</summary>
    internal static async Task<List<Guid>> ItensParaReavaliarAsync(NpgsqlConnection c, NpgsqlTransaction tx, PoliticaTenant politica, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, SelectResumo + " where i.status = 'Aprovado'");
        return (await LerResumosAsync(cmd, politica, ct).ConfigureAwait(false)).Where(i => i.PrecisaReavaliacao).Select(i => i.Id).ToList();
    }

    private static async Task<List<ResumoConhecimento>> LerResumosAsync(NpgsqlCommand cmd, PoliticaTenant politica, CancellationToken ct)
    {
        var lista = new List<ResumoConhecimento>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var status = r.GetString(2);
            var vigente = r.GetInt32(3);
            var ultima = r.IsDBNull(4) ? vigente : r.GetInt32(4);
            var reavaliar = status == "Aprovado" && !r.IsDBNull(7)
                && ElegibilidadeConhecimento.ExcedeValidadeDaPolitica(
                    new HashSet<Classificacao>(r.GetFieldValue<string[]>(7).Select(Enum.Parse<Classificacao>)),
                    r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), politica);
            lista.Add(new ResumoConhecimento(r.GetGuid(0), r.GetString(1), status, vigente, ultima,
                status == "Aprovado" && ultima > vigente && !r.GetBoolean(6), r.GetFieldValue<DateTimeOffset>(5), reavaliar));
        }

        return lista;
    }
}
