namespace Seceletesc.Dominio.Conversas;

/// <summary>Quem pode falar com o cliente. Plano técnico, seção 4.1.</summary>
public enum EstadoConversa
{
    /// <summary>A IA responde, sempre via validador do backend.</summary>
    Automatico,

    /// <summary>Automação interrompida; só o aviso de transferência pode sair.</summary>
    TransferenciaPendente,

    /// <summary>Só o humano fala com o cliente (inclui "aguardando supervisão").</summary>
    Humano,
}

/// <summary>Resultado de uma transição: estado anterior, novo estado e nova versão do estado.</summary>
public sealed record TransicaoConversa(EstadoConversa De, EstadoConversa Para, long NovaVersao, string Motivo);

/// <summary>
/// Conversa de um cliente com um tenant. Toda transição incrementa <see cref="VersaoEstado"/>: uma resposta
/// gerada a partir de uma versão antiga não passa no validador (invariantes 5 e 6, CTX-010/011).
/// </summary>
public sealed class Conversa
{
    private Conversa()
    {
    }

    public Conversa(Guid id, Guid tenantId, Guid clienteId)
    {
        Id = id;
        TenantId = tenantId;
        ClienteId = clienteId;
        Estado = EstadoConversa.Automatico;
        VersaoEstado = 1;
    }

    public Guid Id { get; private set; }

    public Guid TenantId { get; private set; }

    public Guid ClienteId { get; private set; }

    public EstadoConversa Estado { get; private set; }

    public long VersaoEstado { get; private set; }

    public DateTimeOffset? UltimaMensagemClienteEm { get; private set; }

    /// <summary>Momento da última liberação explícita pelo humano. A IA só responde mensagens posteriores.</summary>
    public DateTimeOffset? LiberadaEm { get; private set; }

    /// <summary>Marcação para retomada humana pelo celular (fora da janela de 24h).</summary>
    public bool MarcadaRetomadaHumana { get; private set; }

    /// <summary>Reconstrói a conversa a partir do banco.</summary>
    public static Conversa Restaurar(
        Guid id, Guid tenantId, Guid clienteId, EstadoConversa estado, long versaoEstado,
        DateTimeOffset? ultimaMensagemClienteEm, DateTimeOffset? liberadaEm, bool marcadaRetomadaHumana) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            ClienteId = clienteId,
            Estado = estado,
            VersaoEstado = versaoEstado,
            UltimaMensagemClienteEm = ultimaMensagemClienteEm,
            LiberadaEm = liberadaEm,
            MarcadaRetomadaHumana = marcadaRetomadaHumana,
        };

    public void RegistrarMensagemDoCliente(DateTimeOffset recebidaEm)
    {
        if (UltimaMensagemClienteEm is null || recebidaEm > UltimaMensagemClienteEm)
        {
            UltimaMensagemClienteEm = recebidaEm;
        }
    }

    /// <summary>Automação interrompida (bloqueio, impasse, assunto que exige humano).</summary>
    public TransicaoConversa IniciarTransferencia(string motivo)
    {
        Exigir(EstadoConversa.Automatico, "iniciar transferência");
        return Transitar(EstadoConversa.TransferenciaPendente, motivo);
    }

    /// <summary>Humano assume. Vale a partir de qualquer estado, inclusive durante a geração de uma resposta.</summary>
    public TransicaoConversa Assumir(string motivo) => Transitar(EstadoConversa.Humano, motivo);

    /// <summary>Liberação explícita pelo humano. Única forma de voltar ao modo automático.</summary>
    public TransicaoConversa Liberar(DateTimeOffset agora, string motivo)
    {
        Exigir(EstadoConversa.Humano, "liberar para a IA");
        LiberadaEm = agora;
        return Transitar(EstadoConversa.Automatico, motivo);
    }

    public void MarcarRetomadaHumana() => MarcadaRetomadaHumana = true;

    /// <summary>A IA pode tratar esta mensagem? Só em modo automático e só mensagens posteriores à última liberação.</summary>
    public bool IaPodeTratarMensagemRecebidaEm(DateTimeOffset recebidaEm) =>
        Estado == EstadoConversa.Automatico && (LiberadaEm is null || recebidaEm > LiberadaEm);

    /// <summary>Janela de atendimento do WhatsApp: texto livre só até 24h após a última mensagem do cliente.</summary>
    public bool DentroDaJanela24h(DateTimeOffset agora) =>
        UltimaMensagemClienteEm is { } ultima && agora - ultima < TimeSpan.FromHours(24);

    private TransicaoConversa Transitar(EstadoConversa para, string motivo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(motivo);
        var de = Estado;
        Estado = para;
        VersaoEstado++;
        return new TransicaoConversa(de, para, VersaoEstado, motivo);
    }

    private void Exigir(EstadoConversa esperado, string operacao)
    {
        if (Estado != esperado)
        {
            throw new InvalidOperationException($"Não é possível {operacao}: conversa em {Estado}, esperado {esperado}.");
        }
    }
}
