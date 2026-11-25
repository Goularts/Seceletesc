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
    string TelefoneClienteAtual,
    DateTimeOffset Agora);

/// <summary>
/// Validador determinístico de envio (invariantes 6 a 9 e 11 a 13; plano técnico, seção 4.3).
/// Para na primeira regra que falhar. Resposta gerada não é resposta autorizada.
/// </summary>
public static partial class ValidadorEnvio
{
    public static ResultadoValidacao Validar(RascunhoResposta? rascunho, ContextoValidacao ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (rascunho is null)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.RascunhoInvalido);
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
        var numerosFontes = Normalizacao.Numeros(conteudoFontes);

        // 5. Padrões proibidos (antes dos valores, para dar o motivo mais específico).
        if (AutodeclaracaoHumana().IsMatch(texto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.AutodeclaracaoHumana);
        }

        if (EhMenu(rascunho.Texto, texto))
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.MenuOuOpcoes);
        }

        foreach (Match url in Url().Matches(texto))
        {
            if (!DominioPermitido(url.Groups["host"].Value, ctx.Politica.DominiosPermitidos))
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.UrlNaoPermitida, url.Groups["host"].Value);
            }
        }

        // 6. Telefones: nenhum número de telefone que não seja o do cliente atual ou conste numa fonte.
        var telefoneCliente = Normalizacao.SomenteDigitos(ctx.TelefoneClienteAtual);
        var telefonesFontes = Telefones(conteudoFontes);
        foreach (var digitos in Telefones(texto))
        {
            if (!MesmoTelefone(digitos, telefoneCliente) && !telefonesFontes.Any(f => MesmoTelefone(digitos, f)))
            {
                return ResultadoValidacao.Bloquear(MotivoBloqueio.TelefoneNaoAutorizado);
            }
        }

        // 4. Valores: todo número do texto consta numa fonte citada. Na dúvida, bloqueia.
        var numeroSemFonte = Normalizacao.Numeros(texto).FirstOrDefault(n => !numerosFontes.Contains(n));
        if (numeroSemFonte is not null)
        {
            return ResultadoValidacao.Bloquear(MotivoBloqueio.ValorSemFonte, numeroSemFonte);
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

    private static bool EhMenu(string original, string normalizado)
    {
        var linhasNumeradas = original.Split('\n').Count(l => ItemDeMenu().IsMatch(Normalizacao.ParaComparacao(l)));
        return linhasNumeradas >= 2 || InstrucaoDeOpcao().IsMatch(normalizado);
    }

    private static bool DominioPermitido(string host, IReadOnlySet<string> permitidos) =>
        permitidos.Any(p =>
        {
            var d = Normalizacao.ParaComparacao(p).TrimStart('.');
            return host == d || host.EndsWith("." + d, StringComparison.Ordinal);
        });

    /// <summary>Sequências com cara de telefone brasileiro (10 a 13 dígitos, com ou sem separadores).</summary>
    private static List<string> Telefones(string texto) =>
        Telefone().Matches(texto)
            .Select(m => Normalizacao.SomenteDigitos(m.Value))
            .Where(d => d.Length is >= 10 and <= 13)
            .ToList();

    private static bool MesmoTelefone(string a, string b) =>
        a.Length >= 8 && b.Length >= 8 && (a.EndsWith(b, StringComparison.Ordinal) || b.EndsWith(a, StringComparison.Ordinal));

    private static bool ContemTermo(string texto, string termo) =>
        Regex.IsMatch(texto, $@"(?<![\p{{L}}\d]){Regex.Escape(termo)}(?![\p{{L}}\d])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"\b(sou|eu sou) (um |uma )?(humano|humana|pessoa|pessoa real|atendente humano|atendente humana|ser humano)\b|\bnao sou (um |uma )?(robo|bot|chatbot|ia|inteligencia artificial|assistente virtual|maquina|programa)\b|\b(voce esta|esta) falando com (um |uma )?(humano|humana|pessoa|pessoa real|ser humano)\b")]
    private static partial Regex AutodeclaracaoHumana();

    [GeneratedRegex(@"^\s*(\d{1,2}|[a-e])\s*[\).:\-]\s*\S")]
    private static partial Regex ItemDeMenu();

    [GeneratedRegex(@"\b(digite|responda|escolha|selecione|tecle|envie)\s+(o\s+|a\s+)?(numero\s+|opcao\s+)?\d\b|\bopcao\s+\d\b")]
    private static partial Regex InstrucaoDeOpcao();

    [GeneratedRegex(@"(?:https?://|www\.)?(?<host>(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+(?:com|br|net|org|io|me|ly|app|info|site|online|xyz|co|biz|link|shop|store|tk|ml|gov|edu)(?:\.[a-z]{2})?)\b")]
    private static partial Regex Url();

    [GeneratedRegex(@"\+?\d[\d\s().\-]{7,}\d")]
    private static partial Regex Telefone();
}
