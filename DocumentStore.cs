namespace Primicord;

/// <summary>
/// A pequena lousa compartilhada usada por salas, presenca, chat e sinalizacao.
/// A 0.6.9 tem duas implementacoes: o mini servidor do Primicord (preferencial)
/// e o Firestore (compatibilidade durante a migracao).
/// </summary>
public interface IDocumentStore
{
    Task<List<(string Id, Dictionary<string, object?> Fields)>> ListAsync(
        string collectionPath, int pageSize = 100, CancellationToken ct = default,
        string? orderBy = null);

    Task<Dictionary<string, object?>?> GetAsync(
        string docPath, CancellationToken ct = default);

    Task SetAsync(string docPath, Dictionary<string, object?> fields,
                  bool mergeFields = false, CancellationToken ct = default);

    Task<List<(string Id, Dictionary<string, object?> Fields)>> QueryAsync(
        string parentPath, string collectionId, string orderField, bool descending, int limit,
        CancellationToken ct = default);

    Task<List<(string Id, Dictionary<string, object?> Fields)>> QuerySinceAsync(
        string parentPath, string collectionId, string orderField, long sinceInclusive, int limit,
        CancellationToken ct = default);

    Task DeleteAsync(string docPath, CancellationToken ct = default);
}

public class DocumentStoreException : Exception
{
    public DocumentStoreException(string message, Exception? inner = null) : base(message, inner) { }

    public virtual bool IsPermissionDenied => false;
    public virtual bool IsQuotaExceeded => false;
    public virtual bool IsUnavailable => false;
    public bool IsTransient => IsQuotaExceeded || IsUnavailable;
}
