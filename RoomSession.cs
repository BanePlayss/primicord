using System.Net;
using System.Net.Sockets;

namespace Primicord;

/// <summary>Um primitivo do outro lado da linha.</summary>
public sealed class RemotePeer
{
    public string PeerId = "";
    public uint SenderId;
    public string Nick = "";
    public bool Muted;
    public bool Sharing;

    /// <summary>Enderecos onde ele PODE estar (publico + todos os locais).</summary>
    public readonly List<IPEndPoint> Candidates = new();

    /// <summary>O endereco que respondeu de verdade. null = ainda furando o NAT.</summary>
    public IPEndPoint? Locked;

    public long LastRecvTicks;
    public long LastSeenMs;

    public bool Connected => Locked != null &&
        (DateTime.UtcNow.Ticks - Interlocked.Read(ref LastRecvTicks)) < TimeSpan.TicksPerSecond * 6;
}

/// <summary>
/// A sala: malha UDP ponto-a-ponto entre todos os participantes.
/// </summary>
/// <remarks>
/// Cada um manda a propria voz DIRETO pra cada outro (malha). Sem servidor no meio:
/// o Firestore so serve pra trocar enderecos; depois disso o audio nao passa por
/// lugar nenhum alem dos dois PCs. Com 6 pessoas cada um sobe ~5x64kbps (~320kbps),
/// tranquilo em qualquer banda larga.
///
/// COMO DOIS ROTEADORES SE ATRAVESSAM (hole punching): cada lado descobre seu IP:porta
/// publico via STUN e publica no Firestore. Ai os dois passam a mandar pacotinhos um
/// pro outro ao mesmo tempo. O primeiro pacote de A morre no roteador de B — mas ele
/// abre no roteador de A a "porta de volta" pra B. Como B faz o mesmo, em uma ou duas
/// tentativas os dois lados ja tem o buraco aberto e os pacotes comecam a passar.
///
/// LIMITE CONHECIDO: se os DOIS lados estiverem atras de NAT simetrico (comum em 4G e
/// em alguns provedores), a porta publica muda por destino e o furo nao acontece. Nesse
/// caso so um servidor relay (TURN) resolve — nao temos um, entao o par nao conecta.
/// O app avisa na UI em vez de ficar mudo sem explicacao.
/// </remarks>
public sealed class RoomSession : IDisposable
{
    // ─── protocolo ───────────────────────────────────────────────────────────
    public const int HeaderBytes = 9;
    public const byte TypeVoice = 0;
    public const byte TypePunch = 1;   // "estou aqui, me responde"
    public const byte TypePunchAck = 2;   // "te ouvi"
    public const byte TypeBye = 3;   // saida limpa

    private const int PunchIntervalMs = 250;   // enquanto nao conectou
    private const int KeepAliveMs = 1000;  // depois de conectado (mantem o NAT aberto)
    private const int PresenceSyncMs = 2000;  // poll do Firestore
    private const int PeerStaleMs = 15000; // sem heartbeat no Firestore = saiu

    private readonly Firestore _fs;
    private readonly string _roomId;
    private readonly string _peerId;
    private readonly string _nick;

    private Socket? _socket;
    private Thread? _rxThread;
    private System.Threading.Timer? _punchTimer;
    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private bool _firestoreBacked = true;   // false no harness (rede pura, sem presenca)

    private readonly object _peersLock = new();
    private readonly Dictionary<uint, RemotePeer> _peers = new();

    private IPEndPoint? _publicEp;
    private readonly List<IPEndPoint> _localEps = new();
    private uint _mySenderId;
    private uint _voiceSeq;

    public bool Muted { get; set; }
    public bool Sharing { get; set; }

    public string RoomId => _roomId;
    public string PeerId => _peerId;
    public IPEndPoint? PublicEndpoint => _publicEp;

    /// <summary>Voz recebida de alguem (thread de rede — nao toque na UI daqui).</summary>
    public event Action<uint, byte[], int, int>? VoiceReceived;

    /// <summary>A lista de participantes mudou (entrou, saiu, mutou, conectou).</summary>
    public event Action? PeersChanged;

    /// <summary>Erro que o usuario precisa ver (ex.: rules do Firestore faltando).</summary>
    public event Action<string>? Failed;

    public RoomSession(Firestore fs, string roomId, string peerId, string nick)
    {
        _fs = fs;
        _roomId = roomId;
        _peerId = peerId;
        _nick = nick;
        _mySenderId = HashId(peerId);
    }

    public List<RemotePeer> Peers
    {
        get { lock (_peersLock) return _peers.Values.ToList(); }
    }

    /// <summary>
    /// Registra um par manualmente, pulando o Firestore. Existe pro harness de teste
    /// conseguir exercitar o protocolo (punch/ack/voz) em loopback — o caminho normal
    /// e o SyncPeersAsync descobrir os pares sozinho.
    /// </summary>
    public void AddPeerDirect(string peerId, string nick, IEnumerable<IPEndPoint> candidates)
    {
        uint sid = HashId(peerId);
        lock (_peersLock)
        {
            if (!_peers.TryGetValue(sid, out var p))
            {
                p = new RemotePeer { PeerId = peerId, SenderId = sid };
                _peers[sid] = p;
            }
            p.Nick = nick;
            p.Candidates.Clear();
            p.Candidates.AddRange(candidates);
        }
        PeersChanged?.Invoke();
    }

    /// <summary>Sobe so a rede (socket + STUN + punch), sem publicar no Firestore.</summary>
    public async Task StartNetworkOnlyAsync(bool useStun = true, CancellationToken ct = default)
    {
        _firestoreBacked = false;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AppEnv.DisableUdpConnReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        int localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        foreach (var ip in AppEnv.LocalIPv4()) _localEps.Add(new IPEndPoint(ip, localPort));

        if (useStun) _publicEp = await Stun.DiscoverAsync(_socket, ct: _cts.Token).ConfigureAwait(false);

        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "primicord-rx" };
        _rxThread.Start();
        _punchTimer = new System.Threading.Timer(_ => PunchTick(), null, 0, PunchIntervalMs);
    }

    /// <summary>Porta UDP local (o harness precisa pra montar o endpoint de loopback).</summary>
    public int LocalPort => _socket?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    // ─── CICLO DE VIDA ───────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AppEnv.DisableUdpConnReset(_socket);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));   // porta efemera: o STUN descobre qual
        int localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        foreach (var ip in AppEnv.LocalIPv4()) _localEps.Add(new IPEndPoint(ip, localPort));
        Log.Write($"sala {_roomId}: socket na porta {localPort}, locais=[{string.Join(",", _localEps)}]");

        // STUN ANTES do loop de recepcao (ele consome do mesmo socket).
        _publicEp = await Stun.DiscoverAsync(_socket, ct: _cts.Token).ConfigureAwait(false);
        if (_publicEp is null)
            Log.Write("sem endereco publico — so vai conectar com quem estiver na mesma rede");

        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "primicord-rx" };
        _rxThread.Start();

        _punchTimer = new System.Threading.Timer(_ => PunchTick(), null, 0, PunchIntervalMs);

        await PublishPresenceAsync(full: true).ConfigureAwait(false);
        _ = Task.Run(() => PresenceLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;

        try { SendToAll(TypeBye, Array.Empty<byte>(), 0, 0); } catch { }

        try { _punchTimer?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Close(); } catch { }   // destrava o ReceiveFrom bloqueante
        try { _rxThread?.Join(500); } catch { }
        try { _socket?.Dispose(); } catch { }

        // Melhor esforco: apaga minha presenca pra sala nao ficar com fantasma.
        if (_firestoreBacked)
            try { _fs.DeleteAsync($"pc_rooms/{_roomId}/peers/{_peerId}").Wait(1500); } catch { }

        lock (_peersLock) _peers.Clear();
        Log.Write($"sala {_roomId}: encerrada");
    }

    // ─── PRESENCA (Firestore) ────────────────────────────────────────────────

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                await PublishPresenceAsync(full: false).ConfigureAwait(false);
                await SyncPeersAsync(ct).ConfigureAwait(false);
            }
            catch (FirestoreException ex)
            {
                Log.Write("presenca falhou: " + ex.Message);
                if (ex.IsPermissionDenied)
                {
                    Failed?.Invoke("O Firestore recusou a escrita — falta publicar as rules "
                                 + "do pc_rooms no Firebase Console.");
                    return;
                }
            }
            catch (Exception ex) { Log.Write("presenca falhou: " + ex.Message); }

            try { await Task.Delay(PresenceSyncMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PublishPresenceAsync(bool full)
    {
        var fields = new Dictionary<string, object?>
        {
            ["lastSeen"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["muted"] = Muted,
            ["sharing"] = Sharing,
        };
        if (full)
        {
            fields["nick"] = _nick;
            fields["pubIp"] = _publicEp?.Address.ToString() ?? "";
            fields["pubPort"] = (long)(_publicEp?.Port ?? 0);
            fields["locEps"] = string.Join(",", _localEps.Select(e => e.ToString()));
        }
        await _fs.SetAsync($"pc_rooms/{_roomId}/peers/{_peerId}", fields, mergeFields: true)
                 .ConfigureAwait(false);
    }

    private async Task SyncPeersAsync(CancellationToken ct)
    {
        var docs = await _fs.ListAsync($"pc_rooms/{_roomId}/peers", ct: ct).ConfigureAwait(false);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bool changed = false;
        var alive = new HashSet<uint>();

        foreach (var (id, f) in docs)
        {
            if (id == _peerId) continue;
            long lastSeen = Firestore.Num(f, "lastSeen");
            if (now - lastSeen > PeerStaleMs) continue;   // fantasma de sessao morta

            uint sid = HashId(id);
            alive.Add(sid);

            lock (_peersLock)
            {
                if (!_peers.TryGetValue(sid, out var p))
                {
                    p = new RemotePeer { PeerId = id, SenderId = sid };
                    _peers[sid] = p;
                    changed = true;
                    Log.Write($"entrou: {Firestore.Str(f, "nick")} ({id})");
                }

                string nick = Firestore.Str(f, "nick", id);
                bool muted = Firestore.Flag(f, "muted");
                bool sharing = Firestore.Flag(f, "sharing");
                if (p.Nick != nick || p.Muted != muted || p.Sharing != sharing) changed = true;
                p.Nick = nick; p.Muted = muted; p.Sharing = sharing;
                p.LastSeenMs = lastSeen;

                RefreshCandidates(p, f);
            }
        }

        // Quem sumiu do Firestore saiu da sala.
        lock (_peersLock)
        {
            foreach (var sid in _peers.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                Log.Write($"saiu: {_peers[sid].Nick}");
                _peers.Remove(sid);
                changed = true;
            }
        }

        if (changed) PeersChanged?.Invoke();
    }

    /// <summary>Reconstroi a lista de enderecos candidatos publicada pelo peer.</summary>
    private static void RefreshCandidates(RemotePeer p, Dictionary<string, object?> f)
    {
        var fresh = new List<IPEndPoint>();

        string pubIp = Firestore.Str(f, "pubIp");
        int pubPort = (int)Firestore.Num(f, "pubPort");
        if (!string.IsNullOrEmpty(pubIp) && pubPort > 0 && IPAddress.TryParse(pubIp, out var pip))
            fresh.Add(new IPEndPoint(pip, pubPort));

        foreach (string s in Firestore.Str(f, "locEps").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (IPEndPoint.TryParse(s.Trim(), out var lep)) fresh.Add(lep);

        if (fresh.Count == 0) return;
        // So substitui se mudou de verdade (evita descartar o Locked a cada poll).
        if (p.Candidates.Count == fresh.Count && p.Candidates.All(fresh.Contains)) return;
        p.Candidates.Clear();
        p.Candidates.AddRange(fresh);
    }

    // ─── HOLE PUNCHING + KEEPALIVE ───────────────────────────────────────────

    private void PunchTick()
    {
        if (!_running) return;
        var sock = _socket;
        if (sock == null) return;

        long now = DateTime.UtcNow.Ticks;
        foreach (var p in Peers)
        {
            if (p.Locked != null)
            {
                // Conectado: um ping por segundo mantem o buraco do NAT aberto mesmo
                // se a pessoa ficar calada (sem trafego, o roteador fecha em ~30s).
                if ((now - Interlocked.Read(ref p.LastRecvTicks)) > TimeSpan.TicksPerSecond * 6)
                {
                    // Parou de responder — volta a furar por todos os candidatos.
                    Log.Write($"{p.Nick}: mudo ha 6s, refurando");
                    p.Locked = null;
                    PeersChanged?.Invoke();
                }
                else
                {
                    if (Environment.TickCount64 % KeepAliveMs < PunchIntervalMs)
                        SendTo(p.Locked, TypePunch, Array.Empty<byte>(), 0, 0);
                    continue;
                }
            }

            foreach (var ep in p.Candidates) SendTo(ep, TypePunch, Array.Empty<byte>(), 0, 0);
        }
    }

    // ─── ENVIO ───────────────────────────────────────────────────────────────

    /// <summary>Manda um frame de voz pra todo mundo que ja conectou.</summary>
    public void SendVoice(byte[] payload, int offset, int count)
    {
        if (Muted) return;
        SendToAll(TypeVoice, payload, offset, count, Interlocked.Increment(ref _voiceSeq));
    }

    private void SendToAll(byte type, byte[] payload, int offset, int count, uint seq = 0)
    {
        foreach (var p in Peers)
        {
            var ep = p.Locked;
            if (ep != null) SendTo(ep, type, payload, offset, count, seq);
        }
    }

    private void SendTo(IPEndPoint ep, byte type, byte[] payload, int offset, int count, uint seq = 0)
    {
        var sock = _socket;
        if (sock == null) return;
        try
        {
            byte[] packet = new byte[HeaderBytes + count];
            packet[0] = type;
            WriteUInt32(packet, 1, _mySenderId);
            WriteUInt32(packet, 5, seq);
            if (count > 0) Buffer.BlockCopy(payload, offset, packet, HeaderBytes, count);
            sock.SendTo(packet, ep);
        }
        catch (SocketException) { /* destino ainda fechado — normal durante o furo */ }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Write("envio falhou: " + ex.Message); }
    }

    // ─── RECEPCAO ────────────────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        var buf = new byte[4096];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            int len;
            try
            {
                var sock = _socket;
                if (sock == null) break;
                len = sock.ReceiveFrom(buf, ref from);
            }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; Thread.Sleep(2); continue; }
            catch (Exception ex) { Log.Write("recepcao falhou: " + ex.Message); continue; }

            if (len < HeaderBytes) continue;

            byte type = buf[0];
            uint senderId = ReadUInt32(buf, 1);
            if (senderId == _mySenderId) continue;   // eco de mim mesmo

            var src = (IPEndPoint)from;
            RemotePeer? peer;
            lock (_peersLock) _peers.TryGetValue(senderId, out peer);
            if (peer == null) continue;   // ainda nao apareceu no Firestore

            Interlocked.Exchange(ref peer.LastRecvTicks, DateTime.UtcNow.Ticks);

            // Primeiro pacote que chega DE VERDADE define o endereco bom. Se ele mudar
            // (NAT remapeou, trocou de wifi pra cabo), adota o novo sem derrubar a sala.
            if (peer.Locked == null || !peer.Locked.Equals(src))
            {
                bool first = peer.Locked == null;
                peer.Locked = src;
                Log.Write($"{peer.Nick}: {(first ? "conectado" : "endereco mudou")} via {src}");
                PeersChanged?.Invoke();
            }

            switch (type)
            {
                case TypeVoice:
                    VoiceReceived?.Invoke(senderId, buf, HeaderBytes, len - HeaderBytes);
                    break;

                case TypePunch:
                    // Responde imediatamente: e o ack que fecha o furo do outro lado.
                    SendTo(src, TypePunchAck, Array.Empty<byte>(), 0, 0);
                    break;

                case TypePunchAck:
                    break;   // o Locked acima ja e o efeito util

                case TypeBye:
                    lock (_peersLock) _peers.Remove(senderId);
                    Log.Write($"{peer.Nick}: saiu (bye)");
                    PeersChanged?.Invoke();
                    break;
            }
        }
        Log.Write("loop de recepcao encerrado");
    }

    // ─── UTIL ────────────────────────────────────────────────────────────────

    /// <summary>FNV-1a 32 bits — id curto e estavel pro cabecalho do pacote.</summary>
    public static uint HashId(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    private static void WriteUInt32(byte[] b, int off, uint v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
        b[off + 2] = (byte)(v >> 16); b[off + 3] = (byte)(v >> 24);
    }

    private static uint ReadUInt32(byte[] b, int off)
        => (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
}
