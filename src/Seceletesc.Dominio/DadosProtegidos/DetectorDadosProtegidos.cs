using System.Text;
using System.Text.RegularExpressions;
using Seceletesc.Dominio.Comum;

namespace Seceletesc.Dominio.DadosProtegidos;

public enum CategoriaDadoProtegido
{
    Cpf,
    Cnpj,
    Rg,
    Cartao,
    Email,
    Cep,
}

/// <summary>Dado protegido encontrado no texto original: posição, categoria e valor.</summary>
public sealed record OcorrenciaDadoProtegido(CategoriaDadoProtegido Categoria, int Inicio, int Tamanho, string Valor);

/// <summary>Texto sem os dados protegidos (com marcadores) e os valores originais retirados.</summary>
public sealed record TextoMascarado(string Texto, IReadOnlyList<OcorrenciaDadoProtegido> Ocorrencias);

/// <summary>
/// Detecta dados pessoais sensíveis na mensagem do cliente (invariante 23; plano técnico, seção 4.2).
/// O valor original é criptografado pela infraestrutura; o que segue no pipeline e vai ao modelo
/// tem só o marcador <c>[DADO PROTEGIDO:CATEGORIA]</c>.
/// Limite conhecido: o que não casar com um padrão segue como texto. Endereço é detectado só pelo CEP.
/// </summary>
public static partial class DetectorDadosProtegidos
{
    public static TextoMascarado Mascarar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        // Trabalha sobre o texto em NFKC, sem invisíveis, para que dígitos de largura total ou
        // caracteres de largura zero não escondam um CPF do detector.
        var limpo = InvisiveisRegex().Replace(texto.Normalize(NormalizationForm.FormKC), string.Empty);

        var ocorrencias = new List<OcorrenciaDadoProtegido>();
        Coletar(ocorrencias, limpo, EmailRegex(), CategoriaDadoProtegido.Email, _ => true);
        Coletar(ocorrencias, limpo, CartaoRegex(), CategoriaDadoProtegido.Cartao, v => LuhnValido(Normalizacao.SomenteDigitos(v)));
        Coletar(ocorrencias, limpo, CnpjFormatadoRegex(), CategoriaDadoProtegido.Cnpj, _ => true);
        Coletar(ocorrencias, limpo, CpfFormatadoRegex(), CategoriaDadoProtegido.Cpf, _ => true);
        Coletar(ocorrencias, limpo, RgFormatadoRegex(), CategoriaDadoProtegido.Rg, _ => true);
        Coletar(ocorrencias, limpo, CepRegex(), CategoriaDadoProtegido.Cep, _ => true);
        // Sem formatação: só com dígitos verificadores válidos (11 dígitos também podem ser telefone).
        Coletar(ocorrencias, limpo, Digitos14Regex(), CategoriaDadoProtegido.Cnpj, v => CnpjValido(v));
        Coletar(ocorrencias, limpo, Digitos11Regex(), CategoriaDadoProtegido.Cpf, v => CpfValido(v));

        var ordenadas = ocorrencias.OrderBy(o => o.Inicio).ToList();
        var sb = new StringBuilder(limpo.Length);
        var cursor = 0;
        foreach (var o in ordenadas)
        {
            sb.Append(limpo, cursor, o.Inicio - cursor);
            sb.Append("[DADO PROTEGIDO:").Append(o.Categoria.ToString().ToUpperInvariant()).Append(']');
            cursor = o.Inicio + o.Tamanho;
        }

        sb.Append(limpo, cursor, limpo.Length - cursor);
        return new TextoMascarado(sb.ToString(), ordenadas);
    }

    private static void Coletar(List<OcorrenciaDadoProtegido> destino, string texto, Regex regex, CategoriaDadoProtegido categoria, Func<string, bool> confirma)
    {
        foreach (Match m in regex.Matches(texto))
        {
            var sobrepoe = destino.Any(o => m.Index < o.Inicio + o.Tamanho && o.Inicio < m.Index + m.Length);
            if (!sobrepoe && confirma(m.Value))
            {
                destino.Add(new OcorrenciaDadoProtegido(categoria, m.Index, m.Length, m.Value));
            }
        }
    }

    internal static bool CpfValido(string valor)
    {
        var d = Normalizacao.SomenteDigitos(valor);
        if (d.Length != 11 || d.Distinct().Count() == 1)
        {
            return false;
        }

        return DigitoVerificador(d, 9, 10) == d[9] - '0' && DigitoVerificador(d, 10, 11) == d[10] - '0';

        static int DigitoVerificador(string d, int quantidade, int pesoInicial)
        {
            var soma = 0;
            for (var i = 0; i < quantidade; i++)
            {
                soma += (d[i] - '0') * (pesoInicial - i);
            }

            var resto = soma * 10 % 11;
            return resto == 10 ? 0 : resto;
        }
    }

    internal static bool CnpjValido(string valor)
    {
        var d = Normalizacao.SomenteDigitos(valor);
        if (d.Length != 14 || d.Distinct().Count() == 1)
        {
            return false;
        }

        int[] pesos1 = [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        int[] pesos2 = [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2];
        return Digito(d, pesos1) == d[12] - '0' && Digito(d, pesos2) == d[13] - '0';

        static int Digito(string d, int[] pesos)
        {
            var soma = 0;
            for (var i = 0; i < pesos.Length; i++)
            {
                soma += (d[i] - '0') * pesos[i];
            }

            var resto = soma % 11;
            return resto < 2 ? 0 : 11 - resto;
        }
    }

    internal static bool LuhnValido(string digitos)
    {
        if (digitos.Length is < 13 or > 19)
        {
            return false;
        }

        var soma = 0;
        var dobrar = false;
        for (var i = digitos.Length - 1; i >= 0; i--)
        {
            var n = digitos[i] - '0';
            if (dobrar)
            {
                n *= 2;
                if (n > 9)
                {
                    n -= 9;
                }
            }

            soma += n;
            dobrar = !dobrar;
        }

        return soma % 10 == 0;
    }

    [GeneratedRegex("[\u00AD\u200B-\u200F\u202A-\u202E\u2060-\u2064\uFEFF]")]
    private static partial Regex InvisiveisRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)")]
    private static partial Regex CartaoRegex();

    [GeneratedRegex(@"(?<!\d)\d{2}\.?\d{3}\.?\d{3}/\d{4}-?\d{2}(?!\d)")]
    private static partial Regex CnpjFormatadoRegex();

    [GeneratedRegex(@"(?<!\d)\d{3}\.\d{3}\.\d{3}-?\d{2}(?!\d)|(?<!\d)\d{9}-\d{2}(?!\d)")]
    private static partial Regex CpfFormatadoRegex();

    [GeneratedRegex(@"(?<!\d)\d{1,2}\.\d{3}\.\d{3}-[\dXx](?!\d)")]
    private static partial Regex RgFormatadoRegex();

    [GeneratedRegex(@"(?<!\d)\d{5}-\d{3}(?!\d)|(?i:\bcep\b)\s*:?\s*\d{8}(?!\d)")]
    private static partial Regex CepRegex();

    [GeneratedRegex(@"(?<!\d)\d{14}(?!\d)")]
    private static partial Regex Digitos14Regex();

    [GeneratedRegex(@"(?<!\d)\d{11}(?!\d)")]
    private static partial Regex Digitos11Regex();
}
