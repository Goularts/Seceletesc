using Npgsql;
using Seceletesc.Aplicacao.Envio;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Conversas;
using Seceletesc.Infraestrutura.Banco;

namespace Seceletesc.Infraestrutura.Nucleo;

public enum ResultadoCiclo
{
    FilaVazia,
    Enviada,
    Cancelada,
    Incerta,
    DevolvidaParaFila,
    Ignorada,
}

/// <summary>
/// Worker de envio (plano técnico, seção 4.4). Para cada mensagem da fila, numa transação com a mesma trava
/// da tomada de controle humana (FOR UPDATE na conversa), revalida estado, versão, fontes, janela de 24h e
/// validade da mensagem. Marca "enviando" antes da chamada HTTP. Resultado ambíguo vira "incerta" e
/// NUNCA é repetido: preferimos o risco de não enviar ao de enviar duas vezes.
/// </summary>
public sealed class ProcessadorEnvio(NpgsqlDataSource dataSourceWorker, IGatewayWhatsApp gateway, TimeProvider relogio, TimeSpan? timeoutGateway = null)
{
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    /// <summary>Limite da chamada ao WhatsApp. Sempre menor que o lease, para o envio terminar antes de a reserva vencer.</summary>
    public static readonly TimeSpan TimeoutGatewayPadrao = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeoutGateway = timeoutGateway is { } t && t < Lease ? t : TimeoutGatewayPadrao;

    private readonly ExecutorTenant _executor = new(dataSourceWorker);

    public async Task<ResultadoCiclo> ProcessarProximoAsync(CancellationToken ct = default)
    {
        var reserva = await ReservarAsync(ct).ConfigureAwait(false);
        if (reserva is null)
        {
            return ResultadoCiclo.FilaVazia;
        }

        var (outboxId, tenantId) = reserva.Value;

        var preparo = await _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            var item = await LerItemComTravaAsync(c, tx, outboxId, t).ConfigureAwait(false);
            if (item is null)
            {
                return (ResultadoCiclo.Ignorada, (Item?)null);
            }

            if (item.Status == "enviando")
            {
                // Reserva expirou durante um envio (queda do worker): não se sabe se saiu. Nunca reenviar.
                await AtualizarAsync(c, tx, outboxId, "incerta", "lease expirado durante o envio", null, null, false, t).ConfigureAwait(false);
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, item.ConversaId, "envio_incerto", new { outbox = outboxId, motivo = "lease expirado" }, t).ConfigureAwait(false);
                return (ResultadoCiclo.Incerta, null);
            }

            if (item.Status != "pendente")
            {
                return (ResultadoCiclo.Ignorada, null);
            }

            var conversa = (await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, item.ConversaId, t).ConfigureAwait(false))!;
            var politica = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, tenantId, t).ConfigureAwait(false);
            var agora = relogio.GetUtcNow();

            var motivo = await MotivoParaCancelarAsync(c, tx, item, conversa, politica, agora, t).ConfigureAwait(false);
            if (motivo is not null)
            {
                await AtualizarAsync(c, tx, outboxId, "cancelada", motivo, null, null, false, t).ConfigureAwait(false);
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "envio_cancelado", new { outbox = outboxId, motivo }, t).ConfigureAwait(false);
                if (motivo == MotivoForaDaJanela)
                {
                    await MarcarRetomadaHumanaAsync(c, tx, conversa.Id, t).ConfigureAwait(false);
                    await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "retomada_humana_marcada", new { motivo }, t).ConfigureAwait(false);
                }

                // A resposta não sairá: a automação é interrompida e a supervisão acionada (invariantes 3 e 7).
                // Se o motivo é a intervenção humana, a conversa já está com o humano.
                // Mensagem humana que não sai (vencida, fora da janela) também: o humano pode achar que respondeu.
                if (item.Tipo is null or "resposta" or TipoHumana && motivo != MotivoEstadoMudou)
                {
                    await TransferirParaSupervisaoAsync(c, tx, conversa.Id, "envio_cancelado: " + motivo, t).ConfigureAwait(false);
                }

                return (ResultadoCiclo.Cancelada, null);
            }

            await AtualizarAsync(c, tx, outboxId, "enviando", null, null, agora + Lease, true, t).ConfigureAwait(false);
            return (ResultadoCiclo.Enviada, item);
        }, ct).ConfigureAwait(false);

        if (preparo.Item2 is not { } envio)
        {
            return preparo.Item1;
        }

        ResultadoEnvio resultado;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(_timeoutGateway);
            resultado = await gateway.EnviarTextoAsync(envio.WaIdCliente, envio.Texto, limite.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Qualquer erro após iniciar o envio é ambíguo: tratado como incerto, nunca repetido.
        catch (Exception)
#pragma warning restore CA1031
        {
            resultado = new ResultadoEnvio(SituacaoEnvio.Incerta, null, "exceção no gateway");
        }

        return await _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            switch (resultado.Situacao)
            {
                case SituacaoEnvio.Enviada:
                    // Se outra instância já marcou "incerta" (reserva vencida), o registro do envio ainda entra no histórico.
                    var confirmada = await AtualizarAsync(c, tx, outboxId, "enviada", null, resultado.Wamid, null, false, t, exigirStatus: "enviando").ConfigureAwait(false);
                    await RepositorioNucleo.InserirMensagemAsync(c, tx, Guid.NewGuid(), tenantId, envio.ConversaId, "saida", envio.Texto, resultado.Wamid, relogio.GetUtcNow(), t).ConfigureAwait(false);
                    await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, envio.ConversaId, confirmada ? "mensagem_enviada" : "mensagem_enviada_apos_incerta",
                        new { outbox = outboxId, decisao = envio.DecisaoId, autor = envio.AutorId }, t).ConfigureAwait(false);
                    return ResultadoCiclo.Enviada;
                case SituacaoEnvio.NaoEnviada:
                    // Com certeza não saiu: volta a "pendente" e será revalidada por completo na próxima reserva.
                    await AtualizarAsync(c, tx, outboxId, "pendente", resultado.Detalhe, null, null, false, t).ConfigureAwait(false);
                    return ResultadoCiclo.DevolvidaParaFila;
                case SituacaoEnvio.FalhaDefinitiva:
                    await AtualizarAsync(c, tx, outboxId, "cancelada", "falha definitiva: " + resultado.Detalhe, null, null, false, t).ConfigureAwait(false);
                    await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, envio.ConversaId, "envio_cancelado", new { outbox = outboxId, motivo = resultado.Detalhe }, t).ConfigureAwait(false);
                    await TransferirParaSupervisaoAsync(c, tx, envio.ConversaId, "falha definitiva no envio", t).ConfigureAwait(false);
                    return ResultadoCiclo.Cancelada;
                default:
                    await AtualizarAsync(c, tx, outboxId, "incerta", resultado.Detalhe, null, null, false, t).ConfigureAwait(false);
                    await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, envio.ConversaId, "envio_incerto", new { outbox = outboxId, motivo = resultado.Detalhe }, t).ConfigureAwait(false);
                    // Um humano precisa conferir se a mensagem chegou.
                    await TransferirParaSupervisaoAsync(c, tx, envio.ConversaId, "envio incerto", t).ConfigureAwait(false);
                    return ResultadoCiclo.Incerta;
            }
        }, ct).ConfigureAwait(false);
    }

    private const string MotivoForaDaJanela = "fora da janela de 24h";
    private const string MotivoEstadoMudou = "estado da conversa mudou";
    private const string TipoHumana = "humana";

    private static async Task<string?> MotivoParaCancelarAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Item item, Conversa conversa, Dominio.Politicas.PoliticaTenant? politica, DateTimeOffset agora, CancellationToken ct)
    {
        // Falha fechado: mensagem automática cuja decisão não aparece não é tratada como humana nem enviada.
        if (item.Tipo is null)
        {
            return "decisão da mensagem não encontrada";
        }

        if (agora >= item.ExpiraEm)
        {
            return item.Tipo == TipoHumana ? "mensagem humana vencida" : "mensagem automática vencida";
        }

        // Mensagem humana: escrita pela supervisão, que tem a palavra final. Sai mesmo que a conversa já tenha sido
        // liberada logo depois (responder e liberar). Só a janela de 24h e a validade da mensagem se aplicam.
        if (item.Tipo == TipoHumana)
        {
            return conversa.DentroDaJanela24h(agora) ? null : MotivoForaDaJanela;
        }

        if (politica is null)
        {
            return "política ausente";
        }

        if (item.Tipo == "resposta")
        {
            if (conversa.Estado != EstadoConversa.Automatico || conversa.VersaoEstado != item.VersaoDecisao)
            {
                return MotivoEstadoMudou;
            }

            var fontes = await RepositorioNucleo.ObterFontesAsync(c, tx, item.FontesCitadas, ct).ConfigureAwait(false);
            if (item.FontesCitadas.Count == 0
                || item.FontesCitadas.Any(k => !fontes.TryGetValue(k, out var f) || !ElegibilidadeConhecimento.EhElegivel(f, politica, conversa, agora)))
            {
                return "fonte citada deixou de ser elegível";
            }
        }
        else if (conversa.Estado != EstadoConversa.TransferenciaPendente || conversa.VersaoEstado != item.VersaoDecisao)
        {
            return MotivoEstadoMudou;
        }
        else if (!string.Equals(item.Texto, politica.TextoAvisoTransferencia, StringComparison.Ordinal))
        {
            return "texto do aviso diferente do configurado na política";
        }

        return conversa.DentroDaJanela24h(agora) ? null : MotivoForaDaJanela;
    }

    private async Task<(Guid OutboxId, Guid TenantId)?> ReservarAsync(CancellationToken ct)
    {
        await using var c = await dataSourceWorker.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("select outbox_id, tenant_id from reservar_envio(@s)", c);
        cmd.Parameters.AddWithValue("s", (int)Lease.TotalSeconds);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? (r.GetGuid(0), r.GetGuid(1)) : null;
    }

    private static async Task<Item?> LerItemComTravaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid outboxId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            select o.conversa_id, o.decisao_id, o.texto, o.status, o.expira_em,
                   case when o.decisao_id is null and o.autor_id is not null then 'humana' else d.tipo end,
                   coalesce(d.versao_estado, 0),
                   coalesce(d.fontes_citadas::text, '[]'), cl.wa_id, o.autor_id
              from outbox o
              left join decisoes_envio d on d.id = o.decisao_id
              join conversas cv on cv.id = o.conversa_id
              join clientes cl on cl.id = cv.cliente_id
             where o.id = @id
             for update of o
            """);
        cmd.Parameters.AddWithValue("id", outboxId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new Item(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), RepositorioNucleo.LerChavesJson(r.GetString(7)), r.GetString(8), r.IsDBNull(9) ? null : r.GetGuid(9));
    }

    private static async Task<bool> AtualizarAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid id, string status, string? motivo, string? wamid, DateTimeOffset? leaseAte, bool incrementar,
        CancellationToken ct, string? exigirStatus = null)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            update outbox set status = @s, motivo = coalesce(@m, motivo), wamid = coalesce(@w, wamid), lease_ate = @l,
                   tentativas = tentativas + case when @inc then 1 else 0 end
             where id = @id and (@exigir::text is null or status = @exigir::text)
            """);
        cmd.Parameters.AddWithValue("exigir", (object?)exigirStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("m", (object?)motivo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("w", (object?)wamid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", (object?)leaseAte ?? DBNull.Value);
        cmd.Parameters.AddWithValue("inc", incrementar);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task TransferirParaSupervisaoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, string motivo, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "select transferir_para_supervisao(@c, @m)");
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("m", motivo);
        await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }

    private static async Task MarcarRetomadaHumanaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "update conversas set marcada_retomada_humana = true where id = @id");
        cmd.Parameters.AddWithValue("id", conversaId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record Item(
        Guid ConversaId, Guid? DecisaoId, string Texto, string Status, DateTimeOffset ExpiraEm,
        string? Tipo, long VersaoDecisao, List<ChaveFonte> FontesCitadas, string WaIdCliente, Guid? AutorId);
}
