using System.Globalization;
using System.Net;
using System.Text;
using Npgsql;
using Seceletesc.Aplicacao.Envio;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Criptografia;
using Seceletesc.Infraestrutura.Supervisao;
using Seceletesc.Infraestrutura.WhatsApp;
using Seceletesc.Integracao.Testes.Pipeline;
using Seceletesc.Integracao.Testes.Supervisao;

namespace Seceletesc.Integracao.Testes.WhatsApp;

/// <summary>
/// M1 (decisão 34; plano técnico, seção 4.13): contas do WhatsApp por empresa. Credencial cifrada por empresa e WABA,
/// lida só pelo envio e só com as três etapas do número cumpridas (conta conectada com credencial, autorização
/// operacional, envio habilitado); número de saída = número em que o cliente escreveu por último. Nada fala com a Meta.
/// </summary>
[Collection(ColecaoBanco.Nome)]
public sealed class ContasWhatsAppTestes(BancoFixture banco)
{
    private const string Operador = "Operador de teste";
    private const string Token = "token-de-teste-EAAG0123456789abcdef";
    private static readonly CifradorCredenciais Cifrador = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    // ------------------------------------------------------------------ cifragem

    [Fact]
    [Trait("Regra", "INV-10")]
    public void Credencial_cifrada_so_decifra_na_mesma_empresa_WABA_e_chave()
    {
        var tenant = Guid.NewGuid();
        var cifrado = Cifrador.Cifrar(Token, tenant, "111111");

        Assert.Equal(Token, Cifrador.Decifrar(cifrado, tenant, "111111"));
        Assert.Null(Cifrador.Decifrar(cifrado, Guid.NewGuid(), "111111"));
        Assert.Null(Cifrador.Decifrar(cifrado, tenant, "222222"));
        Assert.Null(new CifradorCredenciais(new byte[32]).Decifrar(cifrado, tenant, "111111"));
        cifrado[^1] ^= 1;
        Assert.Null(Cifrador.Decifrar(cifrado, tenant, "111111"));
        Assert.DoesNotContain("EAAG", Encoding.UTF8.GetString(Cifrador.Cifrar(Token, tenant, "111111")), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public void Chave_das_credenciais_invalida_nao_gera_cifrador()
    {
        Assert.Null(CifradorCredenciais.DaConfiguracao(null));
        Assert.Null(CifradorCredenciais.DaConfiguracao("nao-e-base64"));
        Assert.Null(CifradorCredenciais.DaConfiguracao(Convert.ToBase64String(new byte[16])));
        Assert.NotNull(CifradorCredenciais.DaConfiguracao(CifradorCredenciais.GerarChaveBase64()));
    }

    // ------------------------------------------------------------------ três etapas

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Credencial_so_sai_para_o_envio_com_as_tres_etapas_do_numero()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var repo = new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador);
        var waba = NovoId();
        var numero = NovoId();

        await OperacoesPlataforma.ConectarContaAsync(banco.ConnectionStringDono, amb.TenantId, waba, null, Operador);
        await OperacoesPlataforma.RegistrarNumeroAsync(banco.ConnectionStringDono, amb.TenantId, numero, waba, Operador);
        Assert.Null(await repo.ObterParaEnvioAsync(amb.TenantId, numero, default));

        await OperacoesPlataforma.RegistrarCredencialAsync(banco.ConnectionStringDono, Cifrador, amb.TenantId, waba, Token, Operador);
        Assert.Null(await repo.ObterParaEnvioAsync(amb.TenantId, numero, default));

        await OperacoesPlataforma.AutorizarOperacaoAsync(banco.ConnectionStringDono, amb.TenantId, numero, Operador, "cliente conferido", true);
        Assert.Null(await repo.ObterParaEnvioAsync(amb.TenantId, numero, default));

        await OperacoesPlataforma.AlterarEnvioAsync(banco.ConnectionStringDono, amb.TenantId, numero, Operador, "autorizado pelo Victor", true);
        var credencial = await repo.ObterParaEnvioAsync(amb.TenantId, numero, default);

        Assert.NotNull(credencial);
        Assert.Equal(Token, credencial.Token);
        Assert.Equal(waba, credencial.WabaId);
        Assert.DoesNotContain(Token, credencial.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Numero_novo_comeca_com_envio_desligado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb, habilitar: false);

        Assert.False(await Escalar<bool>($"select envio_habilitado from tenant_numeros_whatsapp where phone_number_id = '{numero}'"));
        Assert.Null(await new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync(amb.TenantId, numero, default));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Habilitar_envio_sem_autorizacao_operacional_ou_sem_credencial_e_recusado()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);

        // Conta com credencial, mas número sem autorização operacional.
        var (_, semAutorizacao) = await NumeroProntoAsync(amb, autorizar: false, habilitar: false);
        var erroAutorizacao = await Assert.ThrowsAsync<PostgresException>(() =>
            OperacoesPlataforma.AlterarEnvioAsync(banco.ConnectionStringDono, amb.TenantId, semAutorizacao, Operador, "teste", true));
        Assert.Equal(PostgresErrorCodes.CheckViolation, erroAutorizacao.SqlState);

        // Número autorizado, mas a conta não tem credencial vigente.
        var waba = NovoId();
        var semCredencial = NovoId();
        await OperacoesPlataforma.ConectarContaAsync(banco.ConnectionStringDono, amb.TenantId, waba, null, Operador);
        await OperacoesPlataforma.RegistrarNumeroAsync(banco.ConnectionStringDono, amb.TenantId, semCredencial, waba, Operador);
        await OperacoesPlataforma.AutorizarOperacaoAsync(banco.ConnectionStringDono, amb.TenantId, semCredencial, Operador, "cliente conferido", true);
        var erroCredencial = await Assert.ThrowsAsync<PostgresException>(() =>
            OperacoesPlataforma.AlterarEnvioAsync(banco.ConnectionStringDono, amb.TenantId, semCredencial, Operador, "teste", true));
        Assert.Equal("P0001", erroCredencial.SqlState);

        Assert.Equal(0L, await Escalar<long>($"select count(*) from tenant_numeros_whatsapp where phone_number_id in ('{semAutorizacao}', '{semCredencial}') and envio_habilitado"));
    }

    [Theory]
    [Trait("Regra", "INV-6")]
    [InlineData("", "motivo")]
    [InlineData("Operador", "")]
    public async Task Autorizar_e_habilitar_exigem_operador_e_motivo(string operador, string motivo)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb, autorizar: false, habilitar: false);

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            OperacoesPlataforma.AutorizarOperacaoAsync(banco.ConnectionStringDono, amb.TenantId, numero, operador, motivo, true));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Revogar_a_autorizacao_desliga_o_envio()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);

        await OperacoesPlataforma.AutorizarOperacaoAsync(banco.ConnectionStringDono, amb.TenantId, numero, Operador, "contrato suspenso", false);

        Assert.False(await Escalar<bool>($"select envio_habilitado from tenant_numeros_whatsapp where phone_number_id = '{numero}'"));
        Assert.Null(await new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync(amb.TenantId, numero, default));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-15")]
    public async Task Desconectar_a_conta_revoga_a_credencial_e_desliga_o_envio_com_registro()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (waba, numero) = await NumeroProntoAsync(amb);

        await OperacoesPlataforma.DesconectarContaAsync(banco.ConnectionStringDono, amb.TenantId, waba, Operador, "cliente encerrou o contrato");

        Assert.Null(await new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync(amb.TenantId, numero, default));
        Assert.False(await Escalar<bool>($"select envio_habilitado from tenant_numeros_whatsapp where phone_number_id = '{numero}'"));
        Assert.Equal(0L, await Escalar<long>($"select count(*) from credenciais_whatsapp where waba_id = '{waba}' and revogada_em is null"));
        Assert.Equal(1L, await amb.ContarAsync("eventos_historico where tenant_id = @tenant and tipo = 'conta_whatsapp_desconectada'"));
        Assert.Equal(1L, await amb.ContarAsync("eventos_historico where tenant_id = @tenant and tipo = 'envio_whatsapp_desabilitado'"));

        // Revisão da M1, S4/G8: a autorização operacional também cai; ao reconectar, as três etapas são refeitas.
        Assert.Equal(1L, await Escalar<long>($"select count(*) from tenant_numeros_whatsapp where phone_number_id = '{numero}' and operacao_autorizada_em is null"));
        Assert.Equal(1L, await amb.ContarAsync("eventos_historico where tenant_id = @tenant and tipo = 'operacao_whatsapp_revogada'"));
        await OperacoesPlataforma.ConectarContaAsync(banco.ConnectionStringDono, amb.TenantId, waba, null, Operador);
        await OperacoesPlataforma.RegistrarCredencialAsync(banco.ConnectionStringDono, Cifrador, amb.TenantId, waba, Token, Operador);
        var semAutorizacao = await Assert.ThrowsAsync<PostgresException>(() =>
            OperacoesPlataforma.AlterarEnvioAsync(banco.ConnectionStringDono, amb.TenantId, numero, Operador, "reconexão", true));
        Assert.Equal(PostgresErrorCodes.CheckViolation, semAutorizacao.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Nova_credencial_revoga_a_anterior()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (waba, numero) = await NumeroProntoAsync(amb);

        await OperacoesPlataforma.RegistrarCredencialAsync(banco.ConnectionStringDono, Cifrador, amb.TenantId, waba, "token-novo-EAAG9876543210fedcba", Operador);

        Assert.Equal(1L, await Escalar<long>($"select count(*) from credenciais_whatsapp where waba_id = '{waba}' and revogada_em is null"));
        Assert.Equal("token-novo-EAAG9876543210fedcba", (await new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync(amb.TenantId, numero, default))!.Token);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Credencial_cifrada_com_outra_chave_nao_e_usada()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        var outraChave = new CifradorCredenciais(Enumerable.Repeat((byte)7, 32).ToArray());

        Assert.Null(await new RepositorioCredenciaisWhatsApp(amb.WorkerDs, outraChave).ObterParaEnvioAsync(amb.TenantId, numero, default));
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Operacoes_ficam_no_historico_com_o_operador_e_sem_o_token()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await NumeroProntoAsync(amb);

        foreach (var tipo in new[] { "conta_whatsapp_conectada", "credencial_whatsapp_registrada", "numero_whatsapp_registrado", "operacao_whatsapp_autorizada", "envio_whatsapp_habilitado" })
        {
            Assert.Equal(1L, await amb.ContarAsync($"eventos_historico where tenant_id = @tenant and tipo = '{tipo}' and dados->>'operador' = '{Operador}'"));
        }

        Assert.Equal(0L, await amb.ContarAsync($"eventos_historico where tenant_id = @tenant and dados::text like '%EAAG%'"));
    }

    [Fact]
    [Trait("Regra", "INV-15")]
    public async Task Credencial_nao_se_altera_nem_se_apaga()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (waba, _) = await NumeroProntoAsync(amb);

        foreach (var sql in new[] { $"update credenciais_whatsapp set chave_id = '0000000000000000' where waba_id = '{waba}'", $"delete from credenciais_whatsapp where waba_id = '{waba}'" })
        {
            var erro = await Assert.ThrowsAsync<PostgresException>(() => ComoDonoAsync(amb.TenantId, sql));
            Assert.Equal("P0001", erro.SqlState);
        }
    }

    // ------------------------------------------------------------------ isolamento

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Credencial_de_numero_de_outra_empresa_nao_sai()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var (_, numeroA) = await NumeroProntoAsync(a);

        Assert.Null(await new RepositorioCredenciaisWhatsApp(b.WorkerDs, Cifrador).ObterParaEnvioAsync(b.TenantId, numeroA, default));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Conta_de_uma_empresa_nao_e_tomada_por_outra()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var (waba, numero) = await NumeroProntoAsync(a);

        var conta = await Assert.ThrowsAsync<PostgresException>(() => OperacoesPlataforma.ConectarContaAsync(banco.ConnectionStringDono, b.TenantId, waba, null, Operador));
        var credencial = await Assert.ThrowsAsync<PostgresException>(() => OperacoesPlataforma.RegistrarCredencialAsync(banco.ConnectionStringDono, Cifrador, b.TenantId, waba, Token, Operador));
        var numeroDeA = await Assert.ThrowsAsync<PostgresException>(() => OperacoesPlataforma.RegistrarNumeroAsync(banco.ConnectionStringDono, b.TenantId, numero, null, Operador));
        var envio = await Assert.ThrowsAsync<PostgresException>(() => OperacoesPlataforma.AlterarEnvioAsync(banco.ConnectionStringDono, b.TenantId, numero, Operador, "x", false));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, conta.SqlState);
        Assert.Equal("P0001", credencial.SqlState);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, numeroDeA.SqlState);
        Assert.Equal("P0001", envio.SqlState);
        Assert.True(await Escalar<bool>($"select envio_habilitado from tenant_numeros_whatsapp where phone_number_id = '{numero}'"));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Operacao_da_plataforma_exige_a_empresa_da_transacao()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            ComoDonoAsync(b.TenantId, $"select plataforma_conectar_conta('{a.TenantId}', '{NovoId()}', null, 'x')"));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-10")]
    [InlineData("app_teste")]
    [InlineData("worker_teste")]
    [InlineData("supervisao_teste")]
    public async Task Nenhum_papel_da_aplicacao_le_a_tabela_de_credenciais(string usuario)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await NumeroProntoAsync(amb);
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(ds).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("select token_cifrado from credenciais_whatsapp", c, tx);
            return await cmd.ExecuteScalarAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    [Theory]
    [Trait("Regra", "INV-10")]
    [InlineData("app_teste")]
    [InlineData("supervisao_teste")]
    public async Task So_o_worker_pede_a_credencial_de_envio(string usuario)
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        await using var ds = banco.CriarDataSource(usuario, maxPool: 1);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(ds).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand($"select token_cifrado from credencial_envio('{numero}')", c, tx);
            return await cmd.ExecuteScalarAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    // ------------------------------------------------------------------ número de origem da conversa

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Resposta_sai_pelo_numero_em_que_o_cliente_escreveu_por_ultimo()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero1) = await NumeroProntoAsync(amb, habilitar: false);
        var (_, numero2) = await NumeroProntoAsync(amb, habilitar: false);
        await amb.ConhecimentoAsync("horario", SupervisaoApiTestes.Horario);
        var orquestrador = amb.Orquestrador(new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, SupervisaoApiTestes.Horario, "horario"))));

        await orquestrador.RegistrarAsync(amb.Mensagem("Oi") with { NumeroEmpresa = numero1 });
        amb.Relogio.Agora += TimeSpan.FromMinutes(1);
        await orquestrador.ProcessarAsync(amb.Mensagem("Qual o horário?") with { NumeroEmpresa = numero2 });
        // Mensagem antiga que chega fora de ordem não muda o número de saída.
        await orquestrador.RegistrarAsync(amb.Mensagem("Olá de novo", em: amb.Relogio.Agora - TimeSpan.FromMinutes(5)) with { NumeroEmpresa = numero1 });

        Assert.Equal(numero2, await amb.ConsultarAsync<string>("select numero_empresa from conversas where tenant_id = @tenant"));
        await amb.Worker().ProcessarProximoAsync();
        Assert.Equal(numero2, Assert.Single(amb.Gateway.NumerosOrigem));
    }

    [Fact]
    [Trait("Regra", "INV-10")]
    public async Task Conversa_nao_aponta_para_numero_de_outra_empresa()
    {
        await using var a = await AmbientePipeline.CriarAsync(banco);
        await using var b = await AmbientePipeline.CriarAsync(banco);
        var (_, numeroB) = await NumeroProntoAsync(b, habilitar: false);

        var erro = await Assert.ThrowsAsync<PostgresException>(() =>
            a.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).RegistrarAsync(a.Mensagem("Oi") with { NumeroEmpresa = numeroB }));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, erro.SqlState);
        Assert.Equal(0L, await a.ContarAsync("mensagens where tenant_id = @tenant"));
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Supervisao_nao_escolhe_o_numero_de_saida()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero1) = await NumeroProntoAsync(amb, habilitar: false);
        var (_, numero2) = await NumeroProntoAsync(amb, habilitar: false);
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).RegistrarAsync(amb.Mensagem("Oi") with { NumeroEmpresa = numero1 });

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand($"update conversas set numero_empresa = '{numero2}'", c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        // A supervisão nem tem permissão na coluna; o trigger do banco é a segunda barreira.
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
        Assert.Equal(numero1, await amb.ConsultarAsync<string>("select numero_empresa from conversas where tenant_id = @tenant"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Aplicacao_nao_apaga_o_numero_de_origem_da_conversa()
    {
        // Revisão da M1, G7: segunda barreira (trigger) do número de origem.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb, habilitar: false);
        await amb.Orquestrador(new MotorFalso(_ => Task.FromResult("{}"))).RegistrarAsync(amb.Mensagem("Oi") with { NumeroEmpresa = numero });

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.App).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("update conversas set numero_empresa = null", c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        }));

        Assert.Equal("P0001", erro.SqlState);
    }

    [Fact]
    [Trait("Regra", "INV-26")]
    public async Task Supervisao_nao_ve_as_contas_do_WhatsApp()
    {
        // Revisão da M1, S7: menor privilégio; nenhuma rota da supervisão usa as contas.
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        await NumeroProntoAsync(amb, habilitar: false);

        var erro = await Assert.ThrowsAsync<PostgresException>(() => new ExecutorTenant(amb.SupervisaoDs).ExecutarAsync(amb.TenantId, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand("select waba_id from contas_whatsapp", c, tx);
            return await cmd.ExecuteScalarAsync(ct);
        }));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, erro.SqlState);
    }

    // ------------------------------------------------------------------ worker com o gateway real (revisão da M1, G4)

    [Fact]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-7")]
    public async Task Resposta_por_numero_sem_envio_habilitado_e_cancelada_e_vai_para_supervisao_sem_chamar_a_Meta()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb, habilitar: false);
        var http = new HttpQueRegistra();
        var worker = await RespostaNaFilaAsync(amb, numero, new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = true },
            new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync));

        var r = await worker.ProcessarProximoAsync();

        Assert.Equal(Infraestrutura.Nucleo.ResultadoCiclo.Cancelada, r);
        Assert.Null(http.Uri);
        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
        Assert.Equal(1L, await amb.ContarAsync("outbox where tenant_id = @tenant and status = 'cancelada' and motivo like 'falha definitiva:%'"));
        Assert.Equal(0L, await amb.ContarAsync("mensagens where tenant_id = @tenant and direcao = 'saida'"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Falha_ao_ler_a_credencial_devolve_a_resposta_para_a_fila_sem_marcar_incerta()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        var http = new HttpQueRegistra();
        var worker = await RespostaNaFilaAsync(amb, numero, new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = true },
            (_, _, _) => throw new NpgsqlException("banco fora")));

        var r = await worker.ProcessarProximoAsync();

        Assert.Equal(Infraestrutura.Nucleo.ResultadoCiclo.DevolvidaParaFila, r);
        Assert.Null(http.Uri);
        Assert.Equal(1L, await amb.ContarAsync("outbox where tenant_id = @tenant and status = 'pendente'"));
        Assert.Equal(0L, await amb.ContarAsync("outbox where tenant_id = @tenant and status = 'incerta'"));
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Sem_chave_das_credenciais_a_resposta_e_cancelada_e_vai_para_supervisao()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        var worker = await RespostaNaFilaAsync(amb, numero, new GatewaySemCredenciais());

        Assert.Equal(Infraestrutura.Nucleo.ResultadoCiclo.Cancelada, await worker.ProcessarProximoAsync());
        Assert.Equal("TransferenciaPendente", await amb.EstadoConversaAsync());
    }

    // ------------------------------------------------------------------ gateway com o cadastro real

    [Fact]
    [Trait("Regra", "INV-6")]
    [Trait("Regra", "INV-10")]
    public async Task Gateway_envia_pelo_numero_da_conversa_com_a_credencial_da_empresa()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        var http = new HttpQueRegistra();
        var gateway = new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = true },
            new RepositorioCredenciaisWhatsApp(amb.WorkerDs, Cifrador).ObterParaEnvioAsync);

        var r = await gateway.EnviarTextoAsync(amb.TenantId, numero, "5511999990000", "Olá", default);

        Assert.Equal(SituacaoEnvio.Enviada, r.Situacao);
        Assert.Equal("Bearer " + Token, http.Autorizacao);
        Assert.EndsWith("/" + numero + "/messages", http.Uri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Gateway_com_o_interruptor_geral_desligado_nao_le_credencial_nem_chama_a_Meta()
    {
        await using var amb = await AmbientePipeline.CriarAsync(banco);
        var (_, numero) = await NumeroProntoAsync(amb);
        var http = new HttpQueRegistra();
        var consultas = 0;
        var gateway = new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = false },
            (_, _, _) => { consultas++; return Task.FromResult<CredencialEnvio?>(null); });

        var r = await gateway.EnviarTextoAsync(amb.TenantId, numero, "5511999990000", "Olá", default);

        Assert.Equal(SituacaoEnvio.FalhaDefinitiva, r.Situacao);
        Assert.Equal(0, consultas);
        Assert.Null(http.Uri);
    }

    [Fact]
    [Trait("Regra", "INV-6")]
    public async Task Falha_ao_ler_a_credencial_e_nao_enviada_e_nao_incerta()
    {
        var http = new HttpQueRegistra();
        var gateway = new GatewayWhatsAppCloud(new HttpClient(http), new OpcoesWhatsApp { EnvioHabilitado = true },
            (_, _, _) => throw new NpgsqlException("banco fora"));

        var r = await gateway.EnviarTextoAsync(Guid.NewGuid(), "123456", "5511999990000", "Olá", default);

        Assert.Equal(SituacaoEnvio.NaoEnviada, r.Situacao);
        Assert.Null(http.Uri);
    }

    // ------------------------------------------------------------------ auxiliares

    private static string NovoId() => "9" + Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(CultureInfo.InvariantCulture);

    /// <summary>Conta conectada com credencial, número ligado e, se pedido, autorizado e com envio habilitado.</summary>
    private async Task<(string Waba, string Numero)> NumeroProntoAsync(AmbientePipeline amb, bool autorizar = true, bool habilitar = true)
    {
        var waba = NovoId();
        var numero = NovoId();
        var dono = banco.ConnectionStringDono;
        await OperacoesPlataforma.ConectarContaAsync(dono, amb.TenantId, waba, null, Operador);
        await OperacoesPlataforma.RegistrarCredencialAsync(dono, Cifrador, amb.TenantId, waba, Token, Operador);
        await OperacoesPlataforma.RegistrarNumeroAsync(dono, amb.TenantId, numero, waba, Operador);
        if (autorizar)
        {
            await OperacoesPlataforma.AutorizarOperacaoAsync(dono, amb.TenantId, numero, Operador, "cliente conferido", true);
        }

        if (habilitar)
        {
            await OperacoesPlataforma.AlterarEnvioAsync(dono, amb.TenantId, numero, Operador, "autorizado para teste", true);
        }

        return (waba, numero);
    }

    /// <summary>Resposta da IA aprovada pelo backend e na fila de envio, numa conversa pelo número dado; devolve o worker.</summary>
    private static async Task<Infraestrutura.Nucleo.ProcessadorEnvio> RespostaNaFilaAsync(AmbientePipeline amb, string numero, IGatewayWhatsApp gateway)
    {
        await amb.ConhecimentoAsync("horario", SupervisaoApiTestes.Horario);
        var motor = new MotorFalso(ctx => Task.FromResult(MotorFalso.Rascunho(ctx, SupervisaoApiTestes.Horario, "horario")));
        await amb.Orquestrador(motor).ProcessarAsync(amb.Mensagem("Qual o horário?") with { NumeroEmpresa = numero });
        Assert.Equal(1L, await amb.ContarAsync("outbox where tenant_id = @tenant and status = 'pendente'"));
        return new Infraestrutura.Nucleo.ProcessadorEnvio(amb.WorkerDs, gateway, amb.Relogio, amb.Cifrador);
    }

    private async Task<T> Escalar<T>(string sql) => (T)(await banco.ConsultarComoAdminAsync(sql))!;

    private async Task ComoDonoAsync(Guid tenant, string sql)
    {
        await using var ds = NpgsqlDataSource.Create(banco.ConnectionStringDono);
        await new ExecutorTenant(ds).ExecutarAsync(tenant, async (c, tx, ct) =>
        {
            await using var cmd = new NpgsqlCommand(sql, c, tx);
            return await cmd.ExecuteNonQueryAsync(ct);
        });
    }

    private sealed class HttpQueRegistra : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Autorizacao { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Autorizacao = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"messages":[{"id":"wamid.m1"}]}""", Encoding.UTF8, "application/json") });
        }
    }
}
