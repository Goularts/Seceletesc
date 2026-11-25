using System.Text.Json;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Politicas;

namespace Seceletesc.Infraestrutura.Nucleo;

/// <summary>Formato persistido da política (politicas_tenant.conteudo). Campos ausentes assumem o padrão conservador.</summary>
public static class PoliticaJson
{
    private static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web);

    public static string Serializar(PoliticaTenant p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return JsonSerializer.Serialize(new Dto
        {
            AssuntosAutonomos = [.. p.AssuntosAutonomos],
            AssuntosExigemHumano = [.. p.AssuntosExigemHumano],
            ClassificacoesPermitidas = [.. p.ClassificacoesPermitidasRespostaAutomatica.Select(c => c.ToString())],
            TextoAvisoTransferencia = p.TextoAvisoTransferencia,
            TermosCompromisso = [.. p.TermosCompromisso],
            DominiosPermitidos = [.. p.DominiosPermitidos],
            EnviarConhecimentoClienteAoModelo = p.EnviarConhecimentoClienteAoModelo,
            ValidadeMensagemAutomaticaSegundos = (int)p.ValidadeMensagemAutomatica.TotalSeconds,
        }, Opcoes);
    }

    public static PoliticaTenant Ler(Guid tenantId, int versao, string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Opcoes) ?? throw new InvalidOperationException("Política vazia.");
        var padrao = new PoliticaTenant { TenantId = tenantId, Versao = versao, AssuntosAutonomos = new HashSet<string>() };
        return padrao with
        {
            AssuntosAutonomos = new HashSet<string>(dto.AssuntosAutonomos ?? []),
            AssuntosExigemHumano = new HashSet<string>(dto.AssuntosExigemHumano ?? []),
            ClassificacoesPermitidasRespostaAutomatica = dto.ClassificacoesPermitidas is { } c
                ? new HashSet<Classificacao>(c.Select(LerClassificacao))
                : padrao.ClassificacoesPermitidasRespostaAutomatica,
            TextoAvisoTransferencia = dto.TextoAvisoTransferencia ?? string.Empty,
            TermosCompromisso = dto.TermosCompromisso ?? padrao.TermosCompromisso,
            DominiosPermitidos = new HashSet<string>(dto.DominiosPermitidos ?? []),
            EnviarConhecimentoClienteAoModelo = dto.EnviarConhecimentoClienteAoModelo,
            ValidadeMensagemAutomatica = dto.ValidadeMensagemAutomaticaSegundos is > 0 and var s
                ? TimeSpan.FromSeconds(s)
                : padrao.ValidadeMensagemAutomatica,
        };
    }

    /// <summary>
    /// Só o nome exato de uma classificação. Enum.Parse aceitaria números ("99") e combinações ("Publico, Interno"),
    /// que viram outra classificação ou um valor inexistente.
    /// </summary>
    private static Classificacao LerClassificacao(string nome) =>
        Enum.GetNames<Classificacao>().Contains(nome, StringComparer.Ordinal)
            ? Enum.Parse<Classificacao>(nome)
            : throw new FormatException($"Classificação desconhecida: '{nome}'.");

    private sealed class Dto
    {
        public List<string>? AssuntosAutonomos { get; set; }

        public List<string>? AssuntosExigemHumano { get; set; }

        public List<string>? ClassificacoesPermitidas { get; set; }

        public string? TextoAvisoTransferencia { get; set; }

        public List<string>? TermosCompromisso { get; set; }

        public List<string>? DominiosPermitidos { get; set; }

        public bool EnviarConhecimentoClienteAoModelo { get; set; }

        public int? ValidadeMensagemAutomaticaSegundos { get; set; }
    }
}
