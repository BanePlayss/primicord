using Primicord;
using Xunit;

namespace Primicord.Tests;

public sealed class ClusterDocumentStoreTests
{
    [Fact]
    public async Task Escrita_vai_a_todas_e_leitura_migra_quando_lider_cai()
    {
        var first = new FakeReplica("http://100.64.2.73:8765/");
        var second = new FakeReplica("http://100.100.8.10:8765/");
        var fallback = new FakeReplica("firestore");
        var stores = new Dictionary<string, FakeReplica>
        {
            ["bane"] = first,
            ["vitinho"] = second,
        };
        var cluster = new ClusterDocumentStore(
            new FixedEndpoints(stores.Select(pair =>
                new ClusterEndpoint(pair.Key, pair.Value.BaseUrl)).ToList()),
            fallback,
            endpoint => stores[endpoint.Identity]);

        await cluster.SetAsync("pc_rooms/sala", new Dictionary<string, object?> { ["name"] = "Sala" });
        Assert.NotNull(await first.GetAsync("pc_rooms/sala"));
        Assert.NotNull(await second.GetAsync("pc_rooms/sala"));

        first.Available = false;
        Dictionary<string, object?>? room = await cluster.GetAsync("pc_rooms/sala");

        Assert.Equal("Sala", room!["name"]);
        Assert.Equal(0, fallback.Reads);
    }

    [Fact]
    public async Task Snapshot_preenche_replica_que_entrou_vazia()
    {
        var first = new FakeReplica("http://100.64.2.73:8765/");
        var second = new FakeReplica("http://100.100.8.10:8765/");
        await first.SetAsync("pc_rooms/antiga",
            new Dictionary<string, object?> { ["name"] = "Sala antiga" });
        var stores = new Dictionary<string, FakeReplica> { ["a"] = first, ["b"] = second };
        var cluster = new ClusterDocumentStore(
            new FixedEndpoints(stores.Select(pair => new ClusterEndpoint(pair.Key, pair.Value.BaseUrl)).ToList()),
            new FakeReplica("firestore"), endpoint => stores[endpoint.Identity]);

        await cluster.GetAsync("pc_rooms/antiga"); // descoberta tambem roda anti-entropia

        Assert.NotNull(await second.GetAsync("pc_rooms/antiga"));
        Assert.Equal("2 replicas Primicord", cluster.ActiveName);
    }

    [Fact]
    public async Task Cliente_antigo_sem_snapshot_ainda_participa_do_crud_ao_vivo()
    {
        var legacy = new FakeReplica("http://legacy/") { SnapshotsSupported = false };
        var current = new FakeReplica("http://current/");
        var stores = new Dictionary<string, FakeReplica> { ["legacy"] = legacy, ["current"] = current };
        var cluster = new ClusterDocumentStore(
            new FixedEndpoints(stores.Select(pair => new ClusterEndpoint(pair.Key, pair.Value.BaseUrl)).ToList()),
            new FakeReplica("firestore"), endpoint => stores[endpoint.Identity]);

        await cluster.SetAsync("pc_rooms/mista",
            new Dictionary<string, object?> { ["name"] = "Versoes mistas" });

        Assert.NotNull(await legacy.GetAsync("pc_rooms/mista"));
        Assert.NotNull(await current.GetAsync("pc_rooms/mista"));
    }

    private sealed class FixedEndpoints(IReadOnlyList<ClusterEndpoint> endpoints)
        : IClusterEndpointProvider
    {
        public Task<IReadOnlyList<ClusterEndpoint>> GetEndpointsAsync(CancellationToken ct = default)
            => Task.FromResult(endpoints);
    }

    private sealed class FakeReplica(string baseUrl) : IClusterReplica
    {
        private readonly Dictionary<string, ReplicaDocument> _documents = new(StringComparer.Ordinal);
        private long _clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public string BaseUrl { get; } = baseUrl;
        public bool Available { get; set; } = true;
        public bool SnapshotsSupported { get; set; } = true;
        public int Reads { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Available);

        public Task<Dictionary<string, object?>?> GetAsync(string docPath,
                                                           CancellationToken ct = default)
        {
            EnsureAvailable();
            Reads++;
            return Task.FromResult(_documents.TryGetValue(docPath, out ReplicaDocument? row)
                && !row.Deleted ? new Dictionary<string, object?>(row.Fields) : null);
        }

        public Task SetAsync(string docPath, Dictionary<string, object?> fields,
                             bool mergeFields = false, CancellationToken ct = default)
        {
            EnsureAvailable();
            var value = mergeFields && _documents.TryGetValue(docPath, out ReplicaDocument? old)
                ? new Dictionary<string, object?>(old.Fields)
                : new Dictionary<string, object?>();
            foreach (var pair in fields) value[pair.Key] = pair.Value;
            _documents[docPath] = new ReplicaDocument(docPath, value, Interlocked.Increment(ref _clock), false);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string docPath, CancellationToken ct = default)
        {
            EnsureAvailable();
            _documents[docPath] = new ReplicaDocument(docPath, new(), Interlocked.Increment(ref _clock), true);
            return Task.CompletedTask;
        }

        public Task<List<ReplicaDocument>> ExportSnapshotAsync(CancellationToken ct = default)
        {
            EnsureAvailable();
            if (!SnapshotsSupported) throw new MiniServerException("404", System.Net.HttpStatusCode.NotFound);
            return Task.FromResult(_documents.Values.ToList());
        }

        public Task ImportSnapshotAsync(IReadOnlyList<ReplicaDocument> documents,
                                        CancellationToken ct = default)
        {
            EnsureAvailable();
            if (!SnapshotsSupported) throw new MiniServerException("404", System.Net.HttpStatusCode.NotFound);
            foreach (ReplicaDocument row in documents)
                if (!_documents.TryGetValue(row.Path, out ReplicaDocument? old)
                    || row.UpdatedAt > old.UpdatedAt
                    || row.UpdatedAt == old.UpdatedAt && row.Deleted && !old.Deleted)
                    _documents[row.Path] = row;
            return Task.CompletedTask;
        }

        public Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
            string collectionPath, int pageSize = 100, CancellationToken ct = default,
            string? orderBy = null)
        {
            EnsureAvailable();
            string prefix = collectionPath.Trim('/') + "/";
            var rows = _documents.Values
                .Where(row => !row.Deleted && row.Path.StartsWith(prefix, StringComparison.Ordinal)
                    && !row.Path[prefix.Length..].Contains('/'))
                .Take(pageSize)
                .Select(row => (row.Path[prefix.Length..], new Dictionary<string, object?>(row.Fields)))
                .ToList();
            return Task.FromResult(rows);
        }

        public Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
            string parentPath, string collectionId, string orderField, bool descending, int limit,
            CancellationToken ct = default)
            => ListAsync((parentPath.Trim('/') + "/" + collectionId).Trim('/'), limit, ct);

        public Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
            string parentPath, string collectionId, string orderField, long sinceInclusive, int limit,
            CancellationToken ct = default)
            => QueryAsync(parentPath, collectionId, orderField, false, limit, ct);

        private void EnsureAvailable()
        {
            if (!Available) throw new MiniServerException("offline", null);
        }
    }
}
