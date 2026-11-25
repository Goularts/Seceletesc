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
            // O piso dos termos vem do código: grava só o que a política acrescenta.
            TermosCompromisso = [.. p.TermosCompromissoAdicionais],
            EmailsInstitucionais = [.. p.EmailsInstitucionais.Order(StringComparer.Ordinal)],
            ValidadeMaximaDias = p.ValidadeMaximaDias.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
            DominiosPermitidos = [.. p.DominiosPermitidos],
            EnviarConhecimentoClienteAoModelo = p.EnviarConhecimentoClienteAoModelo,
            ValidadeMensagemAutomaticaSegundos = (int)p.ValidadeMensagemAutomatica.TotalSeconds,
        }, Opcoes);
    }

    public static PoliticaTenant Ler(Guid tenantId, int versao, string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Opcoes) ?? throw new InvalidOperationException("Política vazia.");
        // Elemento nulo em qualquer lista é formato inválido (revisão pós-F3, S10), não erro interno.
        if (new[] { dto.AssuntosAutonomos, dto.AssuntosExigemHumano, dto.ClassificacoesPermitidas, dto.TermosCompromisso, dto.DominiosPermitidos, dto.EmailsInstitucionais }
            .Any(l => l is not null && l.Any(x => x is null)))
        {
            throw new FormatException("listas da política não aceitam elementos nulos");
        }

        var padrao = new PoliticaTenant { TenantId = tenantId, Versao = versao, AssuntosAutonomos = new HashSet<string>() };
        return padrao with
        {
            AssuntosAutonomos = new HashSet<string>(dto.AssuntosAutonomos ?? []),
            AssuntosExigemHumano = new HashSet<string>(dto.AssuntosExigemHumano ?? []),
            ClassificacoesPermitidasRespostaAutomatica = dto.ClassificacoesPermitidas is { } c
                ? new HashSet<Classificacao>(c.Select(LerClassificacao))
                : padrao.ClassificacoesPermitidasRespostaAutomatica,
            TextoAvisoTransferencia = dto.TextoAvisoTransferencia ?? string.Empty,
            TermosCompromisso = dto.TermosCompromisso ?? [],
            EmailsInstitucionais = new HashSet<string>((dto.EmailsInstitucionais ?? []).Select(e => e.Trim().ToLowerInvariant())),
            ValidadeMaximaDias = (dto.ValidadeMaximaDias ?? []).ToDictionary(kv => LerClassificacao(kv.Key), kv => kv.Value),
            DominiosPermitidos = new HashSet<string>(dto.DominiosPermitidos ?? []),
            EnviarConhecimentoClienteAoModelo = dto.EnviarConhecimentoClienteAoModelo,
            ValidadeMensagemAutomatica = dto.ValidadeMensagemAutomaticaSegundos is > 0 and var s
                ? TimeSpan.FromSeconds(s)
                : padrao.ValidadeMensagemAutomatica,
        };
    }

    /// <summary>Classificações pedidas no JSON, antes do piso (para recusar explicitamente INT e RES na publicação).</summary>
    public static IReadOnlyList<string> ClassificacoesPedidas(string json) =>
        JsonSerializer.Deserialize<Dto>(json, Opcoes)?.ClassificacoesPermitidas ?? [];

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

        public List<string>? EmailsInstitucionais { get; set; }

        public Dictionary<string, int>? ValidadeMaximaDias { get; set; }

        public bool EnviarConhecimentoClienteAoModelo { get; set; }

        public int? ValidadeMensagemAutomaticaSegundos { get; set; }
    }
}
