using Primicord;

static Dictionary<string, object?> Peer(string account, string nick, long seen, long joined, string endpoint)
    => new()
    {
        ["accountId"] = account, ["nick"] = nick, ["lastSeen"] = seen,
        ["__serverUpdatedAt"] = seen, ["joinedAt"] = joined,
        ["locEps"] = endpoint, ["left"] = false,
    };

const long now = 1_000_000;
var docs = new List<(string, Dictionary<string, object?>)>
{
    ("bane-old", Peer("bane", "bane", now - 2_000, now - 2_000, "100.64.0.2:5000")),
    ("bane-new", Peer("bane", "bane", now - 300, now - 300, "100.64.0.2:6000")),
    ("mohamed", Peer("mohamed", "mohamed", now - 400, now - 400, "100.64.0.3:6000")),
    ("gone", Peer("gone", "gone", now - RoomPresence.LeaseMs - 1, now - 20_000, "100.64.0.4:6000")),
    ("clock", Peer("clock", "clock", now + RoomPresence.LeaseMs + 1, now + RoomPresence.LeaseMs + 1, "100.64.0.5:6000")),
};
var current = RoomPresence.Current(docs, now);
if (current.Count != 2 || current.All(p => p.Id != "bane-new") || current.Any(p => p.Id == "bane-old"))
    throw new InvalidOperationException("Presence lease/deduplication failed.");
Console.WriteLine("PASS: stale, future and duplicate presence entries are filtered deterministically.");
