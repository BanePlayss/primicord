namespace Primicord;

public sealed record ClusterEndpoint(string Identity, string Url);

public interface IClusterEndpointProvider
{
    Task<IReadOnlyList<ClusterEndpoint>> GetEndpointsAsync(CancellationToken ct = default);
}

/// <summary>Descobre automaticamente os Primicords online pela malha Tailscale.</summary>
public sealed class TailscaleClusterEndpointProvider : IClusterEndpointProvider
{
    private readonly string _fallbackUrl;

    public TailscaleClusterEndpointProvider(string fallbackUrl)
        => _fallbackUrl = Config.NormalizeCoordServerUrl(fallbackUrl);

    public async Task<IReadOnlyList<ClusterEndpoint>> GetEndpointsAsync(
        CancellationToken ct = default)
    {
        var nodes = await TailscaleIntegration.GetOnlineNodesAsync(ct).ConfigureAwait(false);
        if (nodes.Count > 0)
            return nodes.Select(node => new ClusterEndpoint(
                    node.Address,
                    node.IsSelf ? "http://127.0.0.1:8765" : $"http://{node.Address}:8765"))
                .ToList();

        // Sem status do Tailscale, preserva o endpoint antigo e tenta a replica
        // local em seguida. Isso mantem o modo de compatibilidade utilizavel.
        return new[]
        {
            new ClusterEndpoint("fallback", _fallbackUrl),
            new ClusterEndpoint("local", "http://127.0.0.1:8765"),
        }.DistinctBy(endpoint => endpoint.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// Lousa distribuida do Primicord. Leituras usam o primeiro PC saudavel numa
/// ordem deterministica; escritas vao a todas as replicas online. Se o primeiro
/// PC cair, a mesma operacao continua no seguinte sem tocar no audio P2P.
/// </summary>
public sealed class ClusterDocumentStore : IDocumentStore
{
    private readonly IClusterEndpointProvider _endpoints;
    private readonly IDocumentStore? _fallback;
    private readonly Func<ClusterEndpoint, IClusterReplica> _factory;
    private readonly Dictionary<string, IClusterReplica> _stores =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private IReadOnlyList<IClusterReplica> _healthy = Array.Empty<IClusterReplica>();
    private DateTimeOffset _refreshAt;
    private string _healthySignature = "";
    private string _activeName = "procurando replicas";

    public ClusterDocumentStore(IClusterEndpointProvider endpoints, IDocumentStore? fallback,
                                Func<ClusterEndpoint, IClusterReplica>? factory = null)
    {
        _endpoints = endpoints;
        _fallback = fallback;
        _factory = factory ?? (endpoint => new MiniServerStore(endpoint.Url));
    }

    public string ActiveName => _activeName;

    private async Task<IReadOnlyList<IClusterReplica>> GetHealthyAsync(
        CancellationToken ct, bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow < _refreshAt) return _healthy;

        bool synchronize = false;
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTimeOffset.UtcNow < _refreshAt) return _healthy;
            IReadOnlyList<ClusterEndpoint> endpoints =
                await _endpoints.GetEndpointsAsync(ct).ConfigureAwait(false);
            var candidates = new List<IClusterReplica>();
            foreach (ClusterEndpoint endpoint in endpoints)
            {
                if (!_stores.TryGetValue(endpoint.Identity, out IClusterReplica? store)
                    || !store.BaseUrl.Equals(Normalize(endpoint.Url), StringComparison.OrdinalIgnoreCase))
                {
                    store = _factory(endpoint);
                    _stores[endpoint.Identity] = store;
                }
                candidates.Add(store);
            }

            Task<bool>[] probes = candidates.Select(store => store.IsAvailableAsync(ct)).ToArray();
            bool[] available = probes.Length == 0 ? Array.Empty<bool>()
                : await Task.WhenAll(probes).ConfigureAwait(false);
            _healthy = candidates.Where((_, index) => available[index]).ToList();
            _refreshAt = DateTimeOffset.UtcNow.AddSeconds(_healthy.Count > 0 ? 10 : 3);
            _activeName = _healthy.Count switch
            {
                0 when _fallback == null => "mini servidor indisponivel",
                0 => "servidor de compatibilidade",
                1 => "1 replica Primicord",
                _ => $"{_healthy.Count} replicas Primicord",
            };
            string signature = string.Join("|", _healthy.Select(replica => replica.BaseUrl));
            // Escritas ao vivo ja vao a todos. Snapshot so e necessario quando a
            // topologia muda (por exemplo, um PC volta depois de ficar offline).
            synchronize = _healthy.Count > 1
                       && !signature.Equals(_healthySignature, StringComparison.OrdinalIgnoreCase);
            _healthySignature = signature;
        }
        finally { _refreshGate.Release(); }

        if (synchronize) await SynchronizeAsync(_healthy, ct).ConfigureAwait(false);
        return _healthy;
    }

    private async Task<T> ReadAsync<T>(Func<IDocumentStore, Task<T>> action,
                                       CancellationToken ct)
    {
        IReadOnlyList<IClusterReplica> replicas = await GetHealthyAsync(ct).ConfigureAwait(false);
        foreach (IClusterReplica replica in replicas)
        {
            try { return await action(replica).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (IsUnavailable(ex))
            {
                Log.Write("replica caiu durante leitura: " + replica.BaseUrl + " — " + ex.Message);
                Invalidate();
            }
        }

        // Uma descoberta imediata pega o sucessor sem esperar os dez segundos do cache.
        if (replicas.Count > 0)
        {
            foreach (IClusterReplica replica in await GetHealthyAsync(ct, force: true).ConfigureAwait(false))
                try { return await action(replica).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (IsUnavailable(ex)) { Invalidate(); }
        }
        if (_fallback != null) return await action(_fallback).ConfigureAwait(false);
        throw new ClusterUnavailableException();
    }

    private async Task WriteAsync(Func<IDocumentStore, Task> action, CancellationToken ct)
    {
        IReadOnlyList<IClusterReplica> replicas = await GetHealthyAsync(ct).ConfigureAwait(false);
        if (replicas.Count == 0)
        {
            if (_fallback == null) throw new ClusterUnavailableException();
            await action(_fallback).ConfigureAwait(false);
            return;
        }

        Task<bool>[] writes = replicas.Select(replica => TryWriteAsync(replica, action, ct)).ToArray();
        bool[] results = await Task.WhenAll(writes).ConfigureAwait(false);
        if (results.Any(success => success))
        {
            if (results.Any(success => !success)) Invalidate();
            return;
        }

        Invalidate();
        IReadOnlyList<IClusterReplica> refreshed =
            await GetHealthyAsync(ct, force: true).ConfigureAwait(false);
        foreach (IClusterReplica replica in refreshed)
            if (await TryWriteAsync(replica, action, ct).ConfigureAwait(false)) return;

        if (_fallback == null) throw new ClusterUnavailableException();
        await action(_fallback).ConfigureAwait(false);
    }

    private static async Task<bool> TryWriteAsync(IClusterReplica replica,
                                                   Func<IDocumentStore, Task> action,
                                                   CancellationToken ct)
    {
        try
        {
            await action(replica).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            Log.Write("replica nao recebeu escrita: " + replica.BaseUrl + " — " + ex.Message);
            return false;
        }
    }

    private async Task SynchronizeAsync(IReadOnlyList<IClusterReplica> replicas,
                                        CancellationToken ct)
    {
        if (!await _syncGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            List<ReplicaDocument>?[] snapshots = await Task.WhenAll(
                replicas.Select(replica => ExportSafeAsync(replica, ct))).ConfigureAwait(false);

            var merged = new Dictionary<string, ReplicaDocument>(StringComparer.Ordinal);
            foreach (ReplicaDocument document in snapshots.Where(snapshot => snapshot != null)
                         .SelectMany(snapshot => snapshot!))
                if (!merged.TryGetValue(document.Path, out ReplicaDocument? current)
                    || document.UpdatedAt > current.UpdatedAt
                    || document.UpdatedAt == current.UpdatedAt && document.Deleted && !current.Deleted)
                    merged[document.Path] = document;

            if (merged.Count == 0) return;
            IReadOnlyList<ReplicaDocument> documents = merged.Values.ToList();
            await Task.WhenAll(replicas.Select(async replica =>
            {
                try { await replica.ImportSnapshotAsync(documents, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                { Log.Write("replica nao recebeu snapshot: " + replica.BaseUrl + " — " + ex.Message); }
            })).ConfigureAwait(false);
        }
        finally { _syncGate.Release(); }
    }

    private static async Task<List<ReplicaDocument>?> ExportSafeAsync(
        IClusterReplica replica, CancellationToken ct)
    {
        try { return await replica.ExportSnapshotAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 0.6.13 e anteriores ainda respondem health/CRUD, mas nao possuem o
            // endpoint de snapshot. Eles participam da replicacao ao vivo ate atualizar.
            Log.Write("snapshot indisponivel em " + replica.BaseUrl + ": " + ex.Message);
            return null;
        }
    }

    public Task SynchronizeNowAsync(CancellationToken ct = default)
        => SynchronizeAsync(_healthy, ct);

    private void Invalidate() => _refreshAt = DateTimeOffset.MinValue;

    private static bool IsUnavailable(Exception ex)
        => ex is DocumentStoreException store ? store.IsUnavailable
         : ex is HttpRequestException or IOException or TimeoutException;

    private static string Normalize(string url) => url.TrimEnd('/') + "/";

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
        string collectionPath, int pageSize = 100, CancellationToken ct = default,
        string? orderBy = null)
        => ReadAsync(store => store.ListAsync(collectionPath, pageSize, ct, orderBy), ct);

    public Task<Dictionary<string, object?>?> GetAsync(string docPath,
                                                       CancellationToken ct = default)
        => ReadAsync(store => store.GetAsync(docPath, ct), ct);

    public Task SetAsync(string docPath, Dictionary<string, object?> fields,
                         bool mergeFields = false, CancellationToken ct = default)
        => WriteAsync(store => store.SetAsync(docPath, fields, mergeFields, ct), ct);

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
        string parentPath, string collectionId, string orderField, bool descending, int limit,
        CancellationToken ct = default)
        => ReadAsync(store => store.QueryAsync(parentPath, collectionId, orderField,
                                               descending, limit, ct), ct);

    public Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
        string parentPath, string collectionId, string orderField, long sinceInclusive, int limit,
        CancellationToken ct = default)
        => ReadAsync(store => store.QuerySinceAsync(parentPath, collectionId, orderField,
                                                    sinceInclusive, limit, ct), ct);

    public Task DeleteAsync(string docPath, CancellationToken ct = default)
        => WriteAsync(store => store.DeleteAsync(docPath, ct), ct);
}

/// <summary>
/// A malha local ainda nao tem uma replica pronta. O chamador recua e tenta de
/// novo; nunca transforma indisponibilidade local em milhares de leituras cloud.
/// </summary>
public sealed class ClusterUnavailableException : DocumentStoreException
{
    public ClusterUnavailableException()
        : base("O mini servidor do Primicord ainda nao esta disponivel.") { }

    public override bool IsUnavailable => true;
}
