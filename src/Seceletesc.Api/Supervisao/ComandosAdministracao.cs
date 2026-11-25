using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Api.Supervisao;

/// <summary>
/// Comandos de linha para o ambiente de desenvolvimento. Usam a conexão do papel dono, lida da variável de
/// ambiente SECELETESC_CONEXAO_DONO (nunca de arquivo versionado).
/// </summary>
internal static class ComandosAdministracao
{
    private static readonly string[] Comandos = ["migrar", "criar-tenant", "criar-supervisor", "desativar-supervisor"];

    public static bool EhComando(string arg) => Comandos.Contains(arg, StringComparer.Ordinal);

    public static async Task<int> ExecutarAsync(string[] args)
    {
        var conexao = Environment.GetEnvironmentVariable("SECELETESC_CONEXAO_DONO");
        if (string.IsNullOrWhiteSpace(conexao))
        {
            await Console.Error.WriteLineAsync("Defina SECELETESC_CONEXAO_DONO com a conexão do papel seceletesc_dono.").ConfigureAwait(false);
            return 2;
        }

        switch (args[0])
        {
            case "migrar":
                await Migrador.AplicarAsync(conexao).ConfigureAwait(false);
                Console.WriteLine("Migrações aplicadas.");
                return 0;
            case "criar-tenant" when Opcao(args, "--nome") is { } nome:
                Console.WriteLine(await AdministracaoSupervisao.CriarTenantAsync(conexao, nome).ConfigureAwait(false));
                return 0;
            case "criar-supervisor" when Guid.TryParse(Opcao(args, "--tenant"), out var tenant) && Opcao(args, "--nome") is { } nome:
                var (id, chave) = await AdministracaoSupervisao.CriarSupervisorAsync(conexao, tenant, nome).ConfigureAwait(false);
                Console.WriteLine($"usuario: {id}");
                Console.WriteLine($"chave:   {chave}");
                Console.WriteLine("Guarde a chave agora: ela não é exibida de novo.");
                return 0;
            case "desativar-supervisor" when Guid.TryParse(Opcao(args, "--usuario"), out var usuario):
                Console.WriteLine(await AdministracaoSupervisao.DesativarSupervisorAsync(conexao, usuario).ConfigureAwait(false) ? "Desativado." : "Usuário não encontrado.");
                return 0;
            default:
                await Console.Error.WriteLineAsync("""
                    Uso:
                      migrar
                      criar-tenant --nome "<nome>"
                      criar-supervisor --tenant <id> --nome "<nome>"
                      desativar-supervisor --usuario <id>
                    """).ConfigureAwait(false);
                return 2;
        }
    }

    private static string? Opcao(string[] args, string nome)
    {
        var i = Array.IndexOf(args, nome);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[i + 1] : null;
    }
}
