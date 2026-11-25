using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Seceletesc.Infraestrutura.WhatsApp;

/// <summary>Evento do webhook da Cloud API, já separado por tipo (plano técnico, seção 4.8).</summary>
public abstract record EventoWhatsApp(string PhoneNumberId, string Wamid, DateTimeOffset Em);

/// <summary>Mensagem de texto do cliente.</summary>
public sealed record MensagemTextoWhatsApp(string PhoneNumberId, string Wamid, DateTimeOffset Em, string WaIdCliente, string Texto)
    : EventoWhatsApp(PhoneNumberId, Wamid, Em);

/// <summary>Mensagem do cliente que não é texto (mídia, áudio, localização, reação, interativa...). Só o tipo é guardado.</summary>
public sealed record MensagemNaoTextoWhatsApp(string PhoneNumberId, string Wamid, DateTimeOffset Em, string WaIdCliente, string Tipo)
    : EventoWhatsApp(PhoneNumberId, Wamid, Em);

/// <summary>Status de uma mensagem enviada pelo número (sent, delivered, read, failed).</summary>
public sealed record StatusWhatsApp(string PhoneNumberId, string Wamid, DateTimeOffset Em, string Status, string? ErroCodigo)
    : EventoWhatsApp(PhoneNumberId, Wamid, Em);

/// <summary>Eco de mensagem enviada pelo WhatsApp Business App (coexistência): um humano respondeu pelo celular.</summary>
public sealed record EcoAppWhatsApp(string PhoneNumberId, string Wamid, DateTimeOffset Em, string WaIdCliente, string Tipo, string? Texto)
    : EventoWhatsApp(PhoneNumberId, Wamid, Em);

/// <summary>
/// Verificação do cabeçalho X-Hub-Signature-256: HMAC-SHA256 do corpo BRUTO com o App Secret, comparado em tempo
/// constante. Sem segredo configurado, nada é aceito (falha fechada).
/// </summary>
public static class AssinaturaWebhook
{
    private const string Prefixo = "sha256=";

    public static bool Valida(ReadOnlySpan<byte> corpo, string? cabecalho, string? appSecret)
    {
        if (string.IsNullOrEmpty(appSecret) || cabecalho is null || !cabecalho.StartsWith(Prefixo, StringComparison.Ordinal)
            || cabecalho.Length != Prefixo.Length + 64)
        {
            return false;
        }

        byte[] recebida;
        try
        {
            recebida = Convert.FromHexString(cabecalho.AsSpan(Prefixo.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        var esperada = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), corpo);
        return CryptographicOperations.FixedTimeEquals(esperada, recebida);
    }

    /// <summary>Para testes e ferramentas: o cabeçalho que a Meta enviaria para este corpo.</summary>
    public static string Calcular(ReadOnlySpan<byte> corpo, string appSecret) =>
        Prefixo + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), corpo));
}

/// <summary>
/// Lê o JSON do webhook (objeto whatsapp_business_account). Campos ausentes ou inesperados são ignorados: o leitor
/// nunca lança por conteúdo do cliente; só JSON inválido gera <see cref="JsonException"/>.
/// Assinados: "messages" (mensagens e status) e "smb_message_echoes" (coexistência).
/// </summary>
public static class LeitorWebhookWhatsApp
{
    public const int TamanhoMaximoTexto = 4096 * 4;

    /// <summary>Evento com identificador fora do formato é descartado (revisão da F4, S6).</summary>
    private static bool WaIdValido(string? v) => v is { Length: >= 6 and <= 20 } && v.All(char.IsAsciiDigit);

    private static bool WamidValido(string? v) => v is { Length: > 0 and <= 128 } && v.All(ch => ch is > ' ' and < (char)127);

    /// <summary>Texto sem caracteres que o banco recusa (NUL) nem de controle, exceto quebras de linha e tabulação.</summary>
    private static string? Limpo(string? v) => v is null ? null : new string(v.Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray());

    public static IReadOnlyList<EventoWhatsApp> Ler(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var eventos = new List<EventoWhatsApp>();
        if (Texto(doc.RootElement, "object") != "whatsapp_business_account" || !Lista(doc.RootElement, "entry", out var entradas))
        {
            return eventos;
        }

        foreach (var entrada in entradas)
        {
            if (!Lista(entrada, "changes", out var mudancas))
            {
                continue;
            }

            foreach (var mudanca in mudancas)
            {
                if (!mudanca.TryGetProperty("value", out var valor) || valor.ValueKind != JsonValueKind.Object
                    || !valor.TryGetProperty("metadata", out var meta) || Texto(meta, "phone_number_id") is not { } numero)
                {
                    continue;
                }

                var campo = Texto(mudanca, "field");
                if (campo == "messages")
                {
                    LerMensagens(valor, numero, eventos);
                    LerStatus(valor, numero, eventos);
                }
                else if (campo == "smb_message_echoes")
                {
                    LerEcos(valor, numero, eventos);
                }
            }
        }

        return eventos;
    }

    private static void LerMensagens(JsonElement valor, string numero, List<EventoWhatsApp> eventos)
    {
        if (!Lista(valor, "messages", out var mensagens))
        {
            return;
        }

        foreach (var m in mensagens)
        {
            if (Texto(m, "id") is not { } wamid || !WamidValido(wamid) || Texto(m, "from") is not { } de || !WaIdValido(de) || Instante(m) is not { } em)
            {
                continue;
            }

            var tipo = Texto(m, "type") ?? "desconhecido";
            if (tipo == "text" && m.TryGetProperty("text", out var t) && Limpo(Texto(t, "body")) is { Length: > 0 and <= TamanhoMaximoTexto } corpo)
            {
                eventos.Add(new MensagemTextoWhatsApp(numero, wamid, em, de, corpo));
            }
            else
            {
                eventos.Add(new MensagemNaoTextoWhatsApp(numero, wamid, em, de, tipo));
            }
        }
    }

    private static void LerStatus(JsonElement valor, string numero, List<EventoWhatsApp> eventos)
    {
        if (!Lista(valor, "statuses", out var status))
        {
            return;
        }

        foreach (var s in status)
        {
            if (Texto(s, "id") is not { } wamid || !WamidValido(wamid) || Texto(s, "status") is not ("sent" or "delivered" or "read" or "failed" or "deleted") || Instante(s) is not { } em)
            {
                continue;
            }

            string? erro = null;
            if (Lista(s, "errors", out var erros))
            {
                foreach (var e in erros)
                {
                    erro = e.TryGetProperty("code", out var codigo) ? codigo.ToString() : null;
                    break;
                }
            }

            eventos.Add(new StatusWhatsApp(numero, wamid, em, Texto(s, "status")!, erro is { Length: <= 20 } ? erro : null));
        }
    }

    private static void LerEcos(JsonElement valor, string numero, List<EventoWhatsApp> eventos)
    {
        if (!Lista(valor, "message_echoes", out var ecos))
        {
            return;
        }

        foreach (var m in ecos)
        {
            // No eco, "to" é o cliente; "from" é o próprio número da empresa.
            if (Texto(m, "id") is not { } wamid || !WamidValido(wamid) || Texto(m, "to") is not { } para || !WaIdValido(para) || Instante(m) is not { } em)
            {
                continue;
            }

            var tipo = Texto(m, "type") ?? "desconhecido";
            var texto = tipo == "text" && m.TryGetProperty("text", out var t) ? Limpo(Texto(t, "body")) : null;
            eventos.Add(new EcoAppWhatsApp(numero, wamid, em, para, tipo, texto is { Length: <= TamanhoMaximoTexto } ? texto : null));
        }
    }

    private static string? Texto(JsonElement e, string nome) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Lista(JsonElement e, string nome, out JsonElement.ArrayEnumerator itens)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(nome, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            itens = v.EnumerateArray();
            return true;
        }

        itens = default;
        return false;
    }

    private static DateTimeOffset? Instante(JsonElement e) =>
        long.TryParse(Texto(e, "timestamp"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is > 0 and < 32503680000
            ? DateTimeOffset.FromUnixTimeSeconds(s)
            : null;
}
