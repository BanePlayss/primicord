using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Primicord;

public sealed record ReplicaDocument(string Path, Dictionary<string, object?> Fields,
                                     long UpdatedAt, bool Deleted);

public interface IClusterReplica : IDocumentStore
{
    string BaseUrl { get; }
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
    Task<List<ReplicaDocument>> ExportSnapshotAsync(CancellationToken ct = default);
    Task ImportSnapshotAsync(IReadOnlyList<ReplicaDocument> documents,
                             CancellationToken ct = default);
}

/// <summary>
/// Cliente do mini servidor que roda dentro da tailnet. O contrato replica apenas
/// as operacoes de documento que o Primicord ja usa; nenhuma midia passa por ele.
/// </summary>
public sealed class MiniServerStore : IClusterReplica
{
    private readonly HttpClient _http;

    public string BaseUrl { get; }

    public MiniServerStore(string baseUrl)
    {
        BaseUrl = Normalize(baseUrl);
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl, UriKind.Absolute),
            // Midia continua P2P; esta espera so decide qual replica coordena.
            // Dois segundos evitam congelar lobby/chat quando o lider acabou de cair.
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
        string collectionPath, int pageSize = 100, CancellationToken ct = default,
        string? orderBy = null)
    {
        string url = "v1/list?path=" + Esc(collectionPath) + "&limit=" + pageSize;
        if (!string.IsNullOrWhiteSpace(orderBy)) url += "&orderBy=" + Esc(orderBy);
        using var response = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                   as JsonArray;
        var result = new List<(string, Dictionary<string, object?>)>();
        if (rows == null) return result;
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            result.Add((obj["id"]?.GetValue<string>() ?? "",
                        ParseFields(obj["fields"] as JsonObject)));
        }
        return result;
    }

    public async Task<Dictionary<string, object?>?> GetAsync(
        string docPath, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/doc?path=" + Esc(docPath),
                                             null, ct, allowNotFound: true).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        var obj = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                  as JsonObject;
        return ParseFields(obj);
    }

    public async Task SetAsync(string docPath, Dictionary<string, object?> fields,
                               bool mergeFields = false, CancellationToken ct = default)
    {
        string url = "v1/doc?path=" + Esc(docPath) + "&merge=" + (mergeFields ? "true" : "false");
        using var body = new StringContent(JsonSerializer.Serialize(fields), Encoding.UTF8,
                                           "application/json");
        using var _ = await SendAsync(HttpMethod.Put, url, body, ct).ConfigureAwait(false);
    }

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
        string parentPath, string collectionId, string orderField, bool descending, int limit,
        CancellationToken ct = default)
        => QueryCoreAsync(parentPath, collectionId, orderField, descending, limit, null, ct);

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
        string parentPath, string collectionId, string orderField, long sinceInclusive, int limit,
        CancellationToken ct = default)
        => QueryCoreAsync(parentPath, collectionId, orderField, false, limit, sinceInclusive, ct);

    private async Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryCoreAsync(
        string parentPath, string collectionId, string orderField, bool descending, int limit,
        long? sinceInclusive, CancellationToken ct)
    {
        var request = new
        {
            parentPath,
            collectionId,
            orderField,
            descending,
            limit,
            sinceInclusive,
        };
        using var body = JsonContent.Create(request);
        using var response = await SendAsync(HttpMethod.Post, "v1/query", body, ct)
            .ConfigureAwait(false);
        var rows = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                   as JsonArray;
        var result = new List<(string, Dictionary<string, object?>)>();
        if (rows == null) return result;
        foreach (var row in rows)
        {
            if (row is not JsonObject obj) continue;
            result.Add((obj["id"]?.GetValue<string>() ?? "",
                        ParseFields(obj["fields"] as JsonObject)));
        }
        return result;
    }

    public async Task DeleteAsync(string docPath, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete, "v1/doc?path=" + Esc(docPath),
                                      null, ct, allowNotFound: true).ConfigureAwait(false);
    }

    public async Task<List<ReplicaDocument>> ExportSnapshotAsync(CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "v1/snapshot", null, ct)
            .ConfigureAwait(false);
        var array = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
                    as JsonArray;
        var result = new List<ReplicaDocument>();
        if (array == null) return result;
        foreach (JsonNode? node in array)
        {
            if (node is not JsonObject item) continue;
            string path = item["path"]?.GetValue<string>() ?? "";
            if (path.Length == 0) continue;
            result.Add(new ReplicaDocument(
                path,
                ParseFields(item["fields"] as JsonObject),
                item["updatedAt"]?.GetValue<long>() ?? 0,
                item["deleted"]?.GetValue<bool>() ?? false));
        }
        return result;
    }

    public async Task ImportSnapshotAsync(IReadOnlyList<ReplicaDocument> documents,
                                          CancellationToken ct = default)
    {
        var array = new JsonArray();
        foreach (ReplicaDocument document in documents)
            array.Add(new JsonObject
            {
                ["path"] = document.Path,
                ["fields"] = ToJson(document.Fields),
                ["updatedAt"] = document.UpdatedAt,
                ["deleted"] = document.Deleted,
            });
        using var body = new StringContent(array.ToJsonString(), Encoding.UTF8, "application/json");
        using var _ = await SendAsync(HttpMethod.Post, "v1/snapshot", body, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url,
                                                       HttpContent? body, CancellationToken ct,
                                                       bool allowNotFound = false)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url) { Content = body };
            var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                return response;

            string detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new MiniServerException($"Mini servidor {(int)response.StatusCode}: {detail}",
                                          response.StatusCode);
        }
        catch (MiniServerException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MiniServerException("Mini servidor demorou demais para responder.", null);
        }
        catch (HttpRequestException ex)
        {
            throw new MiniServerException("Mini servidor indisponivel: " + ex.Message, null, ex);
        }
    }

    private static string Normalize(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) value = "http://primicord-server:8765";
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        return value.TrimEnd('/') + "/";
    }

    private static string Esc(string value) => Uri.EscapeDataString(value.Trim('/'));

    internal static Dictionary<string, object?> ParseFields(JsonObject? obj)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (obj == null) return result;
        foreach (var (key, value) in obj)
        {
            result[key] = value switch
            {
                null => null,
                JsonValue v when v.TryGetValue<bool>(out var b) => b,
                JsonValue v when v.TryGetValue<long>(out var l) => l,
                JsonValue v when v.TryGetValue<double>(out var d) => d,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => value.ToJsonString(),
            };
        }
        return result;
    }

    internal static JsonObject ToJson(Dictionary<string, object?> fields)
    {
        var result = new JsonObject();
        foreach (var (key, value) in fields)
            result[key] = value switch
            {
                null => null,
                bool b => b,
                byte b => b,
                short s => s,
                int i => i,
                long l => l,
                float f => f,
                double d => d,
                decimal d => (double)d,
                string s => s,
                _ => value.ToString(),
            };
        return result;
    }
}

public sealed class MiniServerException : DocumentStoreException
{
    public HttpStatusCode? StatusCode { get; }

    public MiniServerException(string message, HttpStatusCode? statusCode, Exception? inner = null)
        : base(message, inner) => StatusCode = statusCode;

    public override bool IsPermissionDenied => StatusCode == HttpStatusCode.Forbidden;
    public override bool IsUnavailable => StatusCode is null || StatusCode == HttpStatusCode.RequestTimeout
        || StatusCode == HttpStatusCode.TooManyRequests || (int?)StatusCode >= 500;
}

/// <summary>
/// Mantem a 0.6.9 compativel enquanto todos configuram a tailnet. Quando o mini
/// servidor responde ele recebe tudo; se estiver ausente, o caminho antigo entra
/// por alguns minutos sem martelar cada chamada.
/// </summary>
public sealed class MigratingDocumentStore : IDocumentStore
{
    private readonly MiniServerStore _primary;
    private readonly IDocumentStore _fallback;
    private DateTimeOffset _retryAt;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private bool _primaryReady;

    public MigratingDocumentStore(MiniServerStore primary, IDocumentStore fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public string ActiveName => _primaryReady ? "mini servidor" : "Firestore (compatibilidade)";

    private async Task<IDocumentStore> ChooseAsync(CancellationToken ct)
    {
        if (_primaryReady) return _primary;
        if (DateTimeOffset.UtcNow < _retryAt) return _fallback;

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_primaryReady) return _primary;
            if (DateTimeOffset.UtcNow < _retryAt) return _fallback;
            _primaryReady = await _primary.IsAvailableAsync(ct).ConfigureAwait(false);
            if (_primaryReady)
            {
                Log.Write("coordenacao: usando mini servidor em " + _primary.BaseUrl);
                return _primary;
            }
            _retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            Log.Write("coordenacao: mini servidor ausente; compatibilidade Firestore por 2 min");
            return _fallback;
        }
        finally { _probeGate.Release(); }
    }

    private async Task<T> RunAsync<T>(Func<IDocumentStore, Task<T>> action, CancellationToken ct)
    {
        var store = await ChooseAsync(ct).ConfigureAwait(false);
        try { return await action(store).ConfigureAwait(false); }
        catch (MiniServerException ex) when (ex.IsUnavailable)
        {
            _primaryReady = false;
            _retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            Log.Write("coordenacao: mini servidor caiu; usando compatibilidade: " + ex.Message);
            return await action(_fallback).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(Func<IDocumentStore, Task> action, CancellationToken ct)
    {
        var store = await ChooseAsync(ct).ConfigureAwait(false);
        try { await action(store).ConfigureAwait(false); }
        catch (MiniServerException ex) when (ex.IsUnavailable)
        {
            _primaryReady = false;
            _retryAt = DateTimeOffset.UtcNow.AddMinutes(2);
            Log.Write("coordenacao: mini servidor caiu; usando compatibilidade: " + ex.Message);
            await action(_fallback).ConfigureAwait(false);
        }
    }

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
        string collectionPath, int pageSize = 100, CancellationToken ct = default,
        string? orderBy = null)
        => RunAsync(s => s.ListAsync(collectionPath, pageSize, ct, orderBy), ct);

    public Task<Dictionary<string, object?>?> GetAsync(string docPath, CancellationToken ct = default)
        => RunAsync(s => s.GetAsync(docPath, ct), ct);

    public Task SetAsync(string docPath, Dictionary<string, object?> fields,
                         bool mergeFields = false, CancellationToken ct = default)
        => RunAsync(s => s.SetAsync(docPath, fields, mergeFields, ct), ct);

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
        string parentPath, string collectionId, string orderField, bool descending, int limit,
        CancellationToken ct = default)
        => RunAsync(s => s.QueryAsync(parentPath, collectionId, orderField, descending, limit, ct), ct);

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
        string parentPath, string collectionId, string orderField, long sinceInclusive, int limit,
        CancellationToken ct = default)
        => RunAsync(s => s.QuerySinceAsync(parentPath, collectionId, orderField, sinceInclusive, limit, ct), ct);

    public Task DeleteAsync(string docPath, CancellationToken ct = default)
        => RunAsync(s => s.DeleteAsync(docPath, ct), ct);
}
