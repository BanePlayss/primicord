using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Primicord;

public enum GameServerKind { MinecraftJava, Tcp }
public enum GameServerState { Unknown, Online, Reachable, Unreachable, InvalidResponse }

public sealed record GameServerEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string Game { get; init; } = "Minecraft Java";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 25565;
    public GameServerKind Kind { get; init; } = GameServerKind.MinecraftJava;
    [JsonIgnore] public string Endpoint => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public GameServerEntry Validated()
    {
        string host = (Host ?? "").Trim();
        if (host.StartsWith('[') && host.EndsWith(']')) host = host[1..^1];
        if (host.Length is 0 or > 253 || host.Contains('/') || host.Contains('\\') || host.Any(char.IsWhiteSpace)
            || Uri.CheckHostName(host) == UriHostNameType.Unknown)
            throw new ArgumentException("Informe apenas o IP ou nome do host, sem http:// e sem a porta.");
        if (Port is < 1 or > 65535) throw new ArgumentException("A porta deve estar entre 1 e 65535.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 64)
            throw new ArgumentException("O nome do servidor deve ter de 1 a 64 caracteres.");
        if (string.IsNullOrWhiteSpace(Game) || Game.Trim().Length > 64)
            throw new ArgumentException("Informe o nome do jogo (ate 64 caracteres).");
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Tipo de verificacao invalido.");
        return this with { Host = host, Name = Name.Trim(), Game = Game.Trim(),
            Id = Guid.TryParse(Id, out var id) ? id.ToString("N") : Guid.NewGuid().ToString("N") };
    }
}

/// <summary>Explicit, local-only directory. No discovery scan, backend writes, or invented servers.</summary>
public sealed class GameServerDirectory
{
    public const int MaximumServers = 64;
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public string FilePath => _path;
    public GameServerDirectory(string? path = null) => _path = path ?? Path.Combine(AppEnv.DataDir, "game-servers.json");

    public IReadOnlyList<GameServerEntry> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return Array.Empty<GameServerEntry>();
            using var stream = File.OpenRead(_path);
            if (stream.Length > 128 * 1024) throw new InvalidDataException("O cadastro de servidores ultrapassa 128 KiB.");
            var entries = JsonSerializer.Deserialize<List<GameServerEntry>>(stream, JsonOptions)
                ?? throw new InvalidDataException("O cadastro de servidores deve ser uma lista JSON.");
            return Validate(entries);
        }
    }

    public void Save(IEnumerable<GameServerEntry> entries)
    {
        lock (_gate)
        {
            var clean = Validate(entries);
            string parent = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(parent);
            string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, clean, JsonOptions);
                    stream.Flush(true);
                }
                File.Move(temp, _path, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static IReadOnlyList<GameServerEntry> Validate(IEnumerable<GameServerEntry> entries)
    {
        var clean = entries.Take(MaximumServers + 1).Select(e =>
            e?.Validated() ?? throw new InvalidDataException("Entrada de servidor vazia.")).ToList();
        if (clean.Count > MaximumServers) throw new ArgumentException($"O limite e {MaximumServers} servidores.");
        if (clean.Select(e => e.Id).Distinct().Count() != clean.Count)
            throw new InvalidDataException("O cadastro contem identificadores duplicados.");
        return clean;
    }
}

public sealed record GameServerStatus(GameServerEntry Server, GameServerState State, DateTimeOffset CheckedAt,
                                      long? LatencyMs = null, int? Players = null, int? MaxPlayers = null,
                                      string? Version = null, string? Detail = null)
{
    public bool GameConfirmedOnline => State == GameServerState.Online;
}

/// <summary>Checks explicit endpoints with a single deadline for DNS, TCP and Minecraft status.</summary>
public sealed class GameServerProbe
{
    private const int MaxPacketBytes = 256 * 1024;
    private readonly TimeSpan _timeout;
    public GameServerProbe(TimeSpan? timeout = null) => _timeout = timeout ?? TimeSpan.FromSeconds(3);

    public async Task<GameServerStatus> CheckAsync(GameServerEntry entry, CancellationToken ct = default)
    {
        entry = entry.Validated();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        using var tcp = new TcpClient();
        var watch = Stopwatch.StartNew();
        bool connected = false;
        try
        {
            await tcp.ConnectAsync(entry.Host, entry.Port, deadline.Token).ConfigureAwait(false);
            connected = true;
            if (entry.Kind == GameServerKind.Tcp)
                return new(entry, GameServerState.Reachable, DateTimeOffset.Now, watch.ElapsedMilliseconds,
                    Detail: "Porta TCP acessivel. A partida e as vagas nao foram verificadas.");
            var stream = tcp.GetStream();
            using var handshake = new MemoryStream();
            WriteVarInt(handshake, 0); // Handshake packet ID.
            WriteVarInt(handshake, -1); // Status protocol discovery, not a login.
            byte[] host = Encoding.UTF8.GetBytes(entry.Host);
            WriteVarInt(handshake, host.Length); handshake.Write(host);
            handshake.WriteByte((byte)(entry.Port >> 8)); handshake.WriteByte((byte)entry.Port);
            WriteVarInt(handshake, 1); // Next state: status.
            using var request = new MemoryStream();
            WriteVarInt(request, (int)handshake.Length); handshake.Position = 0; handshake.CopyTo(request);
            request.WriteByte(1); request.WriteByte(0); // Empty status request, framed.
            await stream.WriteAsync(request.ToArray(), deadline.Token).ConfigureAwait(false);
            int packetLength = await ReadVarIntAsync(stream, deadline.Token).ConfigureAwait(false);
            if (packetLength < 2 || packetLength > MaxPacketBytes) throw new InvalidDataException("Tamanho de pacote Minecraft invalido.");
            var packet = new byte[packetLength];
            await stream.ReadExactlyAsync(packet, deadline.Token).ConfigureAwait(false);
            using var response = new MemoryStream(packet, false);
            if (ReadVarInt(response) != 0) throw new InvalidDataException("O endpoint nao retornou um status Minecraft.");
            int jsonLength = ReadVarInt(response);
            if (jsonLength < 2 || jsonLength != response.Length - response.Position)
                throw new InvalidDataException("Resposta Minecraft incompleta ou invalida.");
            using var document = JsonDocument.Parse(packet.AsMemory((int)response.Position, jsonLength),
                new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Object || !version.TryGetProperty("name", out var versionName)
                || versionName.ValueKind != JsonValueKind.String || !version.TryGetProperty("protocol", out var protocol)
                || !protocol.TryGetInt32(out _))
                throw new InvalidDataException("O endpoint respondeu, mas nao confirmou um servidor Minecraft.");
            int? online = null, maximum = null;
            if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
            {
                if (players.TryGetProperty("online", out var p) && p.TryGetInt32(out int n) && n >= 0) online = n;
                if (players.TryGetProperty("max", out var m) && m.TryGetInt32(out int max) && max >= 0) maximum = max;
            }
            return new(entry, GameServerState.Online, DateTimeOffset.Now, watch.ElapsedMilliseconds,
                online, maximum, CleanText(versionName.GetString(), 64),
                "Minecraft respondeu ao status. Entrada sujeita a versao, whitelist e vagas.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(entry, connected ? GameServerState.InvalidResponse : GameServerState.Unreachable, DateTimeOffset.Now,
                Detail: connected ? "TCP acessivel, mas o status do jogo nao respondeu a tempo." : "Sem resposta. Confira host, porta e conexao Tailscale.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException or InvalidOperationException or ArgumentException)
        {
            return new(entry, connected ? GameServerState.InvalidResponse : GameServerState.Unreachable, DateTimeOffset.Now,
                Detail: connected ? "TCP acessivel; resposta de jogo ausente ou invalida." : "Nao foi possivel acessar este endpoint.");
        }
    }

    public async Task<IReadOnlyList<GameServerStatus>> CheckAllAsync(IEnumerable<GameServerEntry> entries,
        CancellationToken ct = default)
    {
        var items = entries.Take(GameServerDirectory.MaximumServers).ToArray();
        var results = new GameServerStatus[items.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) => results[i] = await CheckAsync(items[i], token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    private static string CleanText(string? value, int limit) => new((value ?? "")
        .Where(c => !char.IsControl(c)).Take(limit).ToArray());
    private static void WriteVarInt(Stream stream, int value)
    {
        uint remaining = unchecked((uint)value);
        do { byte next = (byte)(remaining & 0x7F); remaining >>= 7; stream.WriteByte((byte)(remaining == 0 ? next : next | 0x80)); }
        while (remaining != 0);
    }
    private static int ReadVarInt(Stream stream)
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            int b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            if (i == 4 && (b & 0xF0) != 0) throw new InvalidDataException("VarInt invalido.");
            result |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return unchecked((int)result);
        }
        throw new InvalidDataException("VarInt muito longo.");
    }
    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[5];
        for (int i = 0; i < bytes.Length; i++)
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(i, 1), ct).ConfigureAwait(false);
            if ((bytes[i] & 0x80) == 0) return ReadVarInt(new MemoryStream(bytes, 0, i + 1, false));
        }
        throw new InvalidDataException("VarInt muito longo.");
    }
}
