namespace Seceletesc.Dominio.Contratos;

/// <summary>Uma mensagem do histórico desta conversa, já sem dados protegidos.</summary>
public sealed record MensagemContexto(string Autor, string TextoMascarado, DateTimeOffset Em);

/// <summary>Um conhecimento que o modelo pode usar e citar (id + versão).</summary>
public sealed record ConhecimentoContexto(Guid ItemId, int Versao, string Assunto, string Conteudo);

/// <summary>
/// Tudo o que o modelo recebe. Montado pelo backend: só esta conversa, só conhecimento elegível,
/// sem telefone, sem identificador do cliente e sem dados protegidos (plano técnico, seção 4.2).
/// </summary>
/// <param name="MensagemAResponder">
/// Texto (já mascarado) da mensagem que esta resposta atende. Numa mensagem que chegou fora de ordem, ela não é a última
/// do histórico: o motor precisa saber qual responder.
/// </param>
public sealed record ContextoModelo(
    IReadOnlyList<MensagemContexto> Historico,
    IReadOnlyList<ConhecimentoContexto> Conhecimentos,
    IReadOnlyList<string> AssuntosAutonomos,
    string? MensagemAResponder = null);

/// <summary>
/// Motor de IA. Devolve um rascunho em JSON (ver LeitorRascunho), nunca envia nada e não acessa banco.
/// A implementação real entra na F5; até lá, os testes usam motores simulados, inclusive maliciosos.
/// </summary>
public interface IMotorIA
{
    Task<string> GerarRascunhoAsync(ContextoModelo contexto, CancellationToken ct);
}
