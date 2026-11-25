using Npgsql;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Contratos;
using Seceletesc.Dominio.DadosProtegidos;
using Seceletesc.Dominio.Envio;
using Seceletesc.Dominio.Respostas;
using Seceletesc.Infraestrutura.Banco;
using Seceletesc.Infraestrutura.Criptografia;

namespace Seceletesc.Infraestrutura.Nucleo;

public sealed record MensagemRecebida(Guid TenantId, string WaIdCliente, string Texto, string Wamid, DateTimeOffset RecebidaEm);

public enum ResultadoProcessamento
{
    /// <summary>Mesmo wamid já processado (reenvio do webhook), ou a mensagem já foi tratada.</summary>
    Duplicada,

    /// <summary>Conversa não está em modo automático, mensagem anterior à liberação, ou há mensagem mais nova: a IA não age.</summary>
    SemAcaoDaIa,

    /// <summary>Resposta aprovada pelo validador e colocada na fila de envio.</summary>
    RespostaEnfileirada,

    /// <summary>Resposta bloqueada (ou impossível): automação interrompida, supervisão acionada.</summary>
    Transferida,

    /// <summary>Mensagem registrada e na fila da IA (o trabalhador da IA trata depois).</summary>
    Registrada,
}

/// <summary>
/// Orquestra uma mensagem recebida (operacao-chatbot.md, seção 2; plano técnico, seções 4 e 4.8), em duas etapas:
/// <list type="number">
/// <item><see cref="RegistrarAsync"/> (no webhook): mascara e cifra dados protegidos, grava a mensagem (idempotente pelo
/// wamid) e, se a IA pode agir, põe na fila da IA. Rápido e durável.</item>
/// <item><see cref="TratarAsync"/> (no trabalhador da IA): monta o contexto mínimo, pede o rascunho ao motor de IA FORA
/// de qualquer transação, valida no backend sobre o estado ATUAL e só então enfileira o envio. A decisão sai uma única
/// vez: quem tira a mensagem da fila na transação da decisão decide.</item>
/// </list>
/// Bloqueio nunca gera nova tentativa: a conversa vai para supervisão (invariante 7).
/// </summary>
public sealed class ProcessadorMensagemRecebida(
    NpgsqlDataSource dataSourceApp,
    IMotorIA motor,
    CifradorDadosProtegidos cifrador,
    TimeProvider relogio,
    bool iaHabilitada = true,
    TimeSpan? idadeMaximaNaFila = null)
{
    public const int LimiteHistorico = 20;

    /// <summary>Tentativas de tratamento (lease vencido, erro) antes de desistir e acionar a supervisão.</summary>
    public const int MaximoTentativas = 3;

    /// <summary>Padrão de escalonamento por atraso (a política de cada empresa define o seu; ver PoliticaTenant).</summary>
    public static readonly TimeSpan IdadeMaximaNaFilaPadrao = TimeSpan.FromMinutes(15);

    /// <summary>Tempo máximo do motor de IA, abaixo do lease da fila (120 s).</summary>
    public static readonly TimeSpan TempoMaximoMotor = TimeSpan.FromSeconds(60);

    private readonly ExecutorTenant _executor = new(dataSourceApp);

    /// <summary>Registra e trata em seguida (uso direto, testes). O webhook usa só <see cref="RegistrarAsync"/>.</summary>
    public async Task<ResultadoProcessamento> ProcessarAsync(MensagemRecebida msg, CancellationToken ct = default)
    {
        var (resultado, mensagemId) = await RegistrarAsync(msg, ct).ConfigureAwait(false);
        return resultado == ResultadoProcessamento.Registrada
            ? await TratarAsync(msg.TenantId, mensagemId!.Value, ct).ConfigureAwait(false)
            : resultado;
    }

    /// <summary>Etapa 1: grava a mensagem do cliente e, se a IA pode agir, põe na fila da IA.</summary>
    public async Task<(ResultadoProcessamento Resultado, Guid? MensagemId)> RegistrarAsync(MensagemRecebida msg, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(msg);

        // Dados protegidos saem antes de qualquer gravação. Se a detecção falhar, o texto inteiro é tratado
        // como protegido e a IA não participa (falha fechada).
        TextoMascarado mascarado;
        var deteccaoFalhou = false;
        try
        {
            mascarado = DetectorDadosProtegidos.Mascarar(msg.Texto);
        }
        catch (Exception e) when (e is System.Text.RegularExpressions.RegexMatchTimeoutException or PlatformNotSupportedException)
        {
            deteccaoFalhou = true;
            mascarado = new TextoMascarado(
                "[CONTEÚDO RETIDO: verificação de dados protegidos indisponível]",
                [new OcorrenciaDadoProtegido(CategoriaDadoProtegido.Documento, 0, msg.Texto.Length, msg.Texto)]);
        }

        return await _executor.ExecutarAsync(msg.TenantId, async (c, tx, t) =>
        {
            var cliente = await RepositorioNucleo.ObterOuCriarClienteAsync(c, tx, msg.TenantId, msg.WaIdCliente, t).ConfigureAwait(false);
            var conversa = await RepositorioNucleo.ObterOuCriarConversaComTravaAsync(c, tx, msg.TenantId, cliente.Id, t).ConfigureAwait(false);

            var mensagemId = Guid.NewGuid();
            if (!await RepositorioNucleo.InserirMensagemAsync(c, tx, mensagemId, msg.TenantId, conversa.Id, "entrada", mascarado.Texto, msg.Wamid, msg.RecebidaEm, t).ConfigureAwait(false))
            {
                return (ResultadoProcessamento.Duplicada, (Guid?)null);
            }

            await CifrarAsync(c, tx, msg.TenantId, conversa.Id, mensagemId, mascarado, t).ConfigureAwait(false);

            conversa.RegistrarMensagemDoCliente(msg.RecebidaEm);
            await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, t).ConfigureAwait(false);
            await RepositorioNucleo.InserirEventoAsync(c, tx, msg.TenantId, conversa.Id, "mensagem_recebida",
                new { mensagem = mensagemId, dados_protegidos = mascarado.Ocorrencias.Select(o => o.Categoria.ToString()) }, t).ConfigureAwait(false);

            if (!conversa.IaPodeTratarMensagemRecebidaEm(msg.RecebidaEm))
            {
                return (ResultadoProcessamento.SemAcaoDaIa, (Guid?)null);
            }

            // Processamento da IA desligado: ninguém responderia. A mensagem vai para um humano já (revisão da F4, S4).
            if (!iaHabilitada)
            {
                var politicaAtual = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, msg.TenantId, t).ConfigureAwait(false);
                await TransferirAsync(c, tx, conversa, politicaAtual, "processamento_da_ia_desligado", t).ConfigureAwait(false);
                return (ResultadoProcessamento.Transferida, (Guid?)null);
            }

            await using (var fila = RepositorioNucleo.Cmd(c, tx, """
                insert into fila_ia (mensagem_id, tenant_id, conversa_id, deteccao_falhou) values (@m, @t, @c, @d)
                """))
            {
                fila.Parameters.AddWithValue("m", mensagemId);
                fila.Parameters.AddWithValue("t", msg.TenantId);
                fila.Parameters.AddWithValue("c", conversa.Id);
                fila.Parameters.AddWithValue("d", deteccaoFalhou);
                await fila.ExecuteNonQueryAsync(t).ConfigureAwait(false);
            }

            return (ResultadoProcessamento.Registrada, (Guid?)mensagemId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Etapa 2: trata uma mensagem da fila da IA. Seguro para repetir (queda no meio, lease vencido): sem a linha na fila,
    /// não decide de novo. Só a mensagem mais recente da conversa é respondida; as anteriores saem da fila sem resposta
    /// (a mais nova é tratada com o histórico completo).
    /// </summary>
    public async Task<ResultadoProcessamento> TratarAsync(Guid tenantId, Guid mensagemId, CancellationToken ct = default)
    {
        // ---------- Transação 1: conferir e montar o contexto
        var preparo = await _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            var item = await ItemDaFilaAsync(c, tx, mensagemId, t).ConfigureAwait(false);
            if (item is null)
            {
                return Preparo.JaTratada;
            }

            var conversa = (await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, item.Value.ConversaId, t).ConfigureAwait(false))!;
            if (!conversa.IaPodeTratarMensagemRecebidaEm(item.Value.RecebidaEm))
            {
                await TirarDaFilaAsync(c, tx, mensagemId, t).ConfigureAwait(false);
                return Preparo.SemAcao;
            }

            var politica = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, tenantId, t).ConfigureAwait(false);

            // Falha na detecção de dados protegidos vai para um humano antes de qualquer agrupamento: o pedido não pode
            // sumir dentro da resposta a outra mensagem (revisão da F4.5, G4).
            if (item.Value.DeteccaoFalhou)
            {
                await TirarDaFilaAsync(c, tx, mensagemId, t).ConfigureAwait(false);
                await TransferirAsync(c, tx, conversa, politica, "deteccao_dados_protegidos_indisponivel", t).ConfigureAwait(false);
                return Preparo.Transferida;
            }

            if (await TratarMensagemMaisNovaAsync(c, tx, conversa, politica, mensagemId, t).ConfigureAwait(false) is { } desfecho)
            {
                return desfecho == ResultadoProcessamento.Transferida ? Preparo.Transferida : Preparo.SemAcao;
            }

            var (maisNovaConhecida, _) = await MensagensMaisNovasAsync(c, tx, conversa.Id, mensagemId, t).ConfigureAwait(false);

            // Sem resposta automática: falha de detecção, sem política, tentativas esgotadas ou atraso acima do limite de
            // escalonamento da política (resposta atrasada não é resposta pertinente). Tudo vai para um humano.
            var atraso = relogio.GetUtcNow() - item.Value.RecebidaEm;
            var limiteEscalonamento = idadeMaximaNaFila ?? politica?.AtrasoEscalonamentoFila ?? IdadeMaximaNaFilaPadrao;
            var motivo = item.Value.DeteccaoFalhou ? "deteccao_dados_protegidos_indisponivel"
                : politica is null ? "politica_ausente"
                : item.Value.Tentativas > MaximoTentativas ? "tentativas_esgotadas"
                : atraso > limiteEscalonamento ? "mensagem_atrasada_escalonada"
                : null;

            // Monitoramento: atraso acima do limite de aviso fica registrado, mesmo quando a IA ainda responde.
            if (motivo is null && politica is not null && atraso > politica.AtrasoAvisoFila)
            {
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "mensagem_atrasada",
                    new { mensagem = mensagemId, atraso_segundos = (int)atraso.TotalSeconds }, t).ConfigureAwait(false);
            }
            if (motivo is not null || politica is null)
            {
                await TirarDaFilaAsync(c, tx, mensagemId, t).ConfigureAwait(false);
                await TransferirAsync(c, tx, conversa, politica, motivo ?? "politica_ausente", t).ConfigureAwait(false);
                return Preparo.Transferida;
            }

            var agora = relogio.GetUtcNow();
            var elegiveis = (await RepositorioNucleo.ListarFontesCandidatasAsync(c, tx, conversa, t).ConfigureAwait(false))
                .Where(f => ElegibilidadeConhecimento.EhElegivel(f, politica, conversa, agora))
                .ToList();
            var historico = await RepositorioNucleo.ListarHistoricoAsync(c, tx, conversa.Id, LimiteHistorico, t).ConfigureAwait(false);

            var contexto = new ContextoModelo(
                historico,
                elegiveis.Select(f => new ConhecimentoContexto(f.ItemId, f.Versao, f.Assunto, f.Conteudo)).ToList(),
                [.. politica.AssuntosAutonomos],
                item.Value.TextoMascarado);

            return new Preparo(conversa.Id, conversa.VersaoEstado, contexto, elegiveis.Select(f => f.Chave).ToHashSet(), politica, maisNovaConhecida);
        }, ct).ConfigureAwait(false);

        if (preparo == Preparo.JaTratada)
        {
            return ResultadoProcessamento.Duplicada;
        }

        if (preparo == Preparo.SemAcao)
        {
            return ResultadoProcessamento.SemAcaoDaIa;
        }

        if (preparo == Preparo.Transferida)
        {
            return ResultadoProcessamento.Transferida;
        }

        // ---------- Motor de IA, fora de transação. Qualquer falha vira rascunho inválido (bloqueio).
        string? rascunhoJson;
        try
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(TempoMaximoMotor);
            rascunhoJson = await motor.GerarRascunhoAsync(preparo.Contexto!, limite.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Falha fechada: qualquer erro do motor é tratado como bloqueio.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            rascunhoJson = null;
        }

        // ---------- Transação 2: validar sobre o estado ATUAL e decidir
        return await _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            var conversa = (await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, preparo.ConversaId, t).ConfigureAwait(false))!;

            // Exatamente uma decisão: quem tira da fila decide. Outra execução (lease vencido) já decidiu.
            if (!await TirarDaFilaAsync(c, tx, mensagemId, t).ConfigureAwait(false))
            {
                return ResultadoProcessamento.Duplicada;
            }

            // Chegou mensagem mais nova durante a geração.
            var politicaAtual = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, tenantId, t).ConfigureAwait(false);
            if (await TratarMensagemMaisNovaAsync(c, tx, conversa, politicaAtual, mensagemId, t, jaFoiTiradaDaFila: true, maisNovaConhecida: preparo.MaisNovaConhecida).ConfigureAwait(false) is { } desfecho)
            {
                return desfecho;
            }

            var politica = politicaAtual ?? preparo.Politica!;
            var rascunho = LeitorRascunho.TentarLer(rascunhoJson, out _);
            var citadas = rascunho?.Fontes ?? [];
            var fontesNoBanco = await RepositorioNucleo.ObterFontesAsync(c, tx, citadas.Distinct().ToList(), t).ConfigureAwait(false);
            var agora = relogio.GetUtcNow();

            var resultado = ValidadorEnvio.Validar(rascunho, new ContextoValidacao(
                conversa, preparo.VersaoLida, politica, preparo.Entregues!, fontesNoBanco, agora));

            var decisaoId = Guid.NewGuid();
            await RepositorioNucleo.InserirDecisaoAsync(c, tx, decisaoId, tenantId, conversa.Id, conversa.VersaoEstado, "resposta",
                resultado.Aprovado, resultado.Motivo?.ToString(), resultado.Detalhe, politica.Versao, preparo.Entregues!, citadas,
                resultado.Aprovado ? rascunho!.Texto : null, t).ConfigureAwait(false);

            if (resultado.Aprovado)
            {
                await RepositorioNucleo.InserirOutboxAsync(c, tx, tenantId, conversa.Id, decisaoId, rascunho!.Texto, agora + politica.ValidadeMensagemAutomatica, t).ConfigureAwait(false);
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "resposta_aprovada", new { decisao = decisaoId }, t).ConfigureAwait(false);
                return ResultadoProcessamento.RespostaEnfileirada;
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "resposta_bloqueada",
                new { decisao = decisaoId, motivo = resultado.Motivo?.ToString() }, t).ConfigureAwait(false);

            // Bloqueio por estado mudado: um humano interveio durante a geração. Se assumiu, a conversa já é dele;
            // se liberou, a liberação explícita prevalece (invariante 5) e a mensagem, anterior à liberação, não é
            // tratada pela IA (plano 4.1). Nos demais bloqueios, a automação é interrompida (invariante 7).
            if (conversa.Estado == Dominio.Conversas.EstadoConversa.Automatico && resultado.Motivo != MotivoBloqueio.EstadoMudou)
            {
                await TransferirAsync(c, tx, conversa, politica, "resposta_bloqueada:" + resultado.Motivo, t).ConfigureAwait(false);
            }

            return ResultadoProcessamento.Transferida;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Mensagem que não é texto (mídia, áudio, localização, reação...). Padrão do plano (lacuna funcional): a IA não
    /// trata; a mensagem fica registrada (só o tipo, nunca o conteúdo) e a conversa vai para supervisão.
    /// </summary>
    public Task<ResultadoProcessamento> RegistrarNaoTextoAsync(Guid tenantId, string waIdCliente, string tipo, string wamid, DateTimeOffset recebidaEm, CancellationToken ct = default) =>
        _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            var cliente = await RepositorioNucleo.ObterOuCriarClienteAsync(c, tx, tenantId, waIdCliente, t).ConfigureAwait(false);
            var conversa = await RepositorioNucleo.ObterOuCriarConversaComTravaAsync(c, tx, tenantId, cliente.Id, t).ConfigureAwait(false);
            var mensagemId = Guid.NewGuid();
            var tipoSeguro = TipoSeguro(tipo);
            if (!await RepositorioNucleo.InserirMensagemAsync(c, tx, mensagemId, tenantId, conversa.Id, "entrada", tipoSeguro == "reaction" ? TextoReacao : $"[mensagem não textual: {tipoSeguro}]", wamid, recebidaEm, t).ConfigureAwait(false))
            {
                return ResultadoProcessamento.Duplicada;
            }

            // Reação (decisão do Victor, 2026-11-25): registrada, sem intervenção humana nem resposta da IA. Não conta como
            // nova mensagem do cliente para a janela de 24h (conservador: a janela só abre com mensagem).
            if (tipoSeguro == "reaction")
            {
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "reacao_registrada", new { mensagem = mensagemId }, t).ConfigureAwait(false);
                return ResultadoProcessamento.SemAcaoDaIa;
            }

            conversa.RegistrarMensagemDoCliente(recebidaEm);
            await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversa.Id, "mensagem_recebida", new { mensagem = mensagemId, tipo = tipoSeguro }, t).ConfigureAwait(false);
            if (conversa.Estado != Dominio.Conversas.EstadoConversa.Automatico)
            {
                await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, t).ConfigureAwait(false);
                return ResultadoProcessamento.SemAcaoDaIa;
            }

            var politica = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, tenantId, t).ConfigureAwait(false);
            await TransferirAsync(c, tx, conversa, politica, "mensagem_nao_textual:" + tipoSeguro, t).ConfigureAwait(false);
            return ResultadoProcessamento.Transferida;
        }, ct);

    /// <summary>
    /// Eco do WhatsApp Business App (coexistência): um humano respondeu pelo celular. Registra no histórico (mascarado
    /// e cifrado) e coloca a conversa sob controle humano (invariante 24). Não mexe na janela de 24h.
    /// </summary>
    public Task<ResultadoProcessamento> RegistrarEcoAsync(Guid tenantId, string waIdCliente, string? texto, string tipo, string wamid, DateTimeOffset enviadaEm, CancellationToken ct = default) =>
        _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            var cliente = await RepositorioNucleo.ObterOuCriarClienteAsync(c, tx, tenantId, waIdCliente, t).ConfigureAwait(false);
            var conversa = await RepositorioNucleo.ObterOuCriarConversaComTravaAsync(c, tx, tenantId, cliente.Id, t).ConfigureAwait(false);
            var mascarado = texto is null
                ? new TextoMascarado($"[mensagem não textual pelo app: {TipoSeguro(tipo)}]", [])
                : MascararComFalhaFechada(texto);
            var mensagemId = Guid.NewGuid();
            if (!await RepositorioNucleo.InserirMensagemAsync(c, tx, mensagemId, tenantId, conversa.Id, "saida_app", mascarado.Texto, wamid, enviadaEm, t).ConfigureAwait(false))
            {
                return ResultadoProcessamento.Duplicada;
            }

            await CifrarAsync(c, tx, tenantId, conversa.Id, mensagemId, mascarado, t).ConfigureAwait(false);
            await using (var eco = RepositorioNucleo.Cmd(c, tx, "select registrar_eco_humano(@c, @w)"))
            {
                eco.Parameters.AddWithValue("c", conversa.Id);
                eco.Parameters.AddWithValue("w", wamid);
                await eco.ExecuteScalarAsync(t).ConfigureAwait(false);
            }

            return ResultadoProcessamento.SemAcaoDaIa;
        }, ct);

    /// <summary>
    /// Status de entrega de uma mensagem enviada (idempotente). Falha de entrega de uma mensagem nossa aciona a
    /// supervisão: o cliente não recebeu e alguém precisa saber.
    /// </summary>
    public Task<bool> RegistrarStatusAsync(Guid tenantId, string wamid, string status, DateTimeOffset em, string? erroCodigo, CancellationToken ct = default) =>
        _executor.ExecutarAsync(tenantId, async (c, tx, t) =>
        {
            await using (var cmd = RepositorioNucleo.Cmd(c, tx, """
                insert into status_entrega (tenant_id, wamid, status, ocorrido_em, erro_codigo) values (@t, @w, @s, @em, @e)
                on conflict do nothing
                """))
            {
                cmd.Parameters.AddWithValue("t", tenantId);
                cmd.Parameters.AddWithValue("w", wamid);
                cmd.Parameters.AddWithValue("s", status);
                cmd.Parameters.AddWithValue("em", em);
                cmd.Parameters.AddWithValue("e", (object?)erroCodigo ?? DBNull.Value);
                if (await cmd.ExecuteNonQueryAsync(t).ConfigureAwait(false) == 0)
                {
                    return false; // repetido
                }
            }

            if (status != "failed")
            {
                return true;
            }

            await using var conv = RepositorioNucleo.Cmd(c, tx, "select conversa_id from outbox where wamid = @w limit 1");
            conv.Parameters.AddWithValue("w", wamid);
            if (await conv.ExecuteScalarAsync(t).ConfigureAwait(false) is Guid conversaId)
            {
                await RepositorioNucleo.InserirEventoAsync(c, tx, tenantId, conversaId, "entrega_falhou", new { wamid, erro = erroCodigo }, t).ConfigureAwait(false);
                await using var transferir = RepositorioNucleo.Cmd(c, tx, "select transferir_para_supervisao(@c, 'falha_entrega')");
                transferir.Parameters.AddWithValue("c", conversaId);
                await transferir.ExecuteScalarAsync(t).ConfigureAwait(false);
            }

            return true;
        }, ct);

    /// <summary>Tipo vindo do webhook vai para o histórico só se for um identificador simples.</summary>
    private static string TipoSeguro(string tipo) =>
        tipo.Length is > 0 and <= 30 && tipo.All(ch => ch is (>= 'a' and <= 'z') or '_') ? tipo : "desconhecido";

    private static TextoMascarado MascararComFalhaFechada(string texto)
    {
        try
        {
            return DetectorDadosProtegidos.Mascarar(texto);
        }
        catch (Exception e) when (e is System.Text.RegularExpressions.RegexMatchTimeoutException or PlatformNotSupportedException)
        {
            return new TextoMascarado("[CONTEÚDO RETIDO: verificação de dados protegidos indisponível]",
                [new OcorrenciaDadoProtegido(CategoriaDadoProtegido.Documento, 0, texto.Length, texto)]);
        }
    }

    private async Task CifrarAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, Guid conversaId, Guid mensagemId, TextoMascarado mascarado, CancellationToken ct)
    {
        foreach (var o in mascarado.Ocorrencias)
        {
            var dadoId = Guid.NewGuid();
            var categoria = o.Categoria.ToString();
            var dado = cifrador.Cifrar(o.Valor, new ContextoCifragem(tenantId, dadoId, conversaId, mensagemId, categoria));
            await RepositorioNucleo.InserirDadoProtegidoAsync(c, tx, dadoId, tenantId, conversaId, mensagemId, categoria, dado, ct).ConfigureAwait(false);
        }
    }

    private static async Task<(Guid ConversaId, bool DeteccaoFalhou, DateTimeOffset RecebidaEm, int Tentativas, string TextoMascarado)?> ItemDaFilaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid mensagemId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            select f.conversa_id, f.deteccao_falhou, m.ocorrida_em, f.tentativas, m.texto_mascarado
              from fila_ia f join mensagens m on m.id = f.mensagem_id and m.tenant_id = f.tenant_id
             where f.mensagem_id = @m and f.tenant_id = tenant_atual()
            """);
        cmd.Parameters.AddWithValue("m", mensagemId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false)
            ? (r.GetGuid(0), r.GetBoolean(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt32(3), r.GetString(4))
            : null;
    }

    private static async Task<bool> TirarDaFilaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid mensagemId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, "delete from fila_ia where mensagem_id = @m and tenant_id = tenant_atual()");
        cmd.Parameters.AddWithValue("m", mensagemId);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Há mensagem do cliente mais nova que esta? Nenhuma mensagem sai do fluxo em silêncio (decisões do Victor,
    /// 2026-11-25):
    /// <list type="bullet">
    /// <item>se a mais nova ainda está na fila, esta é agrupada a ela (a resposta à mais nova é gerada com esta no
    /// histórico e passa pela validação); fica registrado no histórico;</item>
    /// <item>se a mais nova já foi tratada (chegada fora de ordem), esta é reconciliada: tratada agora, com o histórico
    /// completo (inclusive a resposta já dada), e o fato fica registrado. Se a validação bloquear (ambiguidade, falta de
    /// fonte), vai para supervisão como qualquer bloqueio.</item>
    /// </list>
    /// Nulo quando esta mensagem deve seguir para o motor.
    /// </summary>
    /// <summary>Marca com que a reação é gravada: não é fala do cliente (não agrupa, não vai ao motor).</summary>
    public const string TextoReacao = "[mensagem não textual: reaction]";

    private static async Task<(Guid? MaisNova, bool AlgumaPendente)> MensagensMaisNovasAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, Guid mensagemId, CancellationToken ct)
    {
        await using var cmd = RepositorioNucleo.Cmd(c, tx, """
            select (select n.id from mensagens n join mensagens m on m.id = @m
                     where n.conversa_id = @c and n.direcao = 'entrada' and n.id <> m.id and n.texto_mascarado <> @reacao
                       and (n.ocorrida_em, n.criada_em) > (m.ocorrida_em, m.criada_em)
                     order by n.ocorrida_em desc, n.criada_em desc limit 1),
                   exists (select 1 from mensagens n join mensagens m on m.id = @m join fila_ia f on f.mensagem_id = n.id
                            where n.conversa_id = @c and n.direcao = 'entrada' and n.id <> m.id
                              and (n.ocorrida_em, n.criada_em) > (m.ocorrida_em, m.criada_em))
            """);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("m", mensagemId);
        cmd.Parameters.AddWithValue("reacao", TextoReacao);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return (r.IsDBNull(0) ? null : r.GetGuid(0), r.GetBoolean(1));
    }

    /// <summary>
    /// Há mensagem do cliente mais nova que esta? Nenhuma mensagem sai do fluxo em silêncio (decisões do Victor,
    /// 2026-11-25). Reações não contam.
    /// <list type="bullet">
    /// <item>se alguma mais nova ainda está na fila, esta é agrupada (a resposta à mais nova é gerada com esta no
    /// histórico e passa pela validação); fica registrado;</item>
    /// <item>se a mais nova já foi tratada (chegada fora de ordem), esta é reconciliada: tratada agora, como mensagem a
    /// responder, com o histórico completo; fica registrado;</item>
    /// <item>na transação da decisão, se uma mais nova foi respondida DURANTE a geração (o contexto não a viu), a
    /// resposta não sai: supervisão (ambiguidade).</item>
    /// </list>
    /// Nulo quando esta mensagem segue para o motor (ou para a decisão).
    /// </summary>
    private async Task<ResultadoProcessamento?> TratarMensagemMaisNovaAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Dominio.Conversas.Conversa conversa, Dominio.Politicas.PoliticaTenant? politica, Guid mensagemId,
        CancellationToken ct, bool jaFoiTiradaDaFila = false, Guid? maisNovaConhecida = null)
    {
        var (maisNova, algumaPendente) = await MensagensMaisNovasAsync(c, tx, conversa.Id, mensagemId, ct).ConfigureAwait(false);
        if (maisNova is null && !algumaPendente)
        {
            return null;
        }

        if (algumaPendente)
        {
            if (!jaFoiTiradaDaFila)
            {
                await TirarDaFilaAsync(c, tx, mensagemId, ct).ConfigureAwait(false);
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, conversa.TenantId, conversa.Id, "mensagem_agrupada",
                new { mensagem = mensagemId, respondida_com = maisNova }, ct).ConfigureAwait(false);
            await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, ct).ConfigureAwait(false);
            return ResultadoProcessamento.SemAcaoDaIa;
        }

        if (!jaFoiTiradaDaFila)
        {
            // Fora de ordem: segue para o motor (reconciliação), com registro.
            await RepositorioNucleo.InserirEventoAsync(c, tx, conversa.TenantId, conversa.Id, "mensagem_fora_de_ordem",
                new { mensagem = mensagemId, mais_nova = maisNova }, ct).ConfigureAwait(false);
            return null;
        }

        if (maisNova == maisNovaConhecida)
        {
            return null; // já era fora de ordem ao montar o contexto: o contexto a viu
        }

        // Uma mensagem mais nova foi respondida enquanto esta era gerada: a resposta não considerou a outra.
        await TransferirAsync(c, tx, conversa, politica, "mensagem_mais_nova_respondida_durante_a_geracao", ct).ConfigureAwait(false);
        return ResultadoProcessamento.Transferida;
    }

    /// <summary>Interrompe a automação (Automatico → TransferenciaPendente) e enfileira o aviso fixo, se houver.</summary>
    private async Task TransferirAsync(NpgsqlConnection c, NpgsqlTransaction tx, Dominio.Conversas.Conversa conversa, Dominio.Politicas.PoliticaTenant? politica, string motivo, CancellationToken ct)
    {
        if (conversa.Estado != Dominio.Conversas.EstadoConversa.Automatico)
        {
            await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, ct).ConfigureAwait(false);
            return;
        }

        var transicao = conversa.IniciarTransferencia(motivo);
        await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, ct).ConfigureAwait(false);
        await RepositorioNucleo.InserirEventoAsync(c, tx, conversa.TenantId, conversa.Id, "transferencia_iniciada",
            new { motivo, versao = transicao.NovaVersao }, ct).ConfigureAwait(false);

        if (politica is null)
        {
            return;
        }

        var aviso = ValidadorEnvio.ValidarAvisoTransferencia(conversa, transicao.NovaVersao, politica);
        var decisaoId = Guid.NewGuid();
        await RepositorioNucleo.InserirDecisaoAsync(c, tx, decisaoId, conversa.TenantId, conversa.Id, conversa.VersaoEstado, "aviso_transferencia",
            aviso.Aprovado, aviso.Motivo?.ToString(), aviso.Detalhe, politica.Versao, [], [],
            aviso.Aprovado ? politica.TextoAvisoTransferencia : null, ct).ConfigureAwait(false);
        if (aviso.Aprovado)
        {
            await RepositorioNucleo.InserirOutboxAsync(c, tx, conversa.TenantId, conversa.Id, decisaoId, politica.TextoAvisoTransferencia,
                relogio.GetUtcNow() + politica.ValidadeMensagemAutomatica, ct).ConfigureAwait(false);
        }
    }

    private sealed record Preparo(Guid ConversaId, long VersaoLida, ContextoModelo? Contexto, IReadOnlySet<ChaveFonte>? Entregues, Dominio.Politicas.PoliticaTenant? Politica = null, Guid? MaisNovaConhecida = null)
    {
        public static readonly Preparo JaTratada = new(Guid.Empty, -1, null, null);
        public static readonly Preparo SemAcao = new(Guid.Empty, -2, null, null);
        public static readonly Preparo Transferida = new(Guid.Empty, -3, null, null);
    }
}
