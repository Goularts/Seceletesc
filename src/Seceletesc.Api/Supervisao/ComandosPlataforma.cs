using Npgsql;
using Seceletesc.Infraestrutura.Criptografia;
using Seceletesc.Infraestrutura.Supervisao;

namespace Seceletesc.Api.Supervisao;

/// <summary>
/// Comandos do operador da plataforma para as contas do WhatsApp das empresas (decisão 34; plano técnico, seção 4.13).
/// O token nunca é argumento (fica no histórico do terminal e na lista de processos): vem de um arquivo indicado pelo
/// operador, e nada dele é exibido. A chave das credenciais vem de WhatsApp:ChaveCredenciais (user-secrets ou
/// variável de ambiente WhatsApp__ChaveCredenciais).
/// </summary>
internal static class ComandosPlataforma
{
    public static async Task<int> ExecutarAsync(string[] args, string conexao)
    {
        var comando = args[0];
        var tenantOk = Guid.TryParse(ComandosAdministracao.Opcao(args, "--tenant"), out var tenant);
        var operador = ComandosAdministracao.Opcao(args, "--operador");
        var motivo = ComandosAdministracao.Opcao(args, "--motivo");
        var waba = ComandosAdministracao.Opcao(args, "--waba");
        var numero = ComandosAdministracao.Opcao(args, "--phone-number-id");
        if (!tenantOk || operador is null)
        {
            return await ErroAsync("Informe --tenant <id> e --operador \"<nome>\" (rode sem argumentos para ver o uso).").ConfigureAwait(false);
        }

        if ((waba is not null && !OperacoesPlataforma.IdentificadorValido(waba)) || (numero is not null && !OperacoesPlataforma.IdentificadorValido(numero)))
        {
            return await ErroAsync("Identificador inválido: use o identificador numérico da Meta.").ConfigureAwait(false);
        }

        try
        {
            switch (comando)
            {
                case "conectar-conta" when waba is not null:
                    var business = ComandosAdministracao.Opcao(args, "--business-id");
                    if (business is not null && !System.Text.RegularExpressions.Regex.IsMatch(business, @"^\d{5,30}$"))
                    {
                        return await ErroAsync("--business-id inválido.").ConfigureAwait(false);
                    }

                    await OperacoesPlataforma.ConectarContaAsync(conexao, tenant, waba, business, operador).ConfigureAwait(false);
                    Console.WriteLine("Conta conectada. O envio dos números continua desligado até a autorização e a habilitação.");
                    return 0;
                case "registrar-credencial" when waba is not null && ComandosAdministracao.Opcao(args, "--arquivo") is { } arquivo:
                    return await RegistrarCredencialAsync(conexao, tenant, waba, arquivo, operador).ConfigureAwait(false);
                case "desconectar-conta" when waba is not null && motivo is not null:
                    await OperacoesPlataforma.DesconectarContaAsync(conexao, tenant, waba, operador, motivo).ConfigureAwait(false);
                    Console.WriteLine("Conta desconectada: credencial revogada e envio dos números desligado.");
                    return 0;
                case "registrar-numero" when numero is not null:
                    await OperacoesPlataforma.RegistrarNumeroAsync(conexao, tenant, numero, waba, operador).ConfigureAwait(false);
                    Console.WriteLine("Número registrado para a empresa (envio desligado).");
                    return 0;
                case "autorizar-operacao" or "revogar-operacao" when numero is not null && motivo is not null:
                    await OperacoesPlataforma.AutorizarOperacaoAsync(conexao, tenant, numero, operador, motivo, comando == "autorizar-operacao").ConfigureAwait(false);
                    Console.WriteLine(comando == "autorizar-operacao" ? "Operação autorizada (o envio continua desligado)." : "Autorização revogada (envio desligado).");
                    return 0;
                case "habilitar-envio" or "desabilitar-envio" when numero is not null && motivo is not null:
                    await OperacoesPlataforma.AlterarEnvioAsync(conexao, tenant, numero, operador, motivo, comando == "habilitar-envio").ConfigureAwait(false);
                    Console.WriteLine(comando == "habilitar-envio"
                        ? "Envio habilitado para o número. Nada sai enquanto WhatsApp:EnvioHabilitado estiver desligado na API."
                        : "Envio desabilitado para o número.");
                    return 0;
                default:
                    return await ErroAsync("Faltam opções obrigatórias (rode sem argumentos para ver o uso).").ConfigureAwait(false);
            }
        }
        catch (PostgresException e) when (e.SqlState is "P0001" or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation
                                              or PostgresErrorCodes.CheckViolation)
        {
            // Mensagens das funções do banco (sem dados sensíveis) ou violações de cadastro.
            return await ErroAsync(e.SqlState switch
            {
                "P0001" => e.MessageText,
                PostgresErrorCodes.UniqueViolation => "Já registrado (ou pertence a outra empresa): " + e.MessageText,
                PostgresErrorCodes.ForeignKeyViolation => "Empresa ou conta (WABA) não encontrada.",
                _ => "Operação recusada pelas regras do cadastro.",
            }).ConfigureAwait(false);
        }
    }

    private static async Task<int> RegistrarCredencialAsync(string conexao, Guid tenant, string waba, string arquivo, string operador)
    {
        var configuracao = new ConfigurationBuilder()
            .AddUserSecrets(typeof(ComandosPlataforma).Assembly, optional: true)
            .AddEnvironmentVariables()
            .Build();
        if (CifradorCredenciais.DaConfiguracao(configuracao["WhatsApp:ChaveCredenciais"]) is not { } cifrador)
        {
            return await ErroAsync("WhatsApp:ChaveCredenciais ausente ou inválida (gere com gerar-chave-credenciais).").ConfigureAwait(false);
        }

        // O repositório é público (revisão da M1, S3): o arquivo do token fica fora do projeto ou dentro de .local.
        if (ComandosAdministracao.PastaPermitida(Path.GetDirectoryName(Path.GetFullPath(arquivo))) is null)
        {
            return await ErroAsync("Guarde o arquivo do token fora do projeto (ou dentro de .local, que não vai para o Git).").ConfigureAwait(false);
        }

        string token;
        try
        {
            token = (await File.ReadAllTextAsync(arquivo).ConfigureAwait(false)).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return await ErroAsync("Não foi possível ler o arquivo do token.").ConfigureAwait(false);
        }

        if (!OperacoesPlataforma.TokenValido(token))
        {
            return await ErroAsync("O arquivo não tem um token no formato esperado (nada foi gravado).").ConfigureAwait(false);
        }

        await OperacoesPlataforma.RegistrarCredencialAsync(conexao, cifrador, tenant, waba, token, operador).ConfigureAwait(false);
        Console.WriteLine($"Credencial registrada, cifrada (chave {cifrador.ChaveId}). A anterior, se havia, foi revogada.");
        Console.WriteLine("Apague agora o arquivo do token.");
        return 0;
    }

    private static async Task<int> ErroAsync(string mensagem)
    {
        await Console.Error.WriteLineAsync(mensagem).ConfigureAwait(false);
        return 2;
    }
}
