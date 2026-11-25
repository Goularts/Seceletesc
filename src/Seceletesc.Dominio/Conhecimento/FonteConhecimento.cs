namespace Seceletesc.Dominio.Conhecimento;

/// <summary>Classificações de conteúdo (podem coexistir). governanca-conhecimento.md, seção 3.</summary>
public enum Classificacao
{
    /// <summary>PUB: informação pública.</summary>
    Publico,

    /// <summary>INT: informação interna.</summary>
    Interno,

    /// <summary>TMP: informação temporária.</summary>
    Temporario,

    /// <summary>CLI: informação específica do cliente.</summary>
    Cliente,

    /// <summary>NEG: negociação comercial.</summary>
    Negociacao,

    /// <summary>EXC: exceção autorizada.</summary>
    Excecao,

    /// <summary>RES: informação restrita.</summary>
    Restrito,
}

/// <summary>Status de revisão. REV é status, não classificação.</summary>
public enum StatusConhecimento
{
    Rev,
    Aprovado,
    Rejeitado,
    Revogado,
}

public enum TipoEscopo
{
    Empresa,
    Cliente,
    Proposta,
    Atendimento,
}

/// <summary>Escopo de uso. <see cref="ReferenciaId"/> é o cliente, a proposta ou a conversa; nulo para Empresa.</summary>
public sealed record EscopoConhecimento(TipoEscopo Tipo, Guid? ReferenciaId)
{
    public static EscopoConhecimento Empresa { get; } = new(TipoEscopo.Empresa, null);
}

/// <summary>
/// Uma versão de um item de conhecimento, com as propriedades independentes da invariante 19
/// (origem, classificação, escopo, validade, status, versão). É o que pode ir ao modelo e ser citado.
/// </summary>
public sealed record FonteConhecimento(
    Guid ItemId,
    int Versao,
    Guid TenantId,
    string Assunto,
    string Conteudo,
    StatusConhecimento Status,
    IReadOnlySet<Classificacao> Classificacoes,
    EscopoConhecimento Escopo,
    DateTimeOffset? ValidoAte,
    string Origem,
    bool EhVersaoVigente = true)
{
    public ChaveFonte Chave => new(ItemId, Versao);
}

/// <summary>Identifica uma versão específica de um item de conhecimento.</summary>
public readonly record struct ChaveFonte(Guid ItemId, int Versao);
