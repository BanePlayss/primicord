using System.Text;

namespace Primicord;

/// <summary>Shared interpretation of presence for the lobby and the voice room.</summary>
public static class RoomPresence
{
    public const int LeaseMs = 15_000;

    // The existing login uses the canonical Primitivão nickname as account ID.
    public static string AccountKey(string nick) => nick.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    public static List<(string Id, Dictionary<string, object?> Fields)> Current(
        IEnumerable<(string Id, Dictionary<string, object?> Fields)> docs, long now)
        => docs.Where(d => IsFresh(d.Fields, now))
            .GroupBy(d => AccountKey(Firestore.Str(d.Fields, "accountId", Firestore.Str(d.Fields, "nick", d.Id))))
            .Select(g => g.OrderByDescending(d => Firestore.Num(d.Fields, "joinedAt") > 0)
                .ThenByDescending(d => Firestore.Num(d.Fields, "joinedAt", SeenAt(d.Fields)))
                .ThenByDescending(d => d.Id, StringComparer.Ordinal).First())
            .OrderBy(d => d.Id, StringComparer.Ordinal).ToList();

    public static long SeenAt(Dictionary<string, object?> f)
        => Firestore.Num(f, "__serverUpdatedAt", Firestore.Num(f, "lastSeen"));

    private static bool IsFresh(Dictionary<string, object?> f, long now)
    {
        long seen = SeenAt(f);
        return !Firestore.Flag(f, "left") && seen > 0 && now - seen <= LeaseMs && seen - now <= LeaseMs;
    }
}
