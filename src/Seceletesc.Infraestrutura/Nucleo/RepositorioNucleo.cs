using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Seceletesc.Dominio.Conhecimento;
using Seceletesc.Dominio.Contratos;
using Seceletesc.Dominio.Conversas;
using Seceletesc.Dominio.Politicas;
using Seceletesc.Infraestrutura.Criptografia;

namespace Seceletesc.Infraestrutura.Nucleo;

/// <summary>
/// Acesso ao banco do núcleo de envio. Todo método roda dentro da transação já vinculada ao tenant
/// (ExecutorTenant): o RLS garante o isolamento entre empresas; os filtros por cliente e conversa
/// garantem o isolamento entre clientes (plano técnico, seção 4.5).
/// </summary>
internal static class RepositorioNucleo
{
    public static async Task<(Guid Id, string WaId)> ObterOuCriarClienteAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, string waId, CancellationToken ct)
    {
        await using (var ins = Cmd(c, tx, "insert into clientes (id, tenant_id, wa_id) values (@id, @t, @w) on conflict (tenant_id, wa_id) do nothing"))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid());
            ins.Parameters.AddWithValue("t", tenantId);
            ins.Parameters.AddWithValue("w", waId);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var sel = Cmd(c, tx, "select id from clientes where wa_id = @w");
        sel.Parameters.AddWithValue("w", waId);
        return ((Guid)(await sel.ExecuteScalarAsync(ct).ConfigureAwait(false))!, waId);
    }

    public static async Task<Conversa> ObterOuCriarConversaComTravaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, Guid clienteId, CancellationToken ct)
    {
        await using (var ins = Cmd(c, tx, """
            insert into conversas (id, tenant_id, cliente_id, estado, versao_estado)
            values (@id, @t, @cl, 'Automatico', 1) on conflict (tenant_id, cliente_id) do nothing
            """))
        {
            ins.Parameters.AddWithValue("id", Guid.NewGuid());
            ins.Parameters.AddWithValue("t", tenantId);
            ins.Parameters.AddWithValue("cl", clienteId);
            await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return (await LerConversaAsync(c, tx, "cliente_id = @x", clienteId, ct).ConfigureAwait(false))!;
    }

    public static Task<Conversa?> ObterConversaComTravaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, CancellationToken ct) =>
        LerConversaAsync(c, tx, "id = @x", conversaId, ct);

    public static async Task SalvarConversaAsync(NpgsqlConnection c, NpgsqlTransaction tx, Conversa conversa, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            update conversas set estado = @e, versao_estado = @v, ultima_mensagem_cliente_em = @u,
                   liberada_em = @l, marcada_retomada_humana = @m
             where id = @id
            """);
        cmd.Parameters.AddWithValue("e", conversa.Estado.ToString());
        cmd.Parameters.AddWithValue("v", conversa.VersaoEstado);
        cmd.Parameters.AddWithValue("u", (object?)conversa.UltimaMensagemClienteEm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("l", (object?)conversa.LiberadaEm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("m", conversa.MarcadaRetomadaHumana);
        cmd.Parameters.AddWithValue("id", conversa.Id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Grava a mensagem já mascarada. Devolve falso se o wamid já existe (reenvio do webhook).</summary>
    public static async Task<bool> InserirMensagemAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid tenantId, Guid conversaId, string direcao, string textoMascarado, string? wamid, DateTimeOffset em, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            insert into mensagens (id, tenant_id, conversa_id, direcao, texto_mascarado, wamid, ocorrida_em)
            values (@id, @t, @c, @d, @tx, @w, @em) on conflict (tenant_id, wamid) do nothing
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("d", direcao);
        cmd.Parameters.AddWithValue("tx", textoMascarado);
        cmd.Parameters.AddWithValue("w", (object?)wamid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("em", em);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public static async Task InserirDadoProtegidoAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid tenantId, Guid conversaId, Guid mensagemId, string categoria, DadoCifrado dado, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            insert into dados_protegidos (id, tenant_id, conversa_id, mensagem_id, categoria, chave_cifrada, nonce, cifrado, tag)
            values (@id, @t, @c, @m, @cat, @k, @n, @x, @g)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("m", mensagemId);
        cmd.Parameters.AddWithValue("cat", categoria);
        cmd.Parameters.AddWithValue("k", dado.ChaveCifrada);
        cmd.Parameters.AddWithValue("n", dado.Nonce);
        cmd.Parameters.AddWithValue("x", dado.Cifrado);
        cmd.Parameters.AddWithValue("g", dado.Tag);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task<PoliticaTenant?> ObterPoliticaVigenteAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "select versao, conteudo::text from politicas_tenant order by versao desc limit 1");
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? PoliticaJson.Ler(tenantId, r.GetInt32(0), r.GetString(1)) : null;
    }

    private const string SelectFonte = """
        select v.item_id, v.versao, v.tenant_id, i.assunto, v.conteudo, i.status, v.classificacoes,
               v.escopo_tipo, v.escopo_ref, v.valido_ate, v.origem, (v.versao = i.versao_vigente) as vigente
          from versoes_conhecimento v join itens_conhecimento i on i.id = v.item_id
        """;

    /// <summary>
    /// Candidatos para o contexto do modelo: versão vigente, aprovada e com escopo desta empresa, deste cliente
    /// ou desta conversa. O domínio (ElegibilidadeConhecimento) aplica as demais regras e tem a palavra final.
    /// </summary>
    public static async Task<List<FonteConhecimento>> ListarFontesCandidatasAsync(NpgsqlConnection c, NpgsqlTransaction tx, Conversa conversa, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, SelectFonte + """
             where v.versao = i.versao_vigente and i.status = 'Aprovado'
               and (v.escopo_tipo = 'Empresa'
                    or (v.escopo_tipo = 'Cliente' and v.escopo_ref = @cliente)
                    or (v.escopo_tipo = 'Atendimento' and v.escopo_ref = @conversa))
             order by i.assunto, v.item_id
            """);
        cmd.Parameters.AddWithValue("cliente", conversa.ClienteId);
        cmd.Parameters.AddWithValue("conversa", conversa.Id);
        return await LerFontesAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>Estado atual, no banco, das versões citadas (nunca de cache).</summary>
    public static async Task<Dictionary<ChaveFonte, FonteConhecimento>> ObterFontesAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyCollection<ChaveFonte> chaves, CancellationToken ct)
    {
        if (chaves.Count == 0)
        {
            return [];
        }

        await using var cmd = Cmd(c, tx, SelectFonte + " where (v.item_id, v.versao) in (select * from unnest(@itens, @versoes))");
        cmd.Parameters.Add(new NpgsqlParameter("itens", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = chaves.Select(k => k.ItemId).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("versoes", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = chaves.Select(k => k.Versao).ToArray() });
        return (await LerFontesAsync(cmd, ct).ConfigureAwait(false)).ToDictionary(f => f.Chave);
    }

    public static async Task<List<MensagemContexto>> ListarHistoricoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid conversaId, int limite, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            select direcao, texto_mascarado, ocorrida_em from (
              select direcao, texto_mascarado, ocorrida_em from mensagens
               where conversa_id = @c order by ocorrida_em desc limit @lim) m
             order by ocorrida_em
            """);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("lim", limite);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var lista = new List<MensagemContexto>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            lista.Add(new MensagemContexto(r.GetString(0) == "entrada" ? "cliente" : "atendimento", r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        }

        return lista;
    }

    public static async Task InserirDecisaoAsync(
        NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid tenantId, Guid conversaId, long versaoEstado, string tipo, bool aprovada,
        string? motivo, string? detalhe, int? politicaVersao, IEnumerable<ChaveFonte> entregues, IEnumerable<ChaveFonte> citadas, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            insert into decisoes_envio (id, tenant_id, conversa_id, versao_estado, tipo, resultado, motivo, detalhe, politica_versao, itens_entregues, fontes_citadas)
            values (@id, @t, @c, @v, @tipo, @res, @mot, @det, @pv, @ent::jsonb, @cit::jsonb)
            """);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("v", versaoEstado);
        cmd.Parameters.AddWithValue("tipo", tipo);
        cmd.Parameters.AddWithValue("res", aprovada ? "aprovada" : "bloqueada");
        cmd.Parameters.AddWithValue("mot", (object?)motivo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("det", (object?)detalhe ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pv", (object?)politicaVersao ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ent", ChavesJson(entregues));
        cmd.Parameters.AddWithValue("cit", ChavesJson(citadas));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task InserirOutboxAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, Guid conversaId, Guid decisaoId, string texto, DateTimeOffset expiraEm, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, """
            insert into outbox (id, tenant_id, conversa_id, decisao_id, texto, status, expira_em)
            values (@id, @t, @c, @d, @tx, 'pendente', @exp)
            """);
        cmd.Parameters.AddWithValue("id", Guid.NewGuid());
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", conversaId);
        cmd.Parameters.AddWithValue("d", decisaoId);
        cmd.Parameters.AddWithValue("tx", texto);
        cmd.Parameters.AddWithValue("exp", expiraEm);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public static async Task InserirEventoAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid tenantId, Guid? conversaId, string tipo, object dados, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, "insert into eventos_historico (tenant_id, conversa_id, tipo, dados) values (@t, @c, @tipo, @d::jsonb)");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("c", (object?)conversaId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("tipo", tipo);
        cmd.Parameters.AddWithValue("d", JsonSerializer.Serialize(dados));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static string ChavesJson(IEnumerable<ChaveFonte> chaves) =>
        JsonSerializer.Serialize(chaves.Select(k => new { item = k.ItemId, versao = k.Versao }));

    internal static List<ChaveFonte> LerChavesJson(string json) =>
        JsonSerializer.Deserialize<List<ChaveJson>>(json)!.Select(k => new ChaveFonte(k.item, k.versao)).ToList();

    internal static NpgsqlCommand Cmd(NpgsqlConnection c, NpgsqlTransaction tx, string sql) => new(sql, c, tx);

    private static async Task<Conversa?> LerConversaAsync(NpgsqlConnection c, NpgsqlTransaction tx, string filtro, Guid valor, CancellationToken ct)
    {
        await using var cmd = Cmd(c, tx, $"""
            select id, tenant_id, cliente_id, estado, versao_estado, ultima_mensagem_cliente_em, liberada_em, marcada_retomada_humana
              from conversas where {filtro} for update
            """);
        cmd.Parameters.AddWithValue("x", valor);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return Conversa.Restaurar(
            r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), Enum.Parse<EstadoConversa>(r.GetString(3)), r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            r.GetBoolean(7));
    }

    private static async Task<List<FonteConhecimento>> LerFontesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var fontes = new List<FonteConhecimento>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            fontes.Add(new FonteConhecimento(
                r.GetGuid(0), r.GetInt32(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
                Enum.Parse<StatusConhecimento>(r.GetString(5)),
                new HashSet<Classificacao>(r.GetFieldValue<string[]>(6).Select(Enum.Parse<Classificacao>)),
                new EscopoConhecimento(Enum.Parse<TipoEscopo>(r.GetString(7)), r.IsDBNull(8) ? null : r.GetGuid(8)),
                r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
                r.GetString(10),
                r.GetBoolean(11)));
        }

        return fontes;
    }

    private sealed record ChaveJson(Guid item, int versao);
}
