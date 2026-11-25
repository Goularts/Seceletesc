using System.Text.RegularExpressions;
using Mono.Cecil;

namespace Seceletesc.Arquitetura.Testes;

/// <summary>
/// Proteção automática das regras (qualidade.md, seção 4).
/// Conta, nos assemblies de teste compilados, os métodos [Fact]/[Theory] ativos (sem Skip) marcados com
/// [Trait("Regra", ID)]. Teste comentado, desativado ou sem atributo de teste não conta.
/// Cada regra obrigatória tem um mínimo de testes em docs/desenvolvimento/cobertura-obrigatoria.txt;
/// remover testes abaixo do mínimo quebra o CI. Reduzir o mínimo ou tirar uma regra da lista é barrado
/// por outro passo do CI, que compara o arquivo com o da branch de destino.
/// </summary>
public sealed partial class CoberturaRegrasTestes
{
    private static readonly string Raiz = EncontrarRaiz();

    [Fact]
    public void Todo_teste_de_regra_aponta_para_uma_regra_existente()
    {
        var conhecidas = RegrasConhecidas();
        var orfas = ContarTestesPorRegra().Keys.Where(id => !conhecidas.Contains(id)).Order().ToList();

        Assert.True(orfas.Count == 0, "Testes marcados com regras inexistentes: " + string.Join(", ", orfas));
    }

    [Fact]
    public void Toda_regra_obrigatoria_tem_o_minimo_de_testes_ativos()
    {
        var contagem = ContarTestesPorRegra();
        var faltas = LerObrigatorias()
            .Select(o => (o.Id, o.Minimo, Atual: contagem.GetValueOrDefault(o.Id)))
            .Where(x => x.Atual < x.Minimo)
            .Select(x => $"{x.Id}: {x.Atual} de {x.Minimo}")
            .ToList();

        Assert.True(faltas.Count == 0, "Regras abaixo do mínimo de testes ativos: " + string.Join("; ", faltas));
    }

    [Fact]
    public void Toda_regra_obrigatoria_existe()
    {
        var conhecidas = RegrasConhecidas();
        var inexistentes = LerObrigatorias().Select(o => o.Id).Where(id => !conhecidas.Contains(id)).ToList();

        Assert.True(inexistentes.Count == 0, "Regras obrigatórias que não existem: " + string.Join(", ", inexistentes));
    }

    /// <summary>
    /// Registro de regressões (docs/desenvolvimento/regressoes.md): todo bug corrigido aponta um teste que existe e está
    /// ativo. Apagar, renomear ou ignorar esse teste quebra o CI. Fonte versionada; o Ruflo só recebe uma cópia.
    /// </summary>
    [Fact]
    public void Toda_regressao_registrada_tem_teste_ativo()
    {
        var ativos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dll in AssembliesDeTeste())
        {
            using var modulo = ModuleDefinition.ReadModule(dll);
            foreach (var metodo in modulo.GetTypes().SelectMany(t => t.Methods).Where(EhTesteAtivo))
            {
                ativos.Add(metodo.Name);
            }
        }

        var linhas = File.ReadAllLines(Path.Combine(Raiz, "docs", "desenvolvimento", "regressoes.md"))
            .Where(l => l.StartsWith("| REG-", StringComparison.Ordinal))
            .Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
            .ToList();
        var ids = linhas.Select(c => c[1]).ToList();
        var semTeste = linhas.Where(c => !ativos.Contains(c[5])).Select(c => $"{c[1]} -> {c[5]}").ToList();

        Assert.NotEmpty(linhas);
        Assert.True(ids.Count == ids.Distinct(StringComparer.Ordinal).Count(), "IDs de regressão repetidos");
        Assert.True(semTeste.Count == 0, "Regressões sem teste ativo: " + string.Join("; ", semTeste));
    }

    private static List<(string Id, int Minimo)> LerObrigatorias() =>
        File.ReadAllLines(Path.Combine(Raiz, "docs", "desenvolvimento", "cobertura-obrigatoria.txt"))
            .Select(l => l.Split('#')[0].Trim())
            .Where(l => l.Length > 0)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

    private static Dictionary<string, int> ContarTestesPorRegra()
    {
        var contagem = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var dll in AssembliesDeTeste())
        {
            using var modulo = ModuleDefinition.ReadModule(dll);
            foreach (var metodo in modulo.GetTypes().SelectMany(t => t.Methods))
            {
                if (!EhTesteAtivo(metodo))
                {
                    continue;
                }

                foreach (var id in RegrasDoMetodo(metodo))
                {
                    contagem[id] = contagem.GetValueOrDefault(id) + 1;
                }
            }
        }

        return contagem;
    }

    private static bool EhTesteAtivo(MethodDefinition metodo) =>
        metodo.CustomAttributes.Any(a =>
            a.AttributeType.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute"
            && !a.Properties.Any(p => p.Name == "Skip" && p.Argument.Value is string s && s.Length > 0));

    private static IEnumerable<string> RegrasDoMetodo(MethodDefinition metodo) =>
        metodo.CustomAttributes
            .Where(a => a.AttributeType.FullName == "Xunit.TraitAttribute"
                        && a.ConstructorArguments.Count == 2
                        && a.ConstructorArguments[0].Value as string == "Regra")
            .Select(a => (string)a.ConstructorArguments[1].Value);

    /// <summary>Os assemblies dos outros projetos de teste, na mesma configuração e framework deste.</summary>
    private static List<string> AssembliesDeTeste()
    {
        var saida = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var framework = saida.Name;
        var configuracao = saida.Parent!.Name;

        var dlls = Directory.EnumerateDirectories(Path.Combine(Raiz, "tests"), "Seceletesc.*.Testes")
            .Select(d => Path.Combine(d, "bin", configuracao, framework, Path.GetFileName(d) + ".dll"))
            .ToList();

        var faltando = dlls.Where(d => !File.Exists(d)).ToList();
        if (faltando.Count > 0)
        {
            throw new InvalidOperationException("Compile a solução inteira antes. Assemblies de teste ausentes: " + string.Join(", ", faltando));
        }

        return dlls;
    }

    private static HashSet<string> RegrasConhecidas()
    {
        var invariantes = File.ReadAllText(Path.Combine(Raiz, "docs", "business-rules", "invariants.md"));
        var matriz = File.ReadAllText(Path.Combine(Raiz, "docs", "desenvolvimento", "matriz-rastreabilidade.md"));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in NumeroInvariante().Matches(invariantes))
        {
            ids.Add("INV-" + m.Groups["n"].Value);
        }

        foreach (Match m in IdCtxOuAdv().Matches(matriz))
        {
            ids.Add(m.Value);
        }

        return ids;
    }

    private static string EncontrarRaiz()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Seceletesc.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório (Seceletesc.slnx) não encontrada.");
    }

    [GeneratedRegex(@"^(?<n>\d+)\. ", RegexOptions.Multiline)]
    private static partial Regex NumeroInvariante();

    [GeneratedRegex(@"\b(CTX-\d{3}|ADV-\d{2})\b")]
    private static partial Regex IdCtxOuAdv();
}
