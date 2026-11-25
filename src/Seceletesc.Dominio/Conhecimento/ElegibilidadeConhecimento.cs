using Seceletesc.Dominio.Conversas;
using Seceletesc.Dominio.Politicas;

namespace Seceletesc.Dominio.Conhecimento;

public enum MotivoInelegivel
{
    OutroTenant,
    NaoAprovado,
    VersaoNaoVigente,
    SemValidade,
    Expirado,
    ForaDoEscopo,
    ClassificacaoNaoPermitida,
    ConhecimentoDeClienteMinimizado,

    /// <summary>Aprovado antes de a política reduzir o prazo de validade da classificação: aguarda reavaliação humana.</summary>
    ValidadeAcimaDaPolitica,
}

/// <summary>
/// Decide se uma versão de conhecimento pode ir ao modelo e ser citada numa resposta automática
/// (invariantes 10, 11 e 20; CTX-007, CTX-012 a CTX-014). Usado pelo montador de contexto e,
/// de novo, pelo validador no momento do envio.
/// </summary>
public static class ElegibilidadeConhecimento
{
    public static MotivoInelegivel? Avaliar(FonteConhecimento fonte, PoliticaTenant politica, Conversa conversa, DateTimeOffset agora)
    {
        ArgumentNullException.ThrowIfNull(fonte);
        ArgumentNullException.ThrowIfNull(politica);
        ArgumentNullException.ThrowIfNull(conversa);

        if (fonte.TenantId != conversa.TenantId || politica.TenantId != conversa.TenantId)
        {
            return MotivoInelegivel.OutroTenant;
        }

        if (fonte.Status != StatusConhecimento.Aprovado)
        {
            return MotivoInelegivel.NaoAprovado;
        }

        if (!fonte.EhVersaoVigente)
        {
            return MotivoInelegivel.VersaoNaoVigente;
        }

        // Só PUB pode ter validade indefinida (governanca-conhecimento.md, seção 3; CTX-006).
        if (fonte.ValidoAte is null && !(fonte.Classificacoes.Count > 0 && fonte.Classificacoes.All(c => c == Classificacao.Publico)))
        {
            return MotivoInelegivel.SemValidade;
        }

        if (fonte.ValidoAte is { } validoAte && validoAte <= agora)
        {
            return MotivoInelegivel.Expirado;
        }

        // Decisão do Victor (2026-11-25): a nova política de validade vale também para o que já foi aprovado. Não há
        // exclusão silenciosa: o item continua no histórico e na lista de reavaliação da supervisão, só não é usado.
        if (ExcedeValidadeDaPolitica(fonte.Classificacoes, fonte.ValidoAte, fonte.AprovadaEm, politica))
        {
            return MotivoInelegivel.ValidadeAcimaDaPolitica;
        }

        var noEscopo = fonte.Escopo.Tipo switch
        {
            TipoEscopo.Empresa => true,
            TipoEscopo.Cliente => fonte.Escopo.ReferenciaId == conversa.ClienteId,
            TipoEscopo.Atendimento => fonte.Escopo.ReferenciaId == conversa.Id,
            // Propostas ainda não estão vinculadas a conversas: sem vínculo, não é elegível.
            TipoEscopo.Proposta => false,
            _ => false,
        };
        if (!noEscopo)
        {
            return MotivoInelegivel.ForaDoEscopo;
        }

        if (fonte.Classificacoes.Count == 0 || !fonte.Classificacoes.All(politica.ClassificacoesPermitidasRespostaAutomatica.Contains))
        {
            return MotivoInelegivel.ClassificacaoNaoPermitida;
        }

        // Condição comercial excepcional (NEG, EXC) só vale para um cliente ou atendimento definido e com validade
        // (decisão 22). Garantido na aprovação; reconferido no uso.
        if ((fonte.Classificacoes.Contains(Classificacao.Negociacao) || fonte.Classificacoes.Contains(Classificacao.Excecao))
            && (fonte.Escopo.Tipo is not (TipoEscopo.Cliente or TipoEscopo.Atendimento) || fonte.ValidoAte is null))
        {
            return MotivoInelegivel.ForaDoEscopo;
        }

        if (fonte.Classificacoes.Contains(Classificacao.Cliente))
        {
            if (!politica.EnviarConhecimentoClienteAoModelo)
            {
                return MotivoInelegivel.ConhecimentoDeClienteMinimizado;
            }

            // Informação de cliente só vale para aquele cliente ou aquele atendimento, nunca para a empresa toda.
            var doProprioCliente = (fonte.Escopo.Tipo == TipoEscopo.Cliente && fonte.Escopo.ReferenciaId == conversa.ClienteId)
                || (fonte.Escopo.Tipo == TipoEscopo.Atendimento && fonte.Escopo.ReferenciaId == conversa.Id);
            if (!doProprioCliente)
            {
                return MotivoInelegivel.ForaDoEscopo;
            }
        }

        return null;
    }

    public static bool EhElegivel(FonteConhecimento fonte, PoliticaTenant politica, Conversa conversa, DateTimeOffset agora) =>
        Avaliar(fonte, politica, conversa, agora) is null;

    /// <summary>
    /// A validade aprovada passa do prazo máximo que a política atual define para alguma das classificações
    /// (contado da aprovação). Com prazo definido e sem data de aprovação conhecida, falha fechada (revisão da F4.5, S8).
    /// </summary>
    public static bool ExcedeValidadeDaPolitica(IReadOnlySet<Classificacao> classificacoes, DateTimeOffset? validoAte, DateTimeOffset? aprovadaEm, PoliticaTenant politica)
    {
        ArgumentNullException.ThrowIfNull(classificacoes);
        ArgumentNullException.ThrowIfNull(politica);
        var prazos = classificacoes.Where(politica.ValidadeMaximaDias.ContainsKey).Select(c => politica.ValidadeMaximaDias[c]).ToList();
        if (prazos.Count == 0)
        {
            return false;
        }

        return aprovadaEm is not { } aprovacao || validoAte is not { } v || v > aprovacao.AddDays(prazos.Min());
    }
}
