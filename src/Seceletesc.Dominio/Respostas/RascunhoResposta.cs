using System.Text.Json;
using Seceletesc.Dominio.Conhecimento;

namespace Seceletesc.Dominio.Respostas;

/// <summary>Resposta proposta pelo motor de IA. Proposta não é autorização: só o validador decide.</summary>
public sealed record RascunhoResposta(string Texto, IReadOnlyList<ChaveFonte> Fontes, string AssuntoDeclarado);

/// <summary>
/// Lê o rascunho do motor de IA em schema estrito (plano técnico, seção 4.3):
/// <c>{"texto": string, "fontes": [{"item": uuid, "versao": int}], "assunto": string}</c>.
/// Campo extra, campo ausente, tipo errado ou limite excedido = rascunho inválido (bloqueio).
/// </summary>
public static class LeitorRascunho
{
    public const int TamanhoMaximoTexto = 2000;
    public const int MaximoFontes = 10;
    public const int TamanhoMaximoAssunto = 100;
    public const int TamanhoMaximoJson = 16_384;

    private static readonly HashSet<string> CamposRaiz = ["texto", "fontes", "assunto"];
    private static readonly HashSet<string> CamposFonte = ["item", "versao"];

    public static RascunhoResposta? TentarLer(string? json, out string? erro)
    {
        erro = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > TamanhoMaximoJson)
        {
            erro = "rascunho vazio ou grande demais";
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object || !SoCampos(raiz, CamposRaiz, out erro))
            {
                erro ??= "raiz não é objeto";
                return null;
            }

            if (!raiz.TryGetProperty("texto", out var texto) || texto.ValueKind != JsonValueKind.String
                || !raiz.TryGetProperty("assunto", out var assunto) || assunto.ValueKind != JsonValueKind.String
                || !raiz.TryGetProperty("fontes", out var fontes) || fontes.ValueKind != JsonValueKind.Array)
            {
                erro = "campos obrigatórios ausentes ou com tipo errado";
                return null;
            }

            var textoValor = texto.GetString()!;
            var assuntoValor = assunto.GetString()!;
            if (textoValor.Trim().Length == 0 || textoValor.Length > TamanhoMaximoTexto
                || assuntoValor.Trim().Length == 0 || assuntoValor.Length > TamanhoMaximoAssunto
                || fontes.GetArrayLength() > MaximoFontes)
            {
                erro = "limites excedidos ou campos vazios";
                return null;
            }

            var chaves = new List<ChaveFonte>();
            foreach (var fonte in fontes.EnumerateArray())
            {
                if (fonte.ValueKind != JsonValueKind.Object || !SoCampos(fonte, CamposFonte, out erro)
                    || !fonte.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(item.GetString(), out var itemId)
                    || !fonte.TryGetProperty("versao", out var versao) || versao.ValueKind != JsonValueKind.Number
                    || !versao.TryGetInt32(out var versaoValor) || versaoValor < 1)
                {
                    erro ??= "fonte inválida";
                    return null;
                }

                chaves.Add(new ChaveFonte(itemId, versaoValor));
            }

            return new RascunhoResposta(textoValor, chaves, assuntoValor.Trim());
        }
        catch (JsonException)
        {
            erro = "JSON inválido";
            return null;
        }
    }

    private static bool SoCampos(JsonElement objeto, HashSet<string> permitidos, out string? erro)
    {
        var vistos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var propriedade in objeto.EnumerateObject())
        {
            if (!permitidos.Contains(propriedade.Name) || !vistos.Add(propriedade.Name))
            {
                erro = $"campo não permitido ou repetido: {propriedade.Name}";
                return false;
            }
        }

        erro = null;
        return true;
    }
}
