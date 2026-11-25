using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Api.Supervisao;

/// <summary>
/// Comandos de linha para o ambiente de desenvolvimento. Usam a conexão do papel dono, lida da variável de
/// ambiente SECELETESC_CONEXAO_DONO (nunca de arquivo versionado).
/// </summary>
internal static class ComandosAdministracao
{
    private static readonly string[] Comandos = ["migrar", "criar-tenant", "criar-supervisor", "alterar-papel", "desativar-supervisor", "registrar-numero", "gerar-chaves"];

    public static bool EhComando(string arg) => Comandos.Contains(arg, StringComparer.Ordinal);

    public static async Task<int> ExecutarAsync(string[] args)
    {
        // Gerar as chaves não usa o banco.
        if (args[0] == "gerar-chaves")
        {
            return GerarChaves(Opcao(args, "--pasta"));
        }

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
            case "criar-supervisor" when Guid.TryParse(Opcao(args, "--tenant"), out var tenant) && Opcao(args, "--nome") is { } nome
                                        && LerPapel(Opcao(args, "--papel") ?? nameof(PapelSupervisao.Atendente)) is { } papel:
                var (id, chave) = await AdministracaoSupervisao.CriarSupervisorAsync(conexao, tenant, nome, papel).ConfigureAwait(false);
                Console.WriteLine($"usuario: {id} ({papel})");
                Console.WriteLine($"chave:   {chave}");
                Console.WriteLine("Guarde a chave agora: ela não é exibida de novo.");
                return 0;
            case "alterar-papel" when Guid.TryParse(Opcao(args, "--usuario"), out var alvo) && LerPapel(Opcao(args, "--papel")) is { } novoPapel:
                Console.WriteLine(await AdministracaoSupervisao.AlterarPapelAsync(conexao, alvo, novoPapel).ConfigureAwait(false) ? "Papel alterado." : "Usuário não encontrado.");
                return 0;
            case "registrar-numero" when Guid.TryParse(Opcao(args, "--tenant"), out var tenantNumero) && Opcao(args, "--phone-number-id") is { } numero:
                // O phone_number_id da Meta é numérico; o prefixo "demo-" serve só à demonstração local.
                if (!System.Text.RegularExpressions.Regex.IsMatch(numero, @"^(\d{5,30}|demo-[a-f0-9]{4,32})$"))
                {
                    await Console.Error.WriteLineAsync("phone_number_id inválido: use o identificador numérico da Meta.").ConfigureAwait(false);
                    return 2;
                }

                try
                {
                    await AdministracaoSupervisao.RegistrarNumeroAsync(conexao, tenantNumero, numero).ConfigureAwait(false);
                }
                catch (Npgsql.PostgresException e) when (e.SqlState is Npgsql.PostgresErrorCodes.UniqueViolation or Npgsql.PostgresErrorCodes.ForeignKeyViolation)
                {
                    await Console.Error.WriteLineAsync(e.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation
                        ? "Este número já está ligado a uma empresa." : "Empresa não encontrada.").ConfigureAwait(false);
                    return 2;
                }

                Console.WriteLine("Número registrado para a empresa.");
                return 0;
            case "desativar-supervisor" when Guid.TryParse(Opcao(args, "--usuario"), out var usuario):
                Console.WriteLine(await AdministracaoSupervisao.DesativarSupervisorAsync(conexao, usuario).ConfigureAwait(false) ? "Desativado." : "Usuário não encontrado.");
                return 0;
            default:
                await Console.Error.WriteLineAsync("""
                    Uso:
                      migrar
                      criar-tenant --nome "<nome>"
                      criar-supervisor --tenant <id> --nome "<nome>" [--papel Atendente|Supervisor|Administrador]
                      alterar-papel --usuario <id> --papel Atendente|Supervisor|Administrador
                      desativar-supervisor --usuario <id>
                      registrar-numero --tenant <id> --phone-number-id <id da Meta>
                      gerar-chaves --pasta <pasta fora do Git>
                    """).ConfigureAwait(false);
                return 2;
        }
    }

    /// <summary>
    /// Par de chaves RSA para cifrar dados protegidos: a pública vai para a configuração da API
    /// (DadosProtegidos:ChavePublicaPem); a privada fica com quem pode ver o dado original e nunca vai para o servidor
    /// da API nem para o Git. A pasta não pode já ter chaves (nada é sobrescrito).
    /// </summary>
    private static int GerarChaves(string? pasta)
    {
        if (string.IsNullOrWhiteSpace(pasta))
        {
            Console.Error.WriteLine("Uso: gerar-chaves --pasta <pasta fora do Git>");
            return 2;
        }

        // A chave privada não pode cair numa pasta versionada (repositório público): dentro do projeto, só em .local.
        var destino = Path.GetFullPath(pasta);
        var raiz = EncontrarRaizDoRepositorio();
        if (raiz is not null && destino.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)
            && !destino.StartsWith(Path.Combine(raiz, ".local"), StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Escolha uma pasta fora do projeto (ou dentro de .local, que não vai para o Git).");
            return 2;
        }

        var publica = Path.Combine(destino, "dados-protegidos-publica.pem");
        var privada = Path.Combine(destino, "dados-protegidos-privada.pem");
        Directory.CreateDirectory(destino);
        using var rsa = System.Security.Cryptography.RSA.Create(3072);
        try
        {
            // CreateNew: nunca sobrescreve (sem janela entre conferir e gravar).
            using (var f = new FileStream(publica, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(f))
            {
                w.Write(rsa.ExportSubjectPublicKeyInfoPem());
            }

            using (var f = new FileStream(privada, FileMode.CreateNew, FileAccess.Write))
            using (var w = new StreamWriter(f))
            {
                w.Write(rsa.ExportPkcs8PrivateKeyPem());
            }
        }
        catch (IOException)
        {
            Console.Error.WriteLine("A pasta já tem chaves: nada foi sobrescrito.");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(privada, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Console.WriteLine($"Chave pública: {publica}");
        Console.WriteLine($"Chave privada: {privada} (guarde com segurança; não vai para o servidor da API)");
        return 0;
    }

    private static string? EncontrarRaizDoRepositorio()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Seceletesc.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }

    /// <summary>Só o nome exato do papel (sem números nem combinações).</summary>
    private static PapelSupervisao? LerPapel(string? nome) =>
        nome is not null && Enum.GetNames<PapelSupervisao>().Contains(nome, StringComparer.Ordinal) ? Enum.Parse<PapelSupervisao>(nome) : null;

    private static string? Opcao(string[] args, string nome)
    {
        var i = Array.IndexOf(args, nome);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[i + 1] : null;
    }
}
