using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using Primicord.Server;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("PRIMICORD_SERVER_URLS")
                       ?? "http://0.0.0.0:8765");

string dataDir = Environment.GetEnvironmentVariable("PRIMICORD_SERVER_DATA_DIR")
                 ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "PrimicordServer");
Directory.CreateDirectory(dataDir);
var store = new SqliteDocumentStore(Path.Combine(dataDir, "primicord.db"));
await store.InitializeAsync();

var app = builder.Build();

// O servidor nao e publico: so aceita o proprio PC ou enderecos da tailnet.
app.Use(async (context, next) =>
{
    IPAddress? remote = context.Connection.RemoteIpAddress;
    if (!TailnetAddress.IsAllowed(remote))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("O mini servidor aceita somente conexoes do Tailscale.");
        return;
    }
    await next();
});

// Handlers trabalham diretamente com JsonNode. Isso deixa o executavel aparavel
// (21 MB em vez de 101 MB) sem reflection de DTO quebrando no PC do usuario.
app.MapGet("/health", async context =>
{
    await ServerJson.WriteAsync(context, new JsonObject
    {
        ["ok"] = true,
        ["version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.6.21",
        ["storage"] = "sqlite",
    });
});

app.MapGet("/v1/doc", async context =>
{
    string path = ServerJson.RequiredQuery(context, "path");
    JsonObject? fields = await store.GetAsync(path, context.RequestAborted);
    if (fields == null) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
    await ServerJson.WriteAsync(context, fields);
});

app.MapPut("/v1/doc", async context =>
{
    string path = ServerJson.RequiredQuery(context, "path");
    bool merge = bool.TryParse(context.Request.Query["merge"], out bool value) && value;
    JsonObject fields = await ServerJson.ReadObjectAsync(context);
    await store.SetAsync(path, fields, merge, context.RequestAborted);
    context.Response.StatusCode = StatusCodes.Status204NoContent;
});

app.MapDelete("/v1/doc", async context =>
{
    string path = ServerJson.RequiredQuery(context, "path");
    await store.DeleteAsync(path, context.RequestAborted);
    context.Response.StatusCode = StatusCodes.Status204NoContent;
});

app.MapGet("/v1/list", async context =>
{
    string path = ServerJson.RequiredQuery(context, "path");
    int limit = int.TryParse(context.Request.Query["limit"], out int value)
        ? Math.Clamp(value, 1, 500) : 100;
    string? orderBy = context.Request.Query["orderBy"].FirstOrDefault();
    var rows = await store.ListAsync(path, limit, orderBy, context.RequestAborted);
    await ServerJson.WriteRowsAsync(context, rows);
});

app.MapPost("/v1/query", async context =>
{
    JsonObject body = await ServerJson.ReadObjectAsync(context);
    var query = new StoreQuery
    {
        ParentPath = ServerJson.String(body, "parentPath"),
        CollectionId = ServerJson.String(body, "collectionId"),
        OrderField = ServerJson.String(body, "orderField"),
        Descending = ServerJson.Bool(body, "descending"),
        Limit = ServerJson.Int(body, "limit", 60),
        SinceInclusive = ServerJson.NullableLong(body, "sinceInclusive"),
    };
    var rows = await store.QueryAsync(query, context.RequestAborted);
    await ServerJson.WriteRowsAsync(context, rows);
});

// Anti-entropia entre os PCs do grupo. Inclui tombstones para exclusoes feitas
// enquanto alguma replica estava offline.
app.MapGet("/v1/snapshot", async context =>
{
    var documents = await store.ExportAsync(context.RequestAborted);
    var array = new JsonArray();
    foreach (var document in documents)
        array.Add(new JsonObject
        {
            ["path"] = document.Path,
            ["fields"] = document.Fields.DeepClone(),
            ["updatedAt"] = document.UpdatedAt,
            ["deleted"] = document.Deleted,
        });
    await ServerJson.WriteAsync(context, array);
});

app.MapPost("/v1/snapshot", async context =>
{
    JsonArray array = await ServerJson.ReadArrayAsync(context);
    if (array.Count > 20_000) throw new BadHttpRequestException("Snapshot grande demais.");
    var documents = new List<SnapshotDocument>(array.Count);
    foreach (JsonNode? node in array)
    {
        if (node is not JsonObject item) continue;
        string path = ServerJson.String(item, "path");
        if (path.Length == 0) continue;
        documents.Add(new SnapshotDocument(
            path,
            item["fields"] as JsonObject ?? new JsonObject(),
            item["updatedAt"]?.GetValue<long>() ?? 0,
            item["deleted"]?.GetValue<bool>() ?? false));
    }
    await store.ImportAsync(documents, context.RequestAborted);
    context.Response.StatusCode = StatusCodes.Status204NoContent;
});

app.Run();

namespace Primicord.Server
{
    public sealed class StoreQuery
    {
        public string ParentPath { get; set; } = "";
        public string CollectionId { get; set; } = "";
        public string OrderField { get; set; } = "";
        public bool Descending { get; set; }
        public int Limit { get; set; } = 60;
        public long? SinceInclusive { get; set; }
    }

    internal static class ServerJson
    {
        public static string RequiredQuery(HttpContext context, string key)
        {
            string value = context.Request.Query[key].ToString().Trim();
            if (value.Length == 0) throw new BadHttpRequestException("Parametro obrigatorio: " + key);
            return value;
        }

        public static async Task<JsonObject> ReadObjectAsync(HttpContext context)
            => await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                   as JsonObject
               ?? throw new BadHttpRequestException("O corpo precisa ser um objeto JSON.");

        public static async Task<JsonArray> ReadArrayAsync(HttpContext context)
            => await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted)
                   as JsonArray
               ?? throw new BadHttpRequestException("O corpo precisa ser uma lista JSON.");

        public static async Task WriteRowsAsync(HttpContext context, IEnumerable<StoreRow> rows)
        {
            var array = new JsonArray();
            foreach (var row in rows)
                array.Add((JsonNode)new JsonObject
                {
                    ["id"] = row.Id,
                    ["fields"] = row.Fields.DeepClone(),
                });
            await WriteAsync(context, array);
        }

        public static async Task WriteAsync(HttpContext context, JsonNode node)
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync(node.ToJsonString(), context.RequestAborted);
        }

        public static string String(JsonObject obj, string key)
            => obj[key]?.GetValue<string>() ?? "";
        public static bool Bool(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<bool>(out bool value) && value;
        public static int Int(JsonObject obj, string key, int fallback)
            => obj[key] is JsonValue v && v.TryGetValue<int>(out int value) ? value : fallback;
        public static long? NullableLong(JsonObject obj, string key)
            => obj[key] is JsonValue v && v.TryGetValue<long>(out long value) ? value : null;
    }
}
