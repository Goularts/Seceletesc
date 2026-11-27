using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Api.Supervisao;

/// <summary>
/// Comandos de linha para o ambiente de desenvolvimento. Usam a conexão do papel dono, lida da variável de
/// ambiente SECELETESC_CONEXAO_DONO (nunca de arquivo versionado).
/// </summary>
internal static class ComandosAdministracao
{
    private static readonly string[] Comandos = ["migrar", "criar-tenant", "criar-supervisor", "alterar-papel", "desativar-supervisor", "registrar-numero", "gerar-chaves",
        "conectar-conta", "registrar-credencial", "desconectar-conta", "autorizar-operacao", "revogar-operacao", "habilitar-envio", "desabilitar-envio", "gerar-chave-credenciais"];

    public static bool EhComando(string arg) => Comandos.Contains(arg, StringComparer.Ordinal);

    public static async Task<int> ExecutarAsync(string[] args)
    {
        // Gerar as chaves não usa o banco.
        if (args[0] == "gerar-chaves")
        {
            return GerarChaves(Opcao(args, "--pasta"));
        }

        if (args[0] == "gerar-chave-credenciais")
        {
            return GerarChaveCredenciais(Opcao(args, "--pasta"));
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
            case "registrar-numero" or "conectar-conta" or "registrar-credencial" or "desconectar-conta" or "autorizar-operacao"
                or "revogar-operacao" or "habilitar-envio" or "desabilitar-envio":
                return await ComandosPlataforma.ExecutarAsync(args, conexao).ConfigureAwait(false);
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
                      gerar-chaves --pasta <pasta fora do Git>
                    Operador da plataforma (contas do WhatsApp de cada empresa; ver docs\setup\GUIA-ADMINISTRADOR.md):
                      conectar-conta --tenant <id> --waba <id> [--business-id <id>] --operador "<nome>"
                      registrar-credencial --tenant <id> --waba <id> --arquivo <arquivo com o token> --operador "<nome>"
                      desconectar-conta --tenant <id> --waba <id> --operador "<nome>" --motivo "<motivo>"
                      registrar-numero --tenant <id> --phone-number-id <id> [--waba <id>] --operador "<nome>"
                      autorizar-operacao | revogar-operacao --tenant <id> --phone-number-id <id> --operador "<nome>" --motivo "<motivo>"
                      habilitar-envio | desabilitar-envio --tenant <id> --phone-number-id <id> --operador "<nome>" --motivo "<motivo>"
                      gerar-chave-credenciais --pasta <pasta fora do Git>
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

    /// <summary>
    /// Chave que cifra as credenciais do WhatsApp no banco (WhatsApp:ChaveCredenciais). Fica com a API e com o operador
    /// que registra credenciais; nunca no Git nem no banco. A pasta segue a mesma regra das chaves de dados protegidos.
    /// </summary>
    private static int GerarChaveCredenciais(string? pasta)
    {
        if (PastaPermitida(pasta) is not { } destino)
        {
            Console.Error.WriteLine("Uso: gerar-chave-credenciais --pasta <pasta fora do projeto ou dentro de .local>");
            return 2;
        }

        var arquivo = Path.Combine(destino, "chave-credenciais-whatsapp.txt");
        Directory.CreateDirectory(destino);
        try
        {
            using var f = new FileStream(arquivo, FileMode.CreateNew, FileAccess.Write);
            using var w = new StreamWriter(f);
            w.Write(Infraestrutura.Criptografia.CifradorCredenciais.GerarChaveBase64());
        }
        catch (IOException)
        {
            Console.Error.WriteLine("A pasta já tem uma chave de credenciais: nada foi sobrescrito.");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(arquivo, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        Console.WriteLine($"Chave das credenciais: {arquivo}");
        Console.WriteLine("Grave o conteúdo em WhatsApp:ChaveCredenciais (user-secrets ou variável de ambiente), sem exibir na tela.");
        return 0;
    }

    /// <summary>Pasta fora do repositório ou dentro de .local (que não vai para o Git); nulo se não permitida.</summary>
    internal static string? PastaPermitida(string? pasta)
    {
        if (string.IsNullOrWhiteSpace(pasta))
        {
            return null;
        }

        var destino = Path.GetFullPath(pasta);
        var raiz = EncontrarRaizDoRepositorio();
        return raiz is not null && destino.StartsWith(raiz, StringComparison.OrdinalIgnoreCase)
               && !destino.StartsWith(Path.Combine(raiz, ".local"), StringComparison.OrdinalIgnoreCase)
            ? null
            : destino;
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

    internal static string? Opcao(string[] args, string nome)
    {
        var i = Array.IndexOf(args, nome);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) ? args[i + 1] : null;
    }
}
