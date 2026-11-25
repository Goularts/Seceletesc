using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Seceletesc.Dominio.Comum;

/// <summary>
/// Normalização de texto usada pelas regras determinísticas. Aplica NFKC (homóglifos de compatibilidade,
/// como letras de largura total), remove caracteres invisíveis, remove acentos, converte para minúsculas
/// e colapsa espaços. Duas strings "iguais para um leitor" ficam iguais para a regra.
/// </summary>
public static partial class Normalizacao
{
    // Falha fechada: se o ambiente não normaliza Unicode (ex.: InvariantGlobalization sem dados de
    // normalização), as regras do validador ficariam cegas a acentos e homóglifos. Melhor não rodar.
    private static readonly bool AmbienteNormaliza = VerificarAmbiente();

    public static string ParaComparacao(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);
        if (!AmbienteNormaliza)
        {
            throw new PlatformNotSupportedException(
                "A normalização Unicode não está disponível neste ambiente. As regras de envio não podem ser avaliadas com segurança.");
        }

        var nfkc = texto.Normalize(NormalizationForm.FormKC);
        var semInvisiveis = Invisiveis().Replace(nfkc, string.Empty);

        var decomposto = semInvisiveis.Normalize(NormalizationForm.FormD);
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

    private static bool VerificarAmbiente()
    {
        try
        {
            // "s" de largura total vira "s"; "ã" decompõe em "a" + til.
            return "ｓ".Normalize(NormalizationForm.FormKC) == "s"
                && "ã".Normalize(NormalizationForm.FormD).Length == 2;
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

    // Zero-width space/joiner/non-joiner, word joiner, BOM, soft hyphen, marcas de direção.
    [GeneratedRegex("[­​-‏‪-‮⁠-⁤﻿]")]
    private static partial Regex Invisiveis();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacos();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digitos();
}
