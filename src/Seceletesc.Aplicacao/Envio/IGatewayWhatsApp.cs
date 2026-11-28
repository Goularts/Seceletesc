namespace Seceletesc.Aplicacao.Envio;

public enum SituacaoEnvio
{
    /// <summary>O WhatsApp aceitou a mensagem.</summary>
    Enviada,

    /// <summary>Com certeza não saiu (ex.: conexão recusada antes do envio). Pode tentar de novo.</summary>
    NaoEnviada,

    /// <summary>Recusada de forma definitiva (ex.: destinatário inválido). Não tenta de novo.</summary>
    FalhaDefinitiva,

    /// <summary>Não se sabe se saiu (ex.: tempo esgotado depois do envio). Nunca tenta de novo.</summary>
    Incerta,
}

public sealed record ResultadoEnvio(SituacaoEnvio Situacao, string? Wamid, string? Detalhe);

/// <summary>
/// Único ponto que envia mensagens ao cliente (plano técnico, seção 4.4). Só existe envio de texto:
/// mensagens interativas (botões, listas) e templates não têm caminho no sistema (invariantes 1 e 24).
/// </summary>
public interface IGatewayWhatsApp
{
    /// <param name="tenantId">Empresa dona da conversa.</param>
    /// <param name="numeroOrigem">phone_number_id em que o cliente escreveu por último (o número que ele conhece). Sem ele, nada sai.</param>
    Task<ResultadoEnvio> EnviarTextoAsync(Guid tenantId, string? numeroOrigem, string waIdDestino, string texto, CancellationToken ct);
}
