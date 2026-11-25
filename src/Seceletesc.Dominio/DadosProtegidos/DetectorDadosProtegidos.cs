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
    ChavePix,

    /// <summary>Sequência longa de dígitos não identificada (ex.: cartão SUS, protocolo com documento).</summary>
    Documento,
}

/// <summary>Dado protegido encontrado no texto limpo: posição, categoria e valor original.</summary>
public sealed record OcorrenciaDadoProtegido(CategoriaDadoProtegido Categoria, int Inicio, int Tamanho, string Valor)
{
    /// <summary>Nunca imprime o valor: um log acidental não pode vazar o dado.</summary>
    public override string ToString() => $"OcorrenciaDadoProtegido {{ Categoria = {Categoria}, Inicio = {Inicio}, Tamanho = {Tamanho}, Valor = *** }}";
}

/// <summary>Texto sem os dados protegidos (com marcadores) e os valores originais retirados.</summary>
public sealed record TextoMascarado(string Texto, IReadOnlyList<OcorrenciaDadoProtegido> Ocorrencias)
{
    public override string ToString() => $"TextoMascarado {{ Texto = {Texto}, Ocorrencias = {Ocorrencias.Count} }}";
}

/// <summary>
/// Detecta dados pessoais sensíveis na mensagem do cliente (invariante 23; plano técnico, seção 4.2).
/// O valor original é criptografado pela infraestrutura; o que segue no pipeline e vai ao modelo
/// tem só o marcador <c>[DADO PROTEGIDO:CATEGORIA]</c>.
/// Limites conhecidos: o que não casar com um padrão segue como texto. Endereço só pelo CEP; CNH e PIS
/// sem formatação só se coincidirem com CPF válido; 11 dígitos inválidos como CPF são tratados como telefone.
/// </summary>
public static partial class DetectorDadosProtegidos
{
    private const int TimeoutMs = 1000;

    public static TextoMascarado Mascarar(string texto)
    {
        ArgumentNullException.ThrowIfNull(texto);

        // NFKC, sem caracteres de formatação, dígitos de qualquer alfabeto em ASCII e travessões como hífen:
        // truques de Unicode não escondem o dado.
        var limpo = Normalizacao.LimparPreservandoTexto(texto);

        var ocorrencias = new List<OcorrenciaDadoProtegido>();
        Coletar(ocorrencias, limpo, EmailRegex(), CategoriaDadoProtegido.Email, _ => true);
        Coletar(ocorrencias, limpo, EmailPorExtensoRegex(), CategoriaDadoProtegido.Email, _ => true);
        Coletar(ocorrencias, limpo, UuidRegex(), CategoriaDadoProtegido.ChavePix, _ => true);
        Coletar(ocorrencias, limpo, CartaoRegex(), CategoriaDadoProtegido.Cartao, v => LuhnValido(Normalizacao.SomenteDigitos(v)));
        Coletar(ocorrencias, limpo, CnpjRegex(), CategoriaDadoProtegido.Cnpj, v => Formatado(v, '/') || CnpjValido(v));
        Coletar(ocorrencias, limpo, CpfRegex(), CategoriaDadoProtegido.Cpf, v => (v.Contains('.', StringComparison.Ordinal) && v.Contains('-', StringComparison.Ordinal)) || CpfValido(v));
        Coletar(ocorrencias, limpo, RgRegex(), CategoriaDadoProtegido.Rg, _ => true);
        Coletar(ocorrencias, limpo, CepRegex(), CategoriaDadoProtegido.Cep, _ => true);
        Coletar(ocorrencias, limpo, DigitosLongosRegex(), CategoriaDadoProtegido.Documento, v => Normalizacao.SomenteDigitos(v).Length >= 14);

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

    private static bool Formatado(string valor, char separador) => valor.Contains(separador, StringComparison.Ordinal);

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

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.None, TimeoutMs)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"[A-Za-z0-9._\-]+\s+(?i:arroba)\s+[A-Za-z0-9\-]+(?:\s+(?i:ponto)\s+[A-Za-z]{2,})+", RegexOptions.None, TimeoutMs)]
    private static partial Regex EmailPorExtensoRegex();

    [GeneratedRegex(@"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.None, TimeoutMs)]
    private static partial Regex UuidRegex();

    [GeneratedRegex(@"(?<![0-9])(?:[0-9][ .\-]?){12,18}[0-9](?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex CartaoRegex();

    [GeneratedRegex(@"(?<![0-9])[0-9]{2}[.\s]?[0-9]{3}[.\s]?[0-9]{3}[/\s]?[0-9]{4}[\-\s]?[0-9]{2}(?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex CnpjRegex();

    [GeneratedRegex(@"(?<![0-9])[0-9]{3}[.\s]?[0-9]{3}[.\s]?[0-9]{3}[\-.\s]?[0-9]{2}(?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex CpfRegex();

    [GeneratedRegex(@"(?<![0-9])[0-9]{1,2}\.?[0-9]{3}\.?[0-9]{3}-[0-9Xx](?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex RgRegex();

    [GeneratedRegex(@"(?<![0-9])[0-9]{2}\.?[0-9]{3}-[0-9]{3}(?![0-9])|(?i:\bcep\b)\s*:?\s*[0-9]{5}-?[0-9]{3}(?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex CepRegex();

    [GeneratedRegex(@"(?<![0-9])(?:[0-9][ .\-/]?){13,29}[0-9](?![0-9])", RegexOptions.None, TimeoutMs)]
    private static partial Regex DigitosLongosRegex();
}
