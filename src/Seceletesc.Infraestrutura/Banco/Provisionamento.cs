namespace Seceletesc.Infraestrutura.Banco;

/// <summary>
/// Script de provisionamento dos papéis do banco. Executado uma vez por um administrador do PostgreSQL,
/// nunca pela aplicação. Exposto aqui para os testes e para a automação de infraestrutura.
/// </summary>
public static class Provisionamento
{
    private const string Recurso = "Seceletesc.Infraestrutura.Banco.Provisionamento.provisionar-papeis.sql";

    public static string ScriptPapeis()
    {
        using var stream = typeof(Provisionamento).Assembly.GetManifestResourceStream(Recurso)
            ?? throw new InvalidOperationException($"Recurso não encontrado: {Recurso}");
        using var leitor = new StreamReader(stream);
        return leitor.ReadToEnd();
    }
}
