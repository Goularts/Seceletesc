using Seceletesc.Dominio.Contratos;

namespace Seceletesc.Infraestrutura.Nucleo;

/// <summary>
/// Motor de IA enquanto o provedor não é escolhido (F5): devolve um rascunho vazio. O validador o recusa
/// e a conversa vai para supervisão (falha fechada). Nada é enviado ao cliente pela IA.
/// </summary>
public sealed class MotorIaIndisponivel : IMotorIA
{
    public Task<string> GerarRascunhoAsync(ContextoModelo contexto, CancellationToken ct) => Task.FromResult("{}");
}
