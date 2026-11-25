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
    /// <summary>Mesmo wamid já processado (reenvio do webhook).</summary>
    Duplicada,

    /// <summary>Conversa não está em modo automático, ou mensagem anterior à liberação: a IA não age.</summary>
    SemAcaoDaIa,

    /// <summary>Resposta aprovada pelo validador e colocada na fila de envio.</summary>
    RespostaEnfileirada,

    /// <summary>Resposta bloqueada (ou impossível): automação interrompida, supervisão acionada.</summary>
    Transferida,
}

/// <summary>
/// Orquestra uma mensagem recebida (operacao-chatbot.md, seção 2; plano técnico, seção 4):
/// mascara e cifra dados protegidos, grava, monta o contexto mínimo, pede o rascunho ao motor de IA
/// FORA de qualquer transação, valida no backend sobre o estado ATUAL e só então enfileira o envio.
/// Bloqueio nunca gera nova tentativa: a conversa vai para supervisão (invariante 7).
/// </summary>
public sealed class ProcessadorMensagemRecebida(
    NpgsqlDataSource dataSourceApp,
    IMotorIA motor,
    CifradorDadosProtegidos cifrador,
    TimeProvider relogio)
{
    public const int LimiteHistorico = 20;

    private readonly ExecutorTenant _executor = new(dataSourceApp);

    public async Task<ResultadoProcessamento> ProcessarAsync(MensagemRecebida msg, CancellationToken ct = default)
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

        // ---------- Transação 1: gravar e montar o contexto
        var preparo = await _executor.ExecutarAsync(msg.TenantId, async (c, tx, t) =>
        {
            var cliente = await RepositorioNucleo.ObterOuCriarClienteAsync(c, tx, msg.TenantId, msg.WaIdCliente, t).ConfigureAwait(false);
            var conversa = await RepositorioNucleo.ObterOuCriarConversaComTravaAsync(c, tx, msg.TenantId, cliente.Id, t).ConfigureAwait(false);

            var mensagemId = Guid.NewGuid();
            if (!await RepositorioNucleo.InserirMensagemAsync(c, tx, mensagemId, msg.TenantId, conversa.Id, "entrada", mascarado.Texto, msg.Wamid, msg.RecebidaEm, t).ConfigureAwait(false))
            {
                return Preparo.Duplicada;
            }

            foreach (var o in mascarado.Ocorrencias)
            {
                var dadoId = Guid.NewGuid();
                var categoria = o.Categoria.ToString();
                var dado = cifrador.Cifrar(o.Valor, new ContextoCifragem(msg.TenantId, dadoId, conversa.Id, mensagemId, categoria));
                await RepositorioNucleo.InserirDadoProtegidoAsync(c, tx, dadoId, msg.TenantId, conversa.Id, mensagemId, categoria, dado, t).ConfigureAwait(false);
            }

            conversa.RegistrarMensagemDoCliente(msg.RecebidaEm);
            await RepositorioNucleo.InserirEventoAsync(c, tx, msg.TenantId, conversa.Id, "mensagem_recebida",
                new { mensagem = mensagemId, dados_protegidos = mascarado.Ocorrencias.Select(o => o.Categoria.ToString()) }, t).ConfigureAwait(false);

            if (!conversa.IaPodeTratarMensagemRecebidaEm(msg.RecebidaEm))
            {
                await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, t).ConfigureAwait(false);
                return Preparo.SemAcao;
            }

            var politica = await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, msg.TenantId, t).ConfigureAwait(false);
            if (deteccaoFalhou || politica is null)
            {
                await TransferirAsync(c, tx, conversa, politica, deteccaoFalhou ? "deteccao_dados_protegidos_indisponivel" : "politica_ausente", t).ConfigureAwait(false);
                return Preparo.Transferida;
            }

            await RepositorioNucleo.SalvarConversaAsync(c, tx, conversa, t).ConfigureAwait(false);

            var agora = relogio.GetUtcNow();
            var elegiveis = (await RepositorioNucleo.ListarFontesCandidatasAsync(c, tx, conversa, t).ConfigureAwait(false))
                .Where(f => ElegibilidadeConhecimento.EhElegivel(f, politica, conversa, agora))
                .ToList();
            var historico = await RepositorioNucleo.ListarHistoricoAsync(c, tx, conversa.Id, LimiteHistorico, t).ConfigureAwait(false);

            var contexto = new ContextoModelo(
                historico,
                elegiveis.Select(f => new ConhecimentoContexto(f.ItemId, f.Versao, f.Assunto, f.Conteudo)).ToList(),
                [.. politica.AssuntosAutonomos]);

            return new Preparo(conversa.Id, conversa.VersaoEstado, contexto, elegiveis.Select(f => f.Chave).ToHashSet());
        }, ct).ConfigureAwait(false);

        if (preparo == Preparo.Duplicada)
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
            rascunhoJson = await motor.GerarRascunhoAsync(preparo.Contexto!, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Falha fechada: qualquer erro do motor é tratado como bloqueio.
        catch (Exception) when (!ct.IsCancellationRequested)
#pragma warning restore CA1031
        {
            rascunhoJson = null;
        }

        // ---------- Transação 2: validar sobre o estado ATUAL e decidir
        return await _executor.ExecutarAsync(msg.TenantId, async (c, tx, t) =>
        {
            var conversa = (await RepositorioNucleo.ObterConversaComTravaAsync(c, tx, preparo.ConversaId, t).ConfigureAwait(false))!;
            var politica = (await RepositorioNucleo.ObterPoliticaVigenteAsync(c, tx, msg.TenantId, t).ConfigureAwait(false))!;
            var rascunho = LeitorRascunho.TentarLer(rascunhoJson, out _);
            var citadas = rascunho?.Fontes ?? [];
            var fontesNoBanco = await RepositorioNucleo.ObterFontesAsync(c, tx, citadas.Distinct().ToList(), t).ConfigureAwait(false);
            var agora = relogio.GetUtcNow();

            var resultado = ValidadorEnvio.Validar(rascunho, new ContextoValidacao(
                conversa, preparo.VersaoLida, politica, preparo.Entregues!, fontesNoBanco, agora));

            var decisaoId = Guid.NewGuid();
            await RepositorioNucleo.InserirDecisaoAsync(c, tx, decisaoId, msg.TenantId, conversa.Id, conversa.VersaoEstado, "resposta",
                resultado.Aprovado, resultado.Motivo?.ToString(), resultado.Detalhe, politica.Versao, preparo.Entregues!, citadas,
                resultado.Aprovado ? rascunho!.Texto : null, t).ConfigureAwait(false);

            if (resultado.Aprovado)
            {
                await RepositorioNucleo.InserirOutboxAsync(c, tx, msg.TenantId, conversa.Id, decisaoId, rascunho!.Texto, agora + politica.ValidadeMensagemAutomatica, t).ConfigureAwait(false);
                await RepositorioNucleo.InserirEventoAsync(c, tx, msg.TenantId, conversa.Id, "resposta_aprovada", new { decisao = decisaoId }, t).ConfigureAwait(false);
                return ResultadoProcessamento.RespostaEnfileirada;
            }

            await RepositorioNucleo.InserirEventoAsync(c, tx, msg.TenantId, conversa.Id, "resposta_bloqueada",
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

    private sealed record Preparo(Guid ConversaId, long VersaoLida, ContextoModelo? Contexto, IReadOnlySet<ChaveFonte>? Entregues)
    {
        public static readonly Preparo Duplicada = new(Guid.Empty, -1, null, null);
        public static readonly Preparo SemAcao = new(Guid.Empty, -2, null, null);
        public static readonly Preparo Transferida = new(Guid.Empty, -3, null, null);
    }
}
