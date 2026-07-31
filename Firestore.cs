using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Primicord;

/// <summary>
/// Cliente REST minimo do Firestore — o "ponto de encontro" do Primicord.
/// </summary>
/// <remarks>
/// Por que REST e nao o SDK: o Firebase nao tem SDK .NET desktop decente, e a unica
/// coisa que precisamos do Firestore e uma lousa compartilhada (que salas existem,
/// quem esta em cada uma, e qual o endereco IP:porta de cada um). Nenhuma midia passa
/// por aqui — audio e video vao P2P direto entre os PCs.
///
/// Nao ha listener em tempo real no REST (a Listen API e gRPC), entao a lista e lida
/// por POLLING (~2s). Pra uma lista de salas isso e barato e mais que suficiente.
///
/// A apiKey e a mesma do app web (chave publica de cliente; quem protege e a rules).
/// </remarks>
public sealed class Firestore
{
    public const string ProjectId = "primitivao";
    private const string ApiKey = "AIzaSyB4Tu-OIAfBUfzdtY-wF9tSoBwP_36hdRg";
    private const string Base =
        "https://firestore.googleapis.com/v1/projects/" + ProjectId + "/databases/(default)/documents";

    private readonly HttpClient _http;

    public Firestore()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static string Url(string path, string? query = null)
    {
        string u = Base + "/" + path.TrimStart('/') + "?key=" + ApiKey;
        return query is null ? u : u + "&" + query;
    }

    // ─── LEITURA ─────────────────────────────────────────────────────────────

    /// <summary>Lista os documentos de uma coleção. Retorna (id, campos) por doc.</summary>
    public async Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
        string collectionPath, int pageSize = 100, CancellationToken ct = default)
    {
        var outList = new List<(string, Dictionary<string, object?>)>();
        string? pageToken = null;
        do
        {
            string q = "pageSize=" + pageSize;
            if (pageToken != null) q += "&pageToken=" + Uri.EscapeDataString(pageToken);

            using var resp = await _http.GetAsync(Url(collectionPath, q), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                // 404 = coleção ainda não existe (nenhum doc criado) — não é erro.
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return outList;
                throw new FirestoreException(await Describe(resp).ConfigureAwait(false));
            }

            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var docs = node?["documents"]?.AsArray();
            if (docs != null)
            {
                foreach (var d in docs)
                {
                    if (d is null) continue;
                    string name = d["name"]?.GetValue<string>() ?? "";
                    string id = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
                    outList.Add((id, ParseFields(d["fields"])));
                }
            }
            pageToken = node?["nextPageToken"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(pageToken));

        return outList;
    }

    /// <summary>Lê um documento. Retorna null se não existe.</summary>
    public async Task<Dictionary<string, object?>?> GetAsync(string docPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(Url(docPath), ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new FirestoreException(await Describe(resp).ConfigureAwait(false));
        var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseFields(node?["fields"]);
    }

    // ─── ESCRITA ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Cria ou atualiza um documento. Com <paramref name="mergeFields"/>, só os campos
    /// listados são tocados (updateMask) — o resto do doc fica intacto.
    /// </summary>
    public async Task SetAsync(string docPath, Dictionary<string, object?> fields,
                               bool mergeFields = false, CancellationToken ct = default)
    {
        string? q = null;
        if (mergeFields)
        {
            var sb = new StringBuilder();
            foreach (var k in fields.Keys)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append("updateMask.fieldPaths=").Append(Uri.EscapeDataString(k));
            }
            q = sb.ToString();
        }

        var body = new JsonObject { ["fields"] = BuildFields(fields) };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Patch, Url(docPath, q)) { Content = content };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new FirestoreException(await Describe(resp).ConfigureAwait(false));
    }

    public async Task DeleteAsync(string docPath, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync(Url(docPath), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NotFound)
            throw new FirestoreException(await Describe(resp).ConfigureAwait(false));
    }

    private static async Task<string> Describe(HttpResponseMessage resp)
    {
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false); } catch { }
        string msg = "";
        try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? ""; } catch { }
        if (string.IsNullOrEmpty(msg)) msg = body.Length > 300 ? body[..300] : body;
        return $"Firestore {(int)resp.StatusCode}: {msg}";
    }

    // ─── CONVERSÃO DE VALORES TIPADOS ────────────────────────────────────────
    // O Firestore REST embrulha cada valor no seu tipo:
    //   "nick": { "stringValue": "bane" }, "lastSeen": { "integerValue": "1738..." }

    private static JsonObject BuildFields(Dictionary<string, object?> fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields) o[k] = BuildValue(v);
        return o;
    }

    private static JsonObject BuildValue(object? v) => v switch
    {
        null          => new JsonObject { ["nullValue"] = null },
        string s      => new JsonObject { ["stringValue"] = s },
        bool b        => new JsonObject { ["booleanValue"] = b },
        int i         => new JsonObject { ["integerValue"] = i.ToString() },
        long l        => new JsonObject { ["integerValue"] = l.ToString() },
        double d      => new JsonObject { ["doubleValue"] = d },
        float f       => new JsonObject { ["doubleValue"] = (double)f },
        _             => new JsonObject { ["stringValue"] = v.ToString() ?? "" },
    };

    private static Dictionary<string, object?> ParseFields(JsonNode? fields)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (fields is not JsonObject obj) return d;
        foreach (var (k, v) in obj) d[k] = ParseValue(v);
        return d;
    }

    private static object? ParseValue(JsonNode? v)
    {
        if (v is not JsonObject o) return null;
        if (o.ContainsKey("nullValue")) return null;
        if (o.TryGetPropertyValue("stringValue", out var s)) return s?.GetValue<string>();
        if (o.TryGetPropertyValue("booleanValue", out var b)) return b?.GetValue<bool>();
        if (o.TryGetPropertyValue("integerValue", out var i))
            return long.TryParse(i?.GetValue<string>(), out var l) ? l : 0L;
        if (o.TryGetPropertyValue("doubleValue", out var dd)) return dd?.GetValue<double>();
        return null;
    }

    // ─── ACESSO TIPADO (helpers de leitura) ──────────────────────────────────
    public static string Str(Dictionary<string, object?> f, string key, string fallback = "")
        => f.TryGetValue(key, out var v) && v is string s ? s : fallback;

    public static long Num(Dictionary<string, object?> f, string key, long fallback = 0)
        => f.TryGetValue(key, out var v) && v is long l ? l : fallback;

    public static bool Flag(Dictionary<string, object?> f, string key, bool fallback = false)
        => f.TryGetValue(key, out var v) && v is bool b ? b : fallback;
}

public sealed class FirestoreException : Exception
{
    public FirestoreException(string message) : base(message) { }

    /// <summary>true quando a rules recusou — sinal de que falta publicar as rules.</summary>
    public bool IsPermissionDenied =>
        Message.Contains("403") || Message.Contains("PERMISSION_DENIED", StringComparison.OrdinalIgnoreCase);
}
