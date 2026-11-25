using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Seceletesc.Aplicacao.Envio;

namespace Seceletesc.Infraestrutura.WhatsApp;

/// <summary>Configuração da integração com a Cloud API. Nada aqui vai para o Git (user-secrets ou variáveis de ambiente).</summary>
public sealed class OpcoesWhatsApp
{
    /// <summary>Desligado por padrão: sem autorização do Victor, nada sai para a Meta (decisão 31).</summary>
    public bool EnvioHabilitado { get; set; }

    /// <summary>Liga o trabalhador da fila da IA. Até a F5, o motor é indisponível: tudo vai para supervisão.</summary>
    public bool ProcessamentoHabilitado { get; set; }

    public string? TokenAcesso { get; set; }

    public string? AppSecret { get; set; }

    public string? TokenVerificacao { get; set; }

    public string VersaoGraph { get; set; } = "v23.0";

    /// <summary>phone_number_id de origem por empresa (o número que o cliente conhece).</summary>
    public Dictionary<Guid, string> Numeros { get; set; } = [];
}

/// <summary>
/// Gateway real (plano técnico, seção 4.4): único componente com a credencial de envio. Só envia texto simples, sem
/// prévia de link, sem botões, listas ou templates (invariantes 1 e 24). Classificação conservadora do resultado: na
/// dúvida, Incerta (nunca repetida), para não enviar duas vezes.
/// </summary>
public sealed class GatewayWhatsAppCloud(HttpClient http, OpcoesWhatsApp opcoes, Func<Guid, string, CancellationToken, Task<bool>> numeroDaEmpresa) : IGatewayWhatsApp
{
    public async Task<ResultadoEnvio> EnviarTextoAsync(Guid tenantId, string waIdDestino, string texto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(opcoes);
        if (!opcoes.EnvioHabilitado || string.IsNullOrEmpty(opcoes.TokenAcesso) || !opcoes.Numeros.TryGetValue(tenantId, out var numero))
        {
            // Definitiva: a mensagem é cancelada e a supervisão é acionada; nada sai sem configuração completa.
            return new ResultadoEnvio(SituacaoEnvio.FalhaDefinitiva, null, "envio desabilitado ou número da empresa não configurado");
        }

        // O número configurado precisa ser desta empresa no mesmo cadastro que resolve a entrada (revisão da F4, S3).
        ArgumentNullException.ThrowIfNull(numeroDaEmpresa);
        if (!await numeroDaEmpresa(tenantId, numero, ct).ConfigureAwait(false))
        {
            return new ResultadoEnvio(SituacaoEnvio.FalhaDefinitiva, null, "número configurado não pertence à empresa");
        }

        using var pedido = new HttpRequestMessage(HttpMethod.Post, new Uri("https://graph.facebook.com/" + opcoes.VersaoGraph + "/" + Uri.EscapeDataString(numero) + "/messages"))
        {
            Content = JsonContent.Create(new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = waIdDestino,
                type = "text",
                text = new { preview_url = false, body = texto },
            }),
        };
        pedido.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opcoes.TokenAcesso);

        HttpResponseMessage resposta;
        try
        {
            resposta = await http.SendAsync(pedido, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.NetworkUnreachable })
        {
            return new ResultadoEnvio(SituacaoEnvio.NaoEnviada, null, "sem conexão com a Meta");
        }
        catch (HttpRequestException)
        {
            return new ResultadoEnvio(SituacaoEnvio.Incerta, null, "falha de rede durante o envio");
        }

        using (resposta)
        {
            var codigo = (int)resposta.StatusCode;
            if (resposta.IsSuccessStatusCode)
            {
                return await WamidAsync(resposta, ct).ConfigureAwait(false) is { } wamid
                    ? new ResultadoEnvio(SituacaoEnvio.Enviada, wamid, null)
                    : new ResultadoEnvio(SituacaoEnvio.Incerta, null, "resposta sem identificador da mensagem");
            }

            // Limite de taxa: a Meta recusou antes de enviar.
            if (resposta.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new ResultadoEnvio(SituacaoEnvio.NaoEnviada, null, "limite de taxa da Meta");
            }

            // 4xx: pedido recusado (fora da janela, destinatário inválido, credencial). Não adianta repetir.
            if (codigo is >= 400 and < 500)
            {
                return new ResultadoEnvio(SituacaoEnvio.FalhaDefinitiva, null, "recusado pela Meta (HTTP " + codigo + ")");
            }

            // 5xx: não se sabe se saiu.
            return new ResultadoEnvio(SituacaoEnvio.Incerta, null, "erro da Meta (HTTP " + codigo + ")");
        }
    }

    private static async Task<string?> WamidAsync(HttpResponseMessage resposta, CancellationToken ct)
    {
        try
        {
            await using var corpo = await resposta.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(corpo, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array && m.GetArrayLength() > 0
                   && m[0].TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
