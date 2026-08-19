using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace Primicord.Server;

public sealed class SqliteDocumentStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private long _lastWriteMs;

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
                updated_at INTEGER NOT NULL,
                deleted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_documents_updated ON documents(updated_at);
            """;
        await command.ExecuteNonQueryAsync(ct);

        // Banco criado pela 0.6.9-0.6.13: acrescenta tombstones sem perder salas.
        await using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(documents)";
        bool hasDeleted = false;
        await using (var reader = await columns.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (reader.GetString(1).Equals("deleted", StringComparison.OrdinalIgnoreCase))
                    hasDeleted = true;
        if (!hasDeleted)
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE documents ADD COLUMN deleted INTEGER NOT NULL DEFAULT 0";
            await migrate.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<JsonObject?> GetAsync(string path, CancellationToken ct = default)
    {
        path = Clean(path);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT fields_json FROM documents WHERE path = $path AND deleted = 0";
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
                INSERT INTO documents(path, fields_json, updated_at, deleted)
                VALUES($path, $json, $updated, 0)
                ON CONFLICT(path) DO UPDATE SET
                    fields_json = excluded.fields_json,
                    updated_at = excluded.updated_at,
                    deleted = 0
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$json", fields.ToJsonString());
            command.Parameters.AddWithValue("$updated", NextTimestamp());
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
            // Nao remove fisicamente: a marca de exclusao viaja para replicas que
            // estavam offline e impede uma copia antiga de ressuscitar a sala.
            long updated = NextTimestamp();
            command.CommandText = """
                UPDATE documents SET fields_json = '{}', updated_at = $updated, deleted = 1
                WHERE path = $path OR path LIKE $prefix ESCAPE '\';
                INSERT INTO documents(path, fields_json, updated_at, deleted)
                VALUES($path, '{}', $updated, 1)
                ON CONFLICT(path) DO UPDATE SET
                    fields_json = '{}', updated_at = excluded.updated_at, deleted = 1;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$prefix", EscapeLike(path) + "/%");
            command.Parameters.AddWithValue("$updated", updated);
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
        command.CommandText = "SELECT path, fields_json FROM documents WHERE deleted = 0 AND path LIKE $prefix ESCAPE '\\'";
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

    public async Task<List<SnapshotDocument>> ExportAsync(CancellationToken ct = default)
    {
        var result = new List<SnapshotDocument>();
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, fields_json, updated_at, deleted FROM documents";
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new SnapshotDocument(
                reader.GetString(0),
                JsonNode.Parse(reader.GetString(1)) as JsonObject ?? new JsonObject(),
                reader.GetInt64(2),
                reader.GetInt64(3) != 0));
        return result;
    }

    public async Task ImportAsync(IEnumerable<SnapshotDocument> documents,
                                  CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            foreach (SnapshotDocument document in documents)
            {
                string path = Clean(document.Path);
                await using var command = connection.CreateCommand();
                command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO documents(path, fields_json, updated_at, deleted)
                    VALUES($path, $json, $updated, $deleted)
                    ON CONFLICT(path) DO UPDATE SET
                        fields_json = excluded.fields_json,
                        updated_at = excluded.updated_at,
                        deleted = excluded.deleted
                    WHERE excluded.updated_at > documents.updated_at
                       OR (excluded.updated_at = documents.updated_at
                           AND excluded.deleted > documents.deleted)
                    """;
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$json", document.Fields.ToJsonString());
                command.Parameters.AddWithValue("$updated", document.UpdatedAt);
                command.Parameters.AddWithValue("$deleted", document.Deleted ? 1 : 0);
                await command.ExecuteNonQueryAsync(ct);
                InterlockedExtensions.Max(ref _lastWriteMs, document.UpdatedAt);
            }
            await transaction.CommitAsync(ct);
        }
        finally { _writes.Release(); }
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

    private long NextTimestamp()
    {
        while (true)
        {
            long previous = Interlocked.Read(ref _lastWriteMs);
            long next = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), previous + 1);
            if (Interlocked.CompareExchange(ref _lastWriteMs, next, previous) == previous) return next;
        }
    }
}

public sealed record StoreRow(string Id, JsonObject Fields);
public sealed record SnapshotDocument(string Path, JsonObject Fields, long UpdatedAt, bool Deleted);

internal static class InterlockedExtensions
{
    public static void Max(ref long target, long value)
    {
        while (true)
        {
            long current = Interlocked.Read(ref target);
            if (current >= value) return;
            if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }
}
