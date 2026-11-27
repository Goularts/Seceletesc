using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Seceletesc.Infraestrutura.Banco;

/// <summary>
/// Aplica, em ordem, os scripts SQL embutidos em Banco/Migracoes. Deve rodar com o papel migrador
/// (dono das tabelas), nunca com os papéis da aplicação. Guarda o SHA-256 de cada script: se um script
/// já aplicado for alterado, a migração falha em vez de seguir com o banco divergente do código.
/// </summary>
public static class Migrador
{
    private const string PrefixoRecurso = "Seceletesc.Infraestrutura.Banco.Migracoes.";

    // Chave fixa do advisory lock que serializa migrações concorrentes.
    private const long ChaveLock = 7_310_532_001;

    public static async Task<IReadOnlyList<string>> AplicarAsync(string connectionString, CancellationToken ct = default)
    {
        // Sessão própria, fora do pool: uma migração pode definir variáveis de sessão (ex.: app.tenant_id na 0007, para
        // validar chaves estrangeiras sob o RLS) e essa sessão não pode ser reaproveitada por mais ninguém.
        await using var conexao = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await conexao.OpenAsync(ct).ConfigureAwait(false);

        await ExecutarAsync(conexao, null, "set search_path = public, pg_temp", ct).ConfigureAwait(false);
        await ExecutarAsync(conexao, null, $"select pg_advisory_lock({ChaveLock})", ct).ConfigureAwait(false);
        try
        {
            await ExecutarAsync(conexao, null, """
                create table if not exists schema_migracoes (
                  versao      text primary key,
                  checksum    text not null,
                  aplicado_em timestamptz not null default now()
                )
                """, ct).ConfigureAwait(false);

            var aplicadas = new List<string>();
            foreach (var (versao, sql) in ScriptsEmbutidos())
            {
                await using var tx = await conexao.BeginTransactionAsync(ct).ConfigureAwait(false);

                // Normaliza quebras de linha: o mesmo script não pode ter checksum diferente no Windows e no Linux.
                var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n"))));

                await using (var jaAplicada = new NpgsqlCommand("select checksum from schema_migracoes where versao = @v", conexao, tx))
                {
                    jaAplicada.Parameters.AddWithValue("v", versao);
                    if (await jaAplicada.ExecuteScalarAsync(ct).ConfigureAwait(false) is string existente)
                    {
                        await tx.RollbackAsync(ct).ConfigureAwait(false);
                        if (!string.Equals(existente, checksum, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"A migração {versao} foi alterada depois de aplicada. Crie uma nova migração em vez de editar a antiga.");
                        }

                        continue;
                    }
                }

                await ExecutarAsync(conexao, tx, sql, ct).ConfigureAwait(false);

                await using (var registrar = new NpgsqlCommand("insert into schema_migracoes (versao, checksum) values (@v, @c)", conexao, tx))
                {
                    registrar.Parameters.AddWithValue("v", versao);
                    registrar.Parameters.AddWithValue("c", checksum);
                    await registrar.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
                aplicadas.Add(versao);
            }

            return aplicadas;
        }
        finally
        {
            await ExecutarAsync(conexao, null, $"select pg_advisory_unlock({ChaveLock})", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static IEnumerable<(string Versao, string Sql)> ScriptsEmbutidos()
    {
        var assembly = typeof(Migrador).Assembly;
        foreach (var nome in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(PrefixoRecurso, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(nome)
                ?? throw new InvalidOperationException($"Recurso de migração não encontrado: {nome}");
            using var leitor = new StreamReader(stream);
            yield return (nome[PrefixoRecurso.Length..^".sql".Length], leitor.ReadToEnd());
        }
    }

    private static async Task ExecutarAsync(NpgsqlConnection conexao, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var comando = new NpgsqlCommand(sql, conexao, tx);
        await comando.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
