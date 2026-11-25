using Npgsql;

namespace Seceletesc.Infraestrutura.Nucleo;

/// <summary>
/// Trabalhador da fila da IA (plano técnico, seção 4.8). Reserva a próxima mensagem com lease (se o processo cair,
/// ela volta à fila) e trata. A decisão sai uma única vez (ver <see cref="ProcessadorMensagemRecebida.TratarAsync"/>).
/// </summary>
public sealed class TrabalhadorIa(NpgsqlDataSource dataSourceApp, ProcessadorMensagemRecebida processador)
{
    /// <summary>Tempo em que a mensagem fica reservada: cobre a geração do rascunho com folga.</summary>
    public const int LeaseSegundos = 120;

    /// <summary>Trata a próxima mensagem disponível. Nulo se a fila está vazia.</summary>
    public async Task<ResultadoProcessamento?> ProcessarProximoAsync(CancellationToken ct = default)
    {
        Guid mensagemId;
        Guid tenantId;
        await using (var c = await dataSourceApp.OpenConnectionAsync(ct).ConfigureAwait(false))
        await using (var cmd = new NpgsqlCommand($"select mensagem_id, tenant_id from reservar_ia({LeaseSegundos})", c))
        await using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
            {
                return null;
            }

            mensagemId = r.GetGuid(0);
            tenantId = r.GetGuid(1);
        }

        return await processador.TratarAsync(tenantId, mensagemId, ct).ConfigureAwait(false);
    }
}
