using System.Text;

namespace Primicord;

public sealed class ChatMessage
{
    public string Id = "";
    public string Nick = "";
    public string Text = "";
    public long At;

    public DateTime Local => DateTimeOffset.FromUnixTimeMilliseconds(At).LocalDateTime;
}

public sealed class MemberPresence
{
    public string Nick = "";
    public long LastSeen;
    public string Room = "";          // sala de voz em que esta, "" se nenhuma
    public bool Online => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastSeen < 150_000;
}

/// <summary>
/// Chat geral, mensagens diretas e presenca global (quem esta com o app aberto).
/// </summary>
/// <remarks>
/// Tudo nas replicas SQLite do Primicord, por polling. Ids de
/// mensagem sao `{timestamp:D13}-{aleatorio}`, entao a ordem alfabetica do id JA e a
/// ordem cronologica — da pra pedir as 60 mais recentes com `orderBy=__name__ desc`
/// em vez de baixar o historico inteiro.
///
/// PRIVACIDADE — LEIA: as DMs NAO sao privadas de verdade. Elas sao replicadas
/// entre os PCs do grupo para sobreviver a quedas, sem criptografia ponta-a-ponta
/// por conversa. Serve pra combinar jogo, nao pra segredo.
/// </remarks>
public sealed class ChatService
{
    public const string GeneralChannel = "geral";
    private const int RecentCount = 60;

    private readonly IDocumentStore _fs;
    private readonly string _myNick;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly Dictionary<string, ChatCache> _cache = new(StringComparer.Ordinal);

    private sealed class ChatCache
    {
        public bool Loaded;
        public long LatestAt;
        public readonly List<ChatMessage> Messages = new();
    }

    public ChatService(IDocumentStore fs, string myNick)
    {
        _fs = fs;
        _myNick = myNick.ToLowerInvariant();
    }

    // ─── CAMINHOS ────────────────────────────────────────────────────────────

    private static string ChannelMsgs(string channel) => $"pc_chat/{channel}/msgs";

    /// <summary>Canal textual isolado da sala, dentro do sistema de chat existente.</summary>
    public static string RoomChannel(string roomId)
        => "sala-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(roomId))
        ).AsSpan(0, 10).ToString().ToLowerInvariant();

    /// <summary>
    /// Caminho da DM. A chave e o par de nicks em ordem alfabetica, pros dois lados
    /// caírem sempre na mesma conversa (bane+juca e juca+bane = "bane__juca").
    /// </summary>
    public static string DmKey(string a, string b)
    {
        a = a.ToLowerInvariant(); b = b.ToLowerInvariant();
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}__{b}" : $"{b}__{a}";
    }

    private static string DmMsgs(string a, string b) => $"pc_dm/{DmKey(a, b)}/msgs";

    private static string NewMessageId()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D13")
         + "-" + Random.Shared.Next(0x1000, 0xFFFF).ToString("x4");

    // ─── LER ─────────────────────────────────────────────────────────────────

    public Task<List<ChatMessage>> ReadChannelAsync(string channel, CancellationToken ct = default)
        => ReadAsync($"pc_chat/{channel}", ct);

    public Task<List<ChatMessage>> ReadDmAsync(string other, CancellationToken ct = default)
        => ReadAsync($"pc_dm/{DmKey(_myNick, other)}", ct);

    /// <summary>Le as mensagens mais recentes; a ordenacao e o limite vao no servidor.</summary>
    private async Task<List<ChatMessage>> ReadAsync(string parentPath, CancellationToken ct)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_cache.TryGetValue(parentPath, out var cache))
            {
                cache = new ChatCache();
                _cache[parentPath] = cache;
            }

            List<(string Id, Dictionary<string, object?> Fields)> docs = cache.Loaded
                ? await _fs.QuerySinceAsync(parentPath, "msgs", "at", cache.LatestAt,
                                            RecentCount, ct).ConfigureAwait(false)
                : await _fs.QueryAsync(parentPath, "msgs", "at", descending: true,
                                       RecentCount, ct).ConfigureAwait(false);

            var byId = cache.Messages.ToDictionary(m => m.Id, StringComparer.Ordinal);
            foreach (var (id, f) in docs)
            {
                byId[id] = new ChatMessage
                {
                    Id = id,
                    Nick = Firestore.Str(f, "nick", "?"),
                    Text = Firestore.Str(f, "text"),
                    At = Firestore.Num(f, "at"),
                };
            }

            cache.Messages.Clear();
            cache.Messages.AddRange(byId.Values.OrderBy(m => m.At).ThenBy(m => m.Id));
            if (cache.Messages.Count > RecentCount)
                cache.Messages.RemoveRange(0, cache.Messages.Count - RecentCount);
            cache.LatestAt = cache.Messages.Count > 0 ? cache.Messages[^1].At : 0;
            cache.Loaded = true;
            return cache.Messages.ToList();
        }
        finally { _readGate.Release(); }
    }

    // ─── ESCREVER ────────────────────────────────────────────────────────────

    public Task SendToChannelAsync(string channel, string text, CancellationToken ct = default)
        => SendAsync(ChannelMsgs(channel), text, ct);

    public Task SendDmAsync(string other, string text, CancellationToken ct = default)
        => SendAsync(DmMsgs(_myNick, other), text, ct);

    private async Task SendAsync(string path, string text, CancellationToken ct)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 500) text = text[..500];

        await _fs.SetAsync($"{path}/{NewMessageId()}", new Dictionary<string, object?>
        {
            ["nick"] = _myNick,
            ["text"] = text,
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        }, ct: ct).ConfigureAwait(false);
    }

    // ─── PRESENCA GLOBAL ─────────────────────────────────────────────────────

    /// <summary>Diz "estou online" (e em que sala de voz, se estiver em alguma).</summary>
    public async Task HeartbeatAsync(string room, CancellationToken ct = default)
    {
        await _fs.SetAsync($"pc_presence/{_myNick}", new Dictionary<string, object?>
        {
            ["nick"] = _myNick,
            ["lastSeen"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["room"] = room ?? "",
        }, mergeFields: true, ct).ConfigureAwait(false);
    }

    public async Task ClearPresenceAsync()
    {
        try { await _fs.DeleteAsync($"pc_presence/{_myNick}").ConfigureAwait(false); } catch { }
    }

    public async Task<Dictionary<string, MemberPresence>> ReadPresenceAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, MemberPresence>(StringComparer.OrdinalIgnoreCase);
        var docs = await _fs.ListAsync("pc_presence", 100, ct).ConfigureAwait(false);
        foreach (var (id, f) in docs)
        {
            map[id] = new MemberPresence
            {
                Nick = Firestore.Str(f, "nick", id),
                LastSeen = Firestore.Num(f, "lastSeen"),
                Room = Firestore.Str(f, "room"),
            };
        }
        return map;
    }
}
