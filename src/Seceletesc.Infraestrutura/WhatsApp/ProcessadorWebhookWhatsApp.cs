using Npgsql;
using Seceletesc.Infraestrutura.Nucleo;

namespace Seceletesc.Infraestrutura.WhatsApp;

/// <summary>Resultado do processamento de um webhook: quantos eventos foram registrados, repetidos, ignorados ou recusados.</summary>
public sealed record ResultadoWebhook(int Registrados, int Repetidos, int Ignorados, int Recusados = 0);

/// <summary>
/// Distribui os eventos de um webhook já autenticado (assinatura conferida). Só registra: a resposta da IA é do
/// trabalhador da IA (fila durável). Tudo é idempotente pelo wamid, então a Meta pode reenviar sem efeito duplicado.
/// Número desconhecido (sem empresa) é ignorado: nenhum dado é gravado.
/// </summary>
public sealed class ProcessadorWebhookWhatsApp(NpgsqlDataSource dataSourceApp, ProcessadorMensagemRecebida processador)
{
    public async Task<ResultadoWebhook> ProcessarAsync(IReadOnlyList<EventoWhatsApp> eventos, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(eventos);
        var tenants = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        int registrados = 0, repetidos = 0, ignorados = 0, recusados = 0;

        // Ecos primeiro (revisão da F4, S1): a resposta humana pelo celular silencia a IA antes de qualquer outra coisa
        // do mesmo lote ser processada.
        foreach (var evento in eventos.OrderBy(e => e is EcoAppWhatsApp ? 0 : 1))
        {
            if (!tenants.TryGetValue(evento.PhoneNumberId, out var tenant))
            {
                tenant = await ResolverTenantAsync(evento.PhoneNumberId, ct).ConfigureAwait(false);
                tenants[evento.PhoneNumberId] = tenant;
            }

            if (tenant is not { } t)
            {
                ignorados++;
                continue;
            }

            bool novo;
            try
            {
                novo = await RegistrarAsync(evento, t, ct).ConfigureAwait(false);
            }
            catch (PostgresException e) when (e.SqlState.StartsWith("22", StringComparison.Ordinal) || e.SqlState.StartsWith("23", StringComparison.Ordinal))
            {
                // Erro determinístico de dado (repetir não adianta): o evento é recusado e os demais seguem. Erro
                // transitório (conexão, deadlock) sobe e o webhook responde 500 para a Meta reenviar o lote todo.
                recusados++;
                continue;
            }

            if (novo)
            {
                registrados++;
            }
            else
            {
                repetidos++;
            }
        }

        return new ResultadoWebhook(registrados, repetidos, ignorados, recusados);
    }

    private async Task<bool> RegistrarAsync(EventoWhatsApp evento, Guid t, CancellationToken ct) =>
            evento switch
            {
                MensagemTextoWhatsApp m => (await processador.RegistrarAsync(new MensagemRecebida(t, m.WaIdCliente, m.Texto, m.Wamid, m.Em), ct).ConfigureAwait(false)).Resultado
                    != ResultadoProcessamento.Duplicada,
                MensagemNaoTextoWhatsApp m => await processador.RegistrarNaoTextoAsync(t, m.WaIdCliente, m.Tipo, m.Wamid, m.Em, ct).ConfigureAwait(false)
                    != ResultadoProcessamento.Duplicada,
                EcoAppWhatsApp e => await processador.RegistrarEcoAsync(t, e.WaIdCliente, e.Texto, e.Tipo, e.Wamid, e.Em, ct).ConfigureAwait(false)
                    != ResultadoProcessamento.Duplicada,
                StatusWhatsApp s => await processador.RegistrarStatusAsync(t, s.Wamid, s.Status, s.Em, s.ErroCodigo, ct).ConfigureAwait(false),
                _ => false,
            };

    private async Task<Guid?> ResolverTenantAsync(string phoneNumberId, CancellationToken ct)
    {
        await using var c = await dataSourceApp.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand("select resolver_tenant_por_phone_number_id(@p)", c);
        cmd.Parameters.AddWithValue("p", phoneNumberId);
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is Guid g ? g : null;
    }
}
