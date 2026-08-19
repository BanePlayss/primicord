using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Primicord.Server;

public sealed class SqliteDocumentStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public SqliteDocumentStore(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents (
                path TEXT PRIMARY KEY NOT NULL,
                fields_json TEXT NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_documents_updated ON documents(updated_at);
            """;
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<JsonObject?> GetAsync(string path, CancellationToken ct = default)
    {
        path = Clean(path);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fields_json FROM documents WHERE path = $path";
        command.Parameters.AddWithValue("$path", path);
        object? value = await command.ExecuteScalarAsync(ct);
        return value is string json ? JsonNode.Parse(json) as JsonObject : null;
    }

    public async Task SetAsync(string path, JsonObject fields, bool merge,
                               CancellationToken ct = default)
    {
        path = Clean(path);
        await _writes.WaitAsync(ct);
        try
        {
            if (merge)
            {
                JsonObject existing = await GetAsync(path, ct) ?? new JsonObject();
                foreach (var (key, value) in fields)
                    existing[key] = value?.DeepClone();
                fields = existing;
            }

            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO documents(path, fields_json, updated_at)
                VALUES($path, $json, $updated)
                ON CONFLICT(path) DO UPDATE SET
                    fields_json = excluded.fields_json,
                    updated_at = excluded.updated_at
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$json", fields.ToJsonString());
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { _writes.Release(); }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        path = Clean(path);
        await _writes.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            // Diferente do Firestore, limpar o pai tambem limpa os filhos orfaos.
            command.CommandText = "DELETE FROM documents WHERE path = $path OR path LIKE $prefix ESCAPE '\\'";
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$prefix", EscapeLike(path) + "/%");
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { _writes.Release(); }
    }

    public async Task<List<StoreRow>> ListAsync(string collectionPath, int limit,
                                                 string? orderBy, CancellationToken ct = default)
    {
        collectionPath = Clean(collectionPath);
        string prefix = collectionPath + "/";
        var all = new List<StoreRow>();

        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, fields_json FROM documents WHERE path LIKE $prefix ESCAPE '\\'";
        command.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string fullPath = reader.GetString(0);
            string remainder = fullPath[prefix.Length..];
            if (remainder.Length == 0 || remainder.Contains('/')) continue;
            all.Add(new StoreRow(remainder,
                JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject()));
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            string[] pieces = orderBy.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool descending = pieces.Length > 1 && pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
            if (pieces[0] == "__name__")
                all = descending ? all.OrderByDescending(r => r.Id, StringComparer.Ordinal).ToList()
                                 : all.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        }
        return all.Take(limit).ToList();
    }

    public async Task<List<StoreRow>> QueryAsync(StoreQuery query, CancellationToken ct = default)
    {
        string collection = string.IsNullOrWhiteSpace(query.ParentPath)
            ? query.CollectionId
            : Clean(query.ParentPath) + "/" + Clean(query.CollectionId);
        var rows = await ListAsync(collection, 500, null, ct);

        IEnumerable<StoreRow> filtered = rows;
        if (query.SinceInclusive.HasValue)
            filtered = filtered.Where(r => Number(r.Fields, query.OrderField) >= query.SinceInclusive.Value);

        filtered = query.Descending
            ? filtered.OrderByDescending(r => Number(r.Fields, query.OrderField)).ThenByDescending(r => r.Id)
            : filtered.OrderBy(r => Number(r.Fields, query.OrderField)).ThenBy(r => r.Id);
        return filtered.Take(Math.Clamp(query.Limit, 1, 500)).ToList();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static long Number(JsonObject fields, string key)
    {
        JsonNode? node = fields[key];
        if (node is JsonValue value && value.TryGetValue<long>(out long number)) return number;
        if (node is JsonValue real && real.TryGetValue<double>(out double d)) return (long)d;
        return 0;
    }

    private static string Clean(string path)
    {
        path = (path ?? "").Trim().Trim('/').Replace('\\', '/');
        if (path.Length == 0 || path.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Caminho de documento invalido.", nameof(path));
        return path;
    }

    private static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

public sealed record StoreRow(string Id, JsonObject Fields);
