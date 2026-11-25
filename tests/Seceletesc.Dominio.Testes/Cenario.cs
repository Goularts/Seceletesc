using System.Text.Json;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Conversas;
using Seceletesc.Dominio.Envio;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Dominio.Respostas;

namespace Seceletesc.Dominio.Testes;

/// <summary>Montagem de cenários de teste do domínio. Dados sintéticos.</summary>
internal static class Cenario
{
    public static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid OutroTenant = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    public static readonly Guid Cliente = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000c1");
    public static readonly Guid OutroCliente = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000c9");
    public const string TelefoneCliente = "5511988887777";
    public static readonly DateTimeOffset Agora = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public static Conversa NovaConversa() => new(Guid.Parse("aaaaaaaa-0000-0000-0000-00000000c0c0"), Tenant, Cliente);

    public static PoliticaTenant Politica(Action<PoliticaBuilder>? ajuste = null)
    {
        var b = new PoliticaBuilder();
        ajuste?.Invoke(b);
        return new PoliticaTenant
        {
            TenantId = Tenant,
            Versao = 1,
            AssuntosAutonomos = b.Autonomos,
            AssuntosExigemHumano = b.ExigemHumano,
            ClassificacoesPermitidasRespostaAutomatica = b.Classificacoes,
            TextoAvisoTransferencia = b.Aviso,
            DominiosPermitidos = b.Dominios,
        };
    }

    public static FonteConhecimento Fonte(
        string assunto,
        string conteudo,
        StatusConhecimento status = StatusConhecimento.Aprovado,
        Classificacao[]? classificacoes = null,
        EscopoConhecimento? escopo = null,
        DateTimeOffset? validoAte = null,
        Guid? tenant = null,
        int versao = 1) =>
        new(
            Guid.NewGuid(),
            versao,
            tenant ?? Tenant,
            assunto,
            conteudo,
            status,
            new HashSet<Classificacao>(classificacoes ?? [Classificacao.Publico]),
            escopo ?? EscopoConhecimento.Empresa,
            validoAte,
            "teste");

    public static ContextoValidacao Contexto(Conversa conversa, PoliticaTenant politica, params FonteConhecimento[] fontes) =>
        new(
            conversa,
            conversa.VersaoEstado,
            politica,
            fontes.Select(f => f.Chave).ToHashSet(),
            fontes.ToDictionary(f => f.Chave),
            Agora);

    public static RascunhoResposta Rascunho(string texto, string assunto, params FonteConhecimento[] fontes) =>
        new(texto, fontes.Select(f => f.Chave).ToList(), assunto);

    public static string RascunhoJson(string texto, string assunto, params FonteConhecimento[] fontes) =>
        JsonSerializer.Serialize(new
        {
            texto,
            fontes = fontes.Select(f => new { item = f.ItemId, versao = f.Versao }),
            assunto,
        });

    internal sealed class PoliticaBuilder
    {
        public HashSet<string> Autonomos { get; } = ["horario", "precos"];

        public HashSet<string> ExigemHumano { get; } = ["desconto"];

        public HashSet<Classificacao> Classificacoes { get; } = [Classificacao.Publico];

        public string Aviso { get; set; } = "Vou encaminhar sua mensagem para nossa equipe.";

        public HashSet<string> Dominios { get; } = [];
    }
}
