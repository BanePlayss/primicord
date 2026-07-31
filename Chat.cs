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
    public bool Online => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastSeen < 60_000;
}

/// <summary>
/// Chat geral, mensagens diretas e presenca global (quem esta com o app aberto).
/// </summary>
/// <remarks>
/// Tudo no Firestore, por POLLING (o REST nao tem listener em tempo real). Ids de
/// mensagem sao `{timestamp:D13}-{aleatorio}`, entao a ordem alfabetica do id JA e a
/// ordem cronologica — da pra pedir as 60 mais recentes com `orderBy=__name__ desc`
/// em vez de baixar o historico inteiro.
///
/// PRIVACIDADE — LEIA: as DMs NAO sao privadas de verdade. O app nao usa Firebase
/// Auth (o login e nick+senha conferido no cliente), entao a rules nao tem como
/// saber quem esta pedindo e a leitura precisa ficar publica. Qualquer um que saiba
/// mexer no Firestore consegue ler as conversas. Serve pra combinar jogo, nao pra
/// segredo. A UI avisa isso no topo da DM.
/// </remarks>
public sealed class ChatService
{
    public const string GeneralChannel = "geral";
    private const int RecentCount = 60;

    private readonly Firestore _fs;
    private readonly string _myNick;

    public ChatService(Firestore fs, string myNick)
    {
        _fs = fs;
        _myNick = myNick.ToLowerInvariant();
    }

    // ─── CAMINHOS ────────────────────────────────────────────────────────────

    private static string ChannelMsgs(string channel) => $"pc_chat/{channel}/msgs";

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
        List<(string Id, Dictionary<string, object?> Fields)> docs;
        try
        {
            docs = await _fs.QueryAsync(parentPath, "msgs", "at", descending: true, RecentCount, ct)
                            .ConfigureAwait(false);
        }
        catch (FirestoreException ex) when (!ex.IsPermissionDenied)
        {
            // Conversa nova ainda nao tem a colecao — listar devolve vazio sem erro.
            Log.Write("chat: consulta falhou, tentando listagem simples: " + ex.Message);
            docs = await _fs.ListAsync(parentPath + "/msgs", RecentCount, ct).ConfigureAwait(false);
        }
        var msgs = new List<ChatMessage>(docs.Count);
        foreach (var (id, f) in docs)
        {
            msgs.Add(new ChatMessage
            {
                Id = id,
                Nick = Firestore.Str(f, "nick", "?"),
                Text = Firestore.Str(f, "text"),
                At = Firestore.Num(f, "at"),
            });
        }
        // Veio do mais novo pro mais velho (ou sem ordem, no fallback): normaliza.
        msgs.Sort((x, y) => x.At != y.At ? x.At.CompareTo(y.At) : string.CompareOrdinal(x.Id, y.Id));
        if (msgs.Count > RecentCount) msgs.RemoveRange(0, msgs.Count - RecentCount);
        return msgs;
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
