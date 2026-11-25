using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Seceletesc.Dominio.Comum;

/// <summary>
/// Normalização de texto usada pelas regras determinísticas: NFKC (homóglifos de compatibilidade, como
/// letras de largura total), remoção de TODO caractere de formatação Unicode (categoria Cf: largura zero,
/// marcas e isolamentos de direção, caracteres de tag), dígitos de qualquer alfabeto convertidos para ASCII,
/// travessões unificados, acentos removidos, minúsculas e espaços colapsados.
/// Homóglifos de outros alfabetos (ex.: cirílico) não são "traduzidos" aqui: o validador recusa
/// caracteres fora do conjunto permitido (ver <see cref="CaracteresPermitidos"/>).
/// </summary>
public static partial class Normalizacao
{
    // Falha fechada: se o ambiente não normaliza Unicode (ex.: InvariantGlobalization sem dados de
    // normalização), as regras do validador ficariam cegas a acentos e homóglifos. Melhor não rodar.
    private static readonly bool AmbienteNormaliza = VerificarAmbiente();

    public static string ParaComparacao(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        GarantirAmbiente();

        var limpo = Limpar(texto.Normalize(NormalizationForm.FormKC));
        var decomposto = limpo.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return Espacos().Replace(sb.ToString().Normalize(NormalizationForm.FormC), " ").Trim();
    }

    /// <summary>
    /// NFKC + remoção de caracteres de formatação + dígitos em ASCII + travessões como hífen,
    /// preservando maiúsculas e acentos. Usado pelo detector de dados protegidos.
    /// </summary>
    public static string LimparPreservandoTexto(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        GarantirAmbiente();
        return Limpar(texto.Normalize(NormalizationForm.FormKC));
    }

    /// <summary>Sequências numéricas do texto, só com dígitos e sem zeros à esquerda ("05" e "5" são iguais).</summary>
    public static IReadOnlySet<string> Numeros(string textoNormalizado)
    {
        ArgumentNullException.ThrowIfNull(textoNormalizado);
        var numeros = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Digitos().Matches(textoNormalizado))
        {
            numeros.Add(SemZerosAEsquerda(m.Value));
        }

        return numeros;
    }

    public static string SomenteDigitos(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto)
        {
            if (c is >= '0' and <= '9')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// O texto (já em NFKC) usa só caracteres esperados numa resposta em português: ASCII imprimível,
    /// letras latinas acentuadas, pontuação tipográfica comum e emoji. Qualquer outro caractere
    /// (cirílico, grego, formatação invisível, separadores de linha Unicode) torna o texto suspeito.
    /// </summary>
    public static bool CaracteresPermitidos(string texto, out string? suspeito)
    {
        ArgumentNullException.ThrowIfNull(texto);
        foreach (var rune in texto.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (!RunePermitida(rune))
            {
                suspeito = $"U+{rune.Value:X4}";
                return false;
            }
        }

        suspeito = null;
        return true;
    }

    private static bool RunePermitida(Rune r)
    {
        var v = r.Value;
        if (v is '\n' or '\r' or '\t' || v is >= 0x20 and <= 0x7E)
        {
            return true;
        }

        // Letras latinas acentuadas do Latin-1 (cobre o português), sem × e ÷. Latin Extended-A fica de fora:
        // traz sósias como "ı" (i sem pingo), que escapariam das comparações.
        if ((v is >= 0xC0 and <= 0xFF) && v is not 0xD7 and not 0xF7)
        {
            return true;
        }

        // Pontuação tipográfica comum: aspas, travessões, reticências, º ª °, « ».
        if (v is 0x2013 or 0x2014 or 0x2018 or 0x2019 or 0x201C or 0x201D or 0x2026 or 0xAA or 0xBA or 0xB0 or 0xAB or 0xBB or 0xA0)
        {
            return true;
        }

        // Emoji e seus seletores/junções (só formam figuras; a normalização remove junções nas comparações).
        if (v is 0xFE0F or 0x200D || (v >= 0x1F000 && Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol)
            || (v is >= 0x2600 and <= 0x27BF && Rune.GetUnicodeCategory(r) == UnicodeCategory.OtherSymbol))
        {
            return true;
        }

        return false;
    }

    private static string Limpar(string nfkc)
    {
        var sb = new StringBuilder(nfkc.Length);
        foreach (var c in nfkc)
        {
            var categoria = CharUnicodeInfo.GetUnicodeCategory(c);
            if (categoria == UnicodeCategory.Format)
            {
                continue; // largura zero, direção, tags, hífen suave etc.
            }

            if (categoria == UnicodeCategory.Surrogate)
            {
                sb.Append(c); // pares substitutos (emoji, tags) tratados como unidade abaixo
                continue;
            }

            var digito = CharUnicodeInfo.GetDecimalDigitValue(c);
            if (digito >= 0)
            {
                sb.Append((char)('0' + digito));
            }
            else if (c is >= (char)0x2010 and <= (char)0x2015 or (char)0x2212)
            {
                sb.Append('-');
            }
            else
            {
                sb.Append(c);
            }
        }

        // Caracteres de tag (U+E0000–U+E007F) e outros Cf fora do BMP chegam como pares substitutos.
        return RemoverFormatoForaDoBmp(sb.ToString());
    }

    private static string RemoverFormatoForaDoBmp(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var rune in texto.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format)
            {
                sb.Append(rune.ToString());
            }
        }

        return sb.ToString();
    }

    private static void GarantirAmbiente()
    {
        if (!AmbienteNormaliza)
        {
            throw new PlatformNotSupportedException(
                "A normalização Unicode não está disponível neste ambiente. As regras de envio não podem ser avaliadas com segurança.");
        }
    }

    private static bool VerificarAmbiente()
    {
        try
        {
            // "s" de largura total vira "s"; "ã" decompõe em "a" + til.
            return ((char)0xFF53).ToString().Normalize(NormalizationForm.FormKC) == "s"
                && ((char)0x00E3).ToString().Normalize(NormalizationForm.FormD).Length == 2;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static string SemZerosAEsquerda(string digitos)
    {
        var semZeros = digitos.TrimStart('0');
        return semZeros.Length == 0 ? "0" : semZeros;
    }

    [GeneratedRegex(@"\s+", RegexOptions.None, 1000)]
    private static partial Regex Espacos();

    [GeneratedRegex(@"[0-9]+", RegexOptions.None, 1000)]
    private static partial Regex Digitos();
}
