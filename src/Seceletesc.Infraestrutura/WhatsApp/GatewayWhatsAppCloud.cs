using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Seceletesc.Aplicacao.Envio;

namespace Seceletesc.Infraestrutura.WhatsApp;

/// <summary>Configuração da integração com a Cloud API. Nada aqui vai para o Git (user-secrets ou variáveis de ambiente).</summary>
public sealed class OpcoesWhatsApp
{
    /// <summary>
    /// Interruptor geral, desligado por padrão: sem autorização do Victor, nada sai para a Meta (decisões 31 e 34). Mesmo
    /// ligado, cada número precisa da autorização operacional e do envio habilitado por um operador (decisão 34c).
    /// </summary>
    public bool EnvioHabilitado { get; set; }

    /// <summary>Liga o trabalhador da fila da IA. Até a F5, o motor é indisponível: tudo vai para supervisão.</summary>
    public bool ProcessamentoHabilitado { get; set; }

    /// <summary>App Secret do app da plataforma (um só para todos os clientes): confere a assinatura dos webhooks.</summary>
    public string? AppSecret { get; set; }

    public string? TokenVerificacao { get; set; }

    /// <summary>
    /// Chave (base64, 32 bytes) que cifra as credenciais de cada empresa no banco. Sem ela, nenhuma credencial é lida e o
    /// envio não é registrado. As credenciais (business tokens) ficam no banco, cifradas, uma por WABA.
    /// </summary>
    public string? ChaveCredenciais { get; set; }

    /// <summary>Versão da Graph API. Os exemplos atuais da documentação usam a v27.0; reconfirmar antes da M3.</summary>
    public string VersaoGraph { get; set; } = "v27.0";
}

/// <summary>
/// Gateway real (plano técnico, seções 4.4 e 4.13): único componente que chama a Meta para enviar. Só envia texto
/// simples, sem prévia de link, sem botões, listas ou templates (invariantes 1 e 24). Sai pelo número em que o cliente
/// escreveu, com a credencial da empresa dona desse número, e só se o banco confirmar as três etapas do número.
/// Classificação conservadora do resultado: na dúvida, Incerta (nunca repetida), para não enviar duas vezes.
/// </summary>
public sealed class GatewayWhatsAppCloud(HttpClient http, OpcoesWhatsApp opcoes, Func<Guid, string, CancellationToken, Task<CredencialEnvio?>> credencialDoNumero) : IGatewayWhatsApp
{
    public async Task<ResultadoEnvio> EnviarTextoAsync(Guid tenantId, string? numeroOrigem, string waIdDestino, string texto, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(opcoes);
        ArgumentNullException.ThrowIfNull(credencialDoNumero);
        if (!opcoes.EnvioHabilitado || string.IsNullOrEmpty(numeroOrigem))
        {
            // Definitiva: a mensagem é cancelada e a supervisão é acionada; nada sai sem configuração completa.
            return new ResultadoEnvio(SituacaoEnvio.FalhaDefinitiva, null, "envio desabilitado ou conversa sem número de origem");
        }

        // A credencial vem do cadastro da empresa dona do número (revisão da F4, S3; M1): número de outra empresa, sem
        // autorização operacional, com envio desligado, conta desconectada ou credencial revogada não envia.
        CredencialEnvio? credencial;
        try
        {
            credencial = await credencialDoNumero(tenantId, numeroOrigem, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Falha antes de qualquer chamada à Meta: com certeza não saiu (não é incerta).
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ResultadoEnvio(SituacaoEnvio.NaoEnviada, null, "falha ao ler a credencial do número");
        }

        if (credencial is null)
        {
            return new ResultadoEnvio(SituacaoEnvio.FalhaDefinitiva, null, "número sem envio autorizado nesta empresa ou sem credencial vigente");
        }

        var numero = numeroOrigem;
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
        pedido.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credencial.Token);

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
