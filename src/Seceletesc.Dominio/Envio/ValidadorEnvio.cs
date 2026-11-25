using System.Text.RegularExpressions;
using Seceletesc.Dominio.Comum;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Conversas;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Dominio.Respostas;

namespace Seceletesc.Dominio.Envio;

public enum MotivoBloqueio
{
    RascunhoInvalido,
    CaracterNaoPermitido,
    EstadoMudou,
    AssuntoNaoAutorizado,
    AssuntoExigeHumano,
    SemFonte,
    FonteNaoEntregue,
    FonteInelegivel,
    AssuntoDivergeDaFonte,
    TelefoneNaoAutorizado,
    ValorSemFonte,
    CompromissoSemFonte,
    AutodeclaracaoHumana,
    MenuOuOpcoes,
    UrlNaoPermitida,
    AvisoNaoConfigurado,
    TempoEsgotado,
    DadoProtegidoNaResposta,
}

public sealed record ResultadoValidacao(bool Aprovado, MotivoBloqueio? Motivo, string? Detalhe)
{
    public static ResultadoValidacao Aprovar() => new(true, null, null);

    public static ResultadoValidacao Bloquear(MotivoBloqueio motivo, string? detalhe = null) => new(false, motivo, detalhe);
}

/// <summary>Tudo que o validador precisa, lido do banco no momento da validação (nunca de cache).</summary>
public sealed record ContextoValidacao(
    Conversa ConversaAtual,
    long VersaoLidaNoInicio,
    PoliticaTenant Politica,
    IReadOnlySet<ChaveFonte> EntreguesAoModeloNestaRodada,
    IReadOnlyDictionary<ChaveFonte, FonteConhecimento> FontesCitadasNoBanco,
    DateTimeOffset Agora);

/// <summary>
/// Validador determinístico de envio (invariantes 6 a 9 e 11 a 13; plano técnico, seção 4.3).
/// Para na primeira regra que falhar e falha fechado (inclusive em tempo esgotado de expressão regular).
/// Resposta gerada não é resposta autorizada.
/// Limite assumido: afirmações sem número, sem termo de compromisso e sem padrão proibido não são
/// verificáveis por regra; esse risco residual é coberto pelos testes de nível Avaliação.
/// </summary>
public static partial class ValidadorEnvio
{
    private const int TimeoutMs = 1000;

    private static readonly string[] NumerosPorExtenso =
    [
        "dois", "duas", "tres", "quatro", "cinco", "seis", "sete", "oito", "nove", "dez", "onze", "doze", "treze",
        "quatorze", "catorze", "quinze", "dezesseis", "dezessete", "dezoito", "dezenove", "vinte", "trinta",
        "quarenta", "cinquenta", "sessenta", "setenta", "oitenta", "noventa", "cem", "cento", "duzentos",
        "trezentos", "quatrocentos", "quinhentos", "seiscentos", "setecentos", "oitocentos", "novecentos", "mil",
        "milhao", "milhoes", "bilhao", "metade", "dobro", "triplo", "por cento", "meia hora",
    ];

    public static ResultadoValidacao Validar(RascunhoResposta? rascunho, ContextoValidacao ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        try
        {
            return ValidarInterno(rascunho, ctx);
        }
        catch (RegexMatchTimeoutException)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.TempoEsgotado);
        }
    }

    private static ResultadoValidacao ValidarInterno(RascunhoResposta? rascunho, ContextoValidacao ctx)
    {
        if (rascunho is null)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.RascunhoInvalido);
        }

        // 0. Só caracteres esperados numa resposta em português (fecha homóglifos de outros alfabetos).
        if (!Normalizacao.CaracteresPermitidos(rascunho.Texto, out var suspeito)
            || !Normalizacao.CaracteresPermitidos(rascunho.AssuntoDeclarado, out suspeito))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.CaracterNaoPermitido, suspeito);
        }

        // 0b. Nenhum dado protegido (CPF, cartão, e-mail...) sai numa resposta automática (invariantes 11 e 23).
        if (DadosProtegidos.DetectorDadosProtegidos.Mascarar(rascunho.Texto).Ocorrencias.Count > 0)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.DadoProtegidoNaResposta);
        }

        // 1. Estado e versão: um humano pode ter assumido durante a geração.
        if (ctx.ConversaAtual.Estado != EstadoConversa.Automatico || ctx.ConversaAtual.VersaoEstado != ctx.VersaoLidaNoInicio)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.EstadoMudou);
        }

        // 2. Assunto: lista fechada definida por humanos; "exige humano" prevalece.
        var assunto = Normalizacao.ParaComparacao(rascunho.AssuntoDeclarado);
        if (ctx.Politica.AssuntosExigemHumano.Any(a => Normalizacao.ParaComparacao(a) == assunto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.AssuntoExigeHumano, rascunho.AssuntoDeclarado);
        }

        if (!ctx.Politica.AssuntosAutonomos.Any(a => Normalizacao.ParaComparacao(a) == assunto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.AssuntoNaoAutorizado, rascunho.AssuntoDeclarado);
        }

        // 3. Fontes: ao menos uma; entregue nesta rodada; elegível agora; do mesmo assunto declarado.
        if (rascunho.Fontes.Count == 0)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.SemFonte);
        }

        var fontes = new List<FonteConhecimento>();
        foreach (var chave in rascunho.Fontes)
        {
            if (!ctx.EntreguesAoModeloNestaRodada.Contains(chave))
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.FonteNaoEntregue, chave.ToString());
            }

            if (!ctx.FontesCitadasNoBanco.TryGetValue(chave, out var fonte)
                || ElegibilidadeConhecimento.Avaliar(fonte, ctx.Politica, ctx.ConversaAtual, ctx.Agora) is not null)
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.FonteInelegivel, chave.ToString());
            }

            if (Normalizacao.ParaComparacao(fonte.Assunto) != assunto)
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.AssuntoDivergeDaFonte, chave.ToString());
            }

            fontes.Add(fonte);
        }

        var texto = Normalizacao.ParaComparacao(rascunho.Texto);
        var conteudoFontes = string.Join("\n", fontes.Select(f => Normalizacao.ParaComparacao(f.Conteudo)));

        // 5. Padrões proibidos.
        if (AutodeclaracaoHumana().IsMatch(texto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.AutodeclaracaoHumana);
        }

        if (EhMenu(rascunho.Texto, texto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.MenuOuOpcoes);
        }

        if (UrlOfuscada().IsMatch(texto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.UrlNaoPermitida, "url ofuscada");
        }

        foreach (Match url in Host().Matches(texto))
        {
            if (!DominioPermitido(url.Groups["host"].Value, ctx.Politica.DominiosPermitidos))
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.UrlNaoPermitida, url.Groups["host"].Value);
            }
        }

        // 6. Telefones: o modelo nunca recebe telefones, então nenhum telefone pode sair numa resposta automática.
        if (Telefone().Matches(texto).Any(m => Normalizacao.SomenteDigitos(m.Value).Length is >= 10 and <= 13))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.TelefoneNaoAutorizado);
        }

        // 4. Valores: todo número (com sua unidade) e todo número por extenso constam numa fonte citada.
        var valoresFontes = Valores(conteudoFontes);
        var valorSemFonte = Valores(texto).FirstOrDefault(v => !valoresFontes.Contains(v));
        if (valorSemFonte is not null)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.ValorSemFonte, valorSemFonte);
        }

        var extensoSemFonte = NumerosPorExtenso.FirstOrDefault(n => ContemTermo(texto, n) && !ContemTermo(conteudoFontes, n));
        if (extensoSemFonte is not null)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.ValorSemFonte, extensoSemFonte);
        }

        foreach (var termo in ctx.Politica.TermosCompromisso)
        {
            var t = Normalizacao.ParaComparacao(termo);
            if (t.Length > 0 && ContemTermo(texto, t) && !ContemTermo(conteudoFontes, t))
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.CompromissoSemFonte, termo);
            }
        }

        return ResultadoValidacao.Aprovar();
    }

    /// <summary>
    /// Caminho próprio do aviso de transferência: texto fixo da política, só em TransferenciaPendente
    /// com a versão esperada. Se o humano já assumiu, não sai.
    /// </summary>
    public static ResultadoValidacao ValidarAvisoTransferencia(Conversa conversa, long versaoEsperada, PoliticaTenant politica)
    {
        ArgumentNullException.ThrowIfNull(conversa);
        ArgumentNullException.ThrowIfNull(politica);

        if (string.IsNullOrWhiteSpace(politica.TextoAvisoTransferencia))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.AvisoNaoConfigurado);
        }

        if (conversa.Estado != EstadoConversa.TransferenciaPendente || conversa.VersaoEstado != versaoEsperada)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.EstadoMudou);
        }

        return ResultadoValidacao.Aprovar();
    }

    /// <summary>
    /// Valores numéricos com a unidade que os acompanha ("18|h", "18|vezes", "150,00|reais").
    /// "18h" e "18 vezes" são valores diferentes; "150,00" e "150.000,00" também.
    /// </summary>
    private static HashSet<string> Valores(string textoNormalizado)
    {
        var valores = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Valor().Matches(textoNormalizado))
        {
            var numero = m.Groups["num"].Value.TrimStart('0');
            valores.Add((numero.Length == 0 || !char.IsDigit(numero[0]) ? "0" + numero : numero) + "|" + m.Groups["unid"].Value.Trim());
        }

        return valores;
    }

    private static bool EhMenu(string original, string normalizado)
    {
        var linhasDeItem = original.Split('\n').Count(l => ItemDeLista().IsMatch(Normalizacao.ParaComparacao(l)));
        return linhasDeItem >= 2 || InstrucaoDeOpcao().IsMatch(normalizado);
    }

    private static bool DominioPermitido(string host, IReadOnlySet<string> permitidos) =>
        permitidos.Any(p =>
        {
            var d = Normalizacao.ParaComparacao(p).TrimStart('.');
            return d.Length > 0 && (host == d || host.EndsWith("." + d, StringComparison.Ordinal));
        });

    private static bool ContemTermo(string texto, string termo) =>
        Regex.IsMatch(texto, $@"(?<![\p{{L}}\d]){Regex.Escape(termo)}(?![\p{{L}}\d])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(TimeoutMs));

    [GeneratedRegex(@"\b(sou|eu sou) (um |uma )?(humano|humana|pessoa|pessoa real|pessoa de verdade|atendente humano|atendente humana|atendente de verdade|ser humano|gente de verdade)\b|\bnao sou (um |uma )?(robo\w*|bot\w*|chatbot|ia|inteligencia artificial|assistente virtual|maquina|programa|automatico|automatica)\b|\b(voce esta|esta) falando com (um |uma )?(humano|humana|pessoa|pessoa real|ser humano|atendente de verdade|gente de verdade)\b|\bde carne e osso\b|\bmeu nome e\b|\bme chamo\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex AutodeclaracaoHumana();

    [GeneratedRegex(@"^\s*(?:(?:\d{1,2}|[a-z])\s*[\).:\-]|[\-\*\+>])\s*\S", RegexOptions.None, TimeoutMs)]
    private static partial Regex ItemDeLista();

    [GeneratedRegex(@"\b(digite|responda|escolha|selecione|tecle|envie|marque)\s+(o\s+|a\s+)?(numero\s+|opcao\s+|letra\s+)?(\d|[a-z])\b(?!\w)|\bopcao\s+(\d|[a-z])\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex InstrucaoDeOpcao();

    [GeneratedRegex(@"\[\s*\.\s*\]|\(\s*\.\s*\)|\bponto\s+(com|br|net|org|io|app)\b|\barroba\b", RegexOptions.None, TimeoutMs)]
    private static partial Regex UrlOfuscada();

    [GeneratedRegex(@"(?<host>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,24})(?![a-z0-9-])", RegexOptions.None, TimeoutMs)]
    private static partial Regex Host();

    [GeneratedRegex(@"\+?[0-9][0-9\s().\-]{7,20}[0-9]", RegexOptions.None, TimeoutMs)]
    private static partial Regex Telefone();

    // Unidade = letras coladas ao número ("18h", "10x") ou, após um espaço, uma unidade conhecida.
    [GeneratedRegex(@"(?<num>[0-9]+(?:[.,:][0-9]+)*)(?<unid>%|[a-z]+|\s(?:%|reais|real|vezes|parcelas?|horas?|hrs?|h|minutos?|min|dias?|meses|mes|anos?|semanas?|km|kg|g|ml|l|m|cm|mm|unidades?|un|pessoas?|x)\b)?", RegexOptions.None, TimeoutMs)]
    private static partial Regex Valor();
}
