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
    public bool JamJoined;
    public string JamLink = "";
    public string Game = "";

    /// <summary>Enderecos onde ele PODE estar (publico + todos os locais).</summary>
    public readonly List<IPEndPoint> Candidates = new();

    /// <summary>O endereco que respondeu de verdade. null = ainda furando o NAT.</summary>
    public IPEndPoint? Locked;

    public long LastRecvTicks;
    public long LastSeenMs;

    public bool Connected => Locked != null &&
        (DateTime.UtcNow.Ticks - Interlocked.Read(ref LastRecvTicks)) < TimeSpan.TicksPerSecond * 6;

    /// <summary>
    /// true quando o caminho que fechou foi por endereco PRIVADO — ou seja, os dois
    /// estao na mesma rede local. Muda tudo: LAN e gigabit e nao tem teto de subida,
    /// entao da pra mandar tela em qualidade cheia com audio sem pesar em nada.
    /// </summary>
    public bool OnLan => Locked != null && AppEnv.IsPrivateAddress(Locked.Address);
    public bool OnTailnet => Locked != null && ConnectionPolicy.IsTailnetAddress(Locked.Address);
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
public sealed class RoomSession : IDisposable, IAsyncDisposable
{
    // ─── protocolo ───────────────────────────────────────────────────────────
    public const int HeaderBytes = 9;
    public const byte TypeVoice = 0;
    public const byte TypePunch = 1;   // "estou aqui, me responde"
    public const byte TypePunchAck = 2;   // "te ouvi"
    public const byte TypeBye = 3;   // saida limpa
    public const byte TypeScreen = 4;   // quadro de tela (fragmentado)
    public const byte TypeMusic = 5;   // áudio do sistema junto da transmissão
    public const byte TypeCinemaCtl = 6;   // controle do cinema (oferta, nack, play)
    public const byte TypeCinemaData = 7;   // pedaco do arquivo de video
    public const byte TypeScreenParity = 8; // paridade XOR de um quadro de tela

    // Um quadro de tela nao cabe num datagrama, entao vai picado. Sub-cabecalho de
    // 10 bytes depois do cabecalho comum: frameId, indice, total, largura, altura.
    private const int ScreenSubHeader = 10;
    // frameId, total, largura, altura e tamanho original do quadro. A paridade
    // permite reconstruir um unico datagrama perdido sem esperar um keyframe.
    private const int ScreenParitySubHeader = 12;
    private const int ChunkPayload = 1100;   // total ~1119 bytes, abaixo do MTU

    private const int PunchIntervalMs = 250;   // enquanto nao conectou
    private const int KeepAliveMs = 1000;  // depois de conectado (mantem o NAT aberto)
    private const int PresenceSyncMs = 2000;  // poll do Firestore

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
    private readonly SemaphoreSlim _presenceGate = new(1, 1);
    private Task? _presenceTask;
    private Task _cleanupTask = Task.CompletedTask;
    private int _disposed;
    private long _joinedAt;
    private long _lastPresenceSync;
    public bool PresenceReady { get; private set; }
    public bool PresenceHealthy => PresenceReady && Environment.TickCount64 - Interlocked.Read(ref _lastPresenceSync) < 12_000;
    public string? PresenceError { get; private set; }

    private readonly object _peersLock = new();
    private readonly Dictionary<uint, RemotePeer> _peers = new();

    private IPEndPoint? _publicEp;
    private readonly List<IPEndPoint> _localEps = new();
    private uint _mySenderId;
    private uint _voiceSeq;

    public bool Muted { get; set; }
    public bool Sharing { get; set; }
    public bool TailscaleOnly { get; set; }
    public bool JamJoined { get; private set; }
    public string JamLink { get; private set; } = "";
    public string Game { get; set; } = "";

    public void SetJam(bool joined, string link)
    {
        JamJoined = joined;
        JamLink = SpotifyJam.TryInvite(link, out var uri) ? uri!.AbsoluteUri : "";
        if (_running && _firestoreBacked) _ = PublishJamStateAsync();
    }

    public string RoomId => _roomId;
    public string PeerId => _peerId;
    public IPEndPoint? PublicEndpoint => _publicEp;

    /// <summary>Voz recebida de alguem (thread de rede — nao toque na UI daqui).</summary>
    public event Action<uint, byte[], int, int>? VoiceReceived;

    /// <summary>Audio do sistema associado à transmissão de tela.</summary>
    public event Action<uint, byte[], int, int>? MusicReceived;

    /// <summary>Quadro de tela COMPLETO ja remontado: (quem, jpeg, largura, altura).</summary>
    public event Action<uint, byte[], int, int>? ScreenFrameReceived;

    /// <summary>Mensagem de controle do cinema (JSON).</summary>
    public event Action<uint, string>? CinemaControl;

    /// <summary>Pedaco do arquivo de video: (quem, indice, buffer, offset, tamanho).</summary>
    public event Action<uint, int, byte[], int, int>? CinemaChunk;

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

    private async Task PublishJamStateAsync()
    {
        try { await PublishPresenceAsync(full: false).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Jam: publicar estado falhou: " + ex.Message); }
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
        // Buffers graudos: a tela vai em rajadas de dezenas de datagramas e o padrao
        // (~64KB) transborda, derrubando pedacos e perdendo o quadro inteiro.
        try { _socket.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        try { _socket.SendBufferSize = 2 * 1024 * 1024; } catch { }
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

    public async Task StartAsync(CancellationToken ct = default, bool useStun = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        var addresses = TailscaleOnly ? ConnectionPolicy.TailnetAddresses() : AppEnv.LocalIPv4();
        if (TailscaleOnly && addresses.Count == 0)
            throw new InvalidOperationException("Conecte o Tailscale à rede da tribo antes de entrar na sala.");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AppEnv.DisableUdpConnReset(_socket);
        // Buffers graudos: a tela vai em rajadas de dezenas de datagramas e o padrao
        // (~64KB) transborda, derrubando pedacos e perdendo o quadro inteiro.
        try { _socket.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        try { _socket.SendBufferSize = 2 * 1024 * 1024; } catch { }
        _socket.Bind(new IPEndPoint(TailscaleOnly ? addresses[0] : IPAddress.Any, 0));
        int localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        foreach (var ip in addresses) _localEps.Add(new IPEndPoint(ip, localPort));
        Log.Write($"sala {_roomId}: socket na porta {localPort}, locais=[{string.Join(",", _localEps)}]");

        // STUN ANTES do loop de recepcao (ele consome do mesmo socket).
        _publicEp = TailscaleOnly || !useStun ? null : await Stun.DiscoverAsync(_socket, ct: _cts.Token).ConfigureAwait(false);
        if (_publicEp is null && !TailscaleOnly)
            Log.Write("sem endereco publico — so vai conectar com quem estiver na mesma rede");

        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "primicord-rx" };
        _rxThread.Start();

        _punchTimer = new System.Threading.Timer(_ => PunchTick(), null, 0, PunchIntervalMs);

        var token = _cts.Token;
        // Use the server clock and verify the exact room before advertising a
        // successful join. The room name is display-only; all peers share its ID.
        var room = await _fs.GetAsync($"pc_rooms/{_roomId}", token).ConfigureAwait(false);
        if (room == null) throw new InvalidOperationException("Essa sala não existe mais. Atualize a lista e escolha uma sala.");
        token.ThrowIfCancellationRequested();
        _joinedAt = _fs.ServerNowMs;
        await PublishPresenceAsync(full: true).ConfigureAwait(false);
        await SyncPeersAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _presenceTask = PresenceLoopAsync(token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _running = false;

        try { SendToAll(TypeBye, Array.Empty<byte>(), 0, 0); } catch { }

        try { _punchTimer?.Dispose(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _socket?.Close(); } catch { }   // destrava o ReceiveFrom bloqueante
        try { _rxThread?.Join(500); } catch { }
        try { _socket?.Dispose(); } catch { }

        // Serialize cleanup after all writes. A late PATCH used to recreate a
        // deleted peer and leave a second copy of the same person in the room.
        if (_firestoreBacked) _cleanupTask = CleanupPresenceAsync();

        lock (_peersLock) _peers.Clear();
        Log.Write($"sala {_roomId}: encerrada");
    }

    public async ValueTask DisposeAsync() { Dispose(); await _cleanupTask.ConfigureAwait(false); }

    private async Task CleanupPresenceAsync()
    {
        try
        {
            if (_presenceTask != null) await _presenceTask.ConfigureAwait(false);
            await _presenceGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _fs.DeleteAsync($"pc_rooms/{_roomId}/peers/{_peerId}", timeout.Token).ConfigureAwait(false);
            }
            finally { _presenceGate.Release(); }
        }
        catch (Exception ex) { Log.Write("limpeza de presença: " + ex.Message); }
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (FirestoreException ex)
            {
                PresenceError = ex.Message;
                Log.Write("presenca falhou: " + ex.Message);
                if (ex.IsPermissionDenied)
                {
                    Failed?.Invoke("O Firestore recusou a escrita — falta publicar as rules "
                                 + "do pc_rooms no Firebase Console.");
                    return;
                }
            }
            catch (Exception ex) { PresenceError = ex.Message; Log.Write("presenca falhou: " + ex.Message); }

            try { await Task.Delay(PresenceSyncMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PublishPresenceAsync(bool full)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await _presenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        if (!_running) return;
        ct.ThrowIfCancellationRequested();
        var fields = new Dictionary<string, object?>
        {
            ["lastSeen"] = _fs.ServerNowMs,
            ["joinedAt"] = _joinedAt,
            ["accountId"] = RoomPresence.AccountKey(_nick),
            ["version"] = Application.ProductVersion,
            ["muted"] = Muted,
            ["sharing"] = Sharing,
            ["jamJoined"] = JamJoined,
            ["jamLink"] = JamLink,
            ["game"] = Game,
        };
        {
            fields["nick"] = _nick;
            fields["pubIp"] = _publicEp?.Address.ToString() ?? "";
            fields["pubPort"] = (long)(_publicEp?.Port ?? 0);
            fields["locEps"] = string.Join(",", _localEps.Select(e => e.ToString()));
        }
        await _fs.SetAsync($"pc_rooms/{_roomId}/peers/{_peerId}", fields, mergeFields: true, ct: ct)
                 .ConfigureAwait(false);
        }
        finally { _presenceGate.Release(); }
    }

    private async Task SyncPeersAsync(CancellationToken ct)
    {
        var docs = await _fs.ListAsync($"pc_rooms/{_roomId}/peers", ct: ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!_running) return;
        bool changed = false;
        var alive = new HashSet<uint>();

        foreach (var (id, f) in RoomPresence.Current(docs, _fs.ServerNowMs))
        {
            if (id == _peerId || RoomPresence.AccountKey(Firestore.Str(f, "accountId", Firestore.Str(f, "nick"))) == RoomPresence.AccountKey(_nick)) continue;
            long lastSeen = RoomPresence.SeenAt(f);

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
                bool jamJoined = Firestore.Flag(f, "jamJoined");
                string jamLink = Firestore.Str(f, "jamLink");
                string game = Firestore.Str(f, "game");
                if (p.JamJoined != jamJoined || p.JamLink != jamLink || p.Game != game) changed = true;
                p.JamJoined = jamJoined;
                p.JamLink = SpotifyJam.TryInvite(jamLink, out var invite) ? invite!.AbsoluteUri : "";
                p.Game = game;
                if (p.Nick != nick || p.Muted != muted || p.Sharing != sharing)
                    changed = true;
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

        bool firstSync = !PresenceReady || PresenceError != null;
        PresenceReady = true;
        PresenceError = null;
        Interlocked.Exchange(ref _lastPresenceSync, Environment.TickCount64);
        if (changed || firstSync) PeersChanged?.Invoke();
    }

    /// <summary>Reconstroi a lista de enderecos candidatos publicada pelo peer.</summary>
    private void RefreshCandidates(RemotePeer p, Dictionary<string, object?> f)
    {
        var fresh = new List<IPEndPoint>();

        string pubIp = Firestore.Str(f, "pubIp");
        int pubPort = (int)Firestore.Num(f, "pubPort");
        if (!string.IsNullOrEmpty(pubIp) && pubPort > 0 && IPAddress.TryParse(pubIp, out var pip))
            fresh.Add(new IPEndPoint(pip, pubPort));

        foreach (string s in Firestore.Str(f, "locEps").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (IPEndPoint.TryParse(s.Trim(), out var lep)) fresh.Add(lep);

        if (TailscaleOnly) fresh.RemoveAll(e => !ConnectionPolicy.IsTailnetAddress(e.Address));

        // So substitui se mudou de verdade (evita descartar o Locked a cada poll).
        if (p.Candidates.Count == fresh.Count && p.Candidates.All(fresh.Contains)) return;
        p.Candidates.Clear();
        p.Candidates.AddRange(fresh);
        if (p.Locked != null && !fresh.Contains(p.Locked))
        {
            p.Locked = null;
            Interlocked.Exchange(ref p.LastRecvTicks, 0);
        }
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

            IPEndPoint[] candidates;
            lock (_peersLock) candidates = p.Candidates.ToArray();
            foreach (var ep in candidates) SendTo(ep, TypePunch, Array.Empty<byte>(), 0, 0);
        }
    }

    // ─── ENVIO ───────────────────────────────────────────────────────────────

    /// <summary>Manda um frame de voz pra todo mundo que ja conectou.</summary>
    public void SendVoice(byte[] payload, int offset, int count)
    {
        if (Muted) return;
        SendToAll(TypeVoice, payload, offset, count, Interlocked.Increment(ref _voiceSeq));
    }

    /// <summary>Manda um frame do áudio do sistema junto da transmissão.</summary>
    public void SendMusic(byte[] payload, int offset, int count)
        => SendToAll(TypeMusic, payload, offset, count, Interlocked.Increment(ref _musicSeq));

    /// <summary>Mensagem de controle do cinema (cabe num datagrama).</summary>
    public void SendCinemaControl(string json)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (bytes.Length > 1200) { Log.Write("cinema: controle grande demais, ignorado"); return; }
        SendToAll(TypeCinemaCtl, bytes, 0, bytes.Length);
    }

    /// <summary>Um pedaco do arquivo. O indice vai no campo de sequencia.</summary>
    public void SendCinemaChunk(int index, byte[] data, int offset, int count)
        => SendToAll(TypeCinemaData, data, offset, count, (uint)index);

    private uint _musicSeq;
    private ushort _screenFrameId;

    /// <summary>
    /// Pica um quadro JPEG em datagramas e manda pra todo mundo.
    /// </summary>
    /// <remarks>
    /// Sem retransmissao de proposito: se um pedaco se perde, o quadro inteiro e
    /// descartado e o proximo (100ms depois) assume. Pra tela ao vivo, chegar
    /// atrasado e pior que faltar um quadro.
    /// </remarks>
    public void SendScreenFrame(byte[] jpeg, int length, int width, int height)
    {
        var sock = _socket;
        if (sock == null) return;

        ushort frameId = _screenFrameId++;
        int chunks = (length + ChunkPayload - 1) / ChunkPayload;
        if (chunks == 0 || chunks > ushort.MaxValue || length > int.MaxValue) return;

        var targets = Peers.Where(p => p.Locked != null).Select(p => p.Locked!).ToList();
        if (targets.Count == 0) return;

        var parity = new byte[ChunkPayload];
        for (int i = 0; i < chunks; i++)
        {
            int off = i * ChunkPayload;
            int len = Math.Min(ChunkPayload, length - off);
            byte[] packet = new byte[HeaderBytes + ScreenSubHeader + len];

            packet[0] = TypeScreen;
            WriteUInt32(packet, 1, _mySenderId);
            WriteUInt32(packet, 5, frameId);
            WriteUInt16(packet, 9, frameId);
            WriteUInt16(packet, 11, (ushort)i);
            WriteUInt16(packet, 13, (ushort)chunks);
            WriteUInt16(packet, 15, (ushort)width);
            WriteUInt16(packet, 17, (ushort)height);
            Buffer.BlockCopy(jpeg, off, packet, HeaderBytes + ScreenSubHeader, len);

            // XOR por posição: todos os pedaços, inclusive o ultimo, ocupam a
            // mesma janela para a paridade. O tamanho real continua no pacote
            // de paridade, portanto nao existe lixo no quadro reconstruido.
            for (int p = 0; p < len; p++) parity[p] ^= jpeg[off + p];

            foreach (var ep in targets)
            {
                try { sock.SendTo(packet, ep); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        // Um datagrama extra recupera exatamente uma perda por quadro. Em redes
        // normais isso elimina o “quadrado velho” que antes só sumia no refresh.
        byte[] parityPacket = new byte[HeaderBytes + ScreenParitySubHeader + ChunkPayload];
        parityPacket[0] = TypeScreenParity;
        WriteUInt32(parityPacket, 1, _mySenderId);
        WriteUInt32(parityPacket, 5, frameId);
        WriteUInt16(parityPacket, 9, frameId);
        WriteUInt16(parityPacket, 11, (ushort)chunks);
        WriteUInt16(parityPacket, 13, (ushort)width);
        WriteUInt16(parityPacket, 15, (ushort)height);
        WriteUInt32(parityPacket, 17, (uint)length);
        Buffer.BlockCopy(parity, 0, parityPacket, HeaderBytes + ScreenParitySubHeader, ChunkPayload);
        foreach (var ep in targets)
        {
            try { sock.SendTo(parityPacket, ep); }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
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
            if (TailscaleOnly && !ConnectionPolicy.IsTailnetAddress(src.Address)) continue;
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

                case TypeMusic:
                    MusicReceived?.Invoke(senderId, buf, HeaderBytes, len - HeaderBytes);
                    break;

                case TypeScreen:
                    HandleScreenChunk(peer, buf, len);
                    break;

                case TypeScreenParity:
                    HandleScreenParity(peer, buf, len);
                    break;

                case TypeCinemaCtl:
                    try
                    {
                        string json = System.Text.Encoding.UTF8.GetString(buf, HeaderBytes, len - HeaderBytes);
                        CinemaControl?.Invoke(senderId, json);
                    }
                    catch (Exception ex) { Log.Write("cinema: controle ilegivel: " + ex.Message); }
                    break;

                case TypeCinemaData:
                    // O indice do pedaco viaja no campo de sequencia do cabecalho.
                    CinemaChunk?.Invoke(senderId, (int)ReadUInt32(buf, 5),
                                        buf, HeaderBytes, len - HeaderBytes);
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

    // ─── REMONTAGEM DE QUADRO DE TELA ────────────────────────────────────────

    private sealed class FrameAssembly
    {
        public ushort FrameId;
        public byte[]?[] Chunks = Array.Empty<byte[]?>();
        public byte[]? Parity;
        public int Have;
        public int Total;
        public int DataLength;
        public int Width, Height;
        public long StartedTicks;
    }

    private readonly Dictionary<uint, FrameAssembly> _assembling = new();

    private void HandleScreenChunk(RemotePeer peer, byte[] buf, int len)
    {
        if (len < HeaderBytes + ScreenSubHeader) return;

        ushort frameId = ReadUInt16(buf, 9);
        ushort idx = ReadUInt16(buf, 11);
        ushort total = ReadUInt16(buf, 13);
        ushort w = ReadUInt16(buf, 15);
        ushort h = ReadUInt16(buf, 17);
        if (total == 0 || idx >= total) return;

        int dataLen = len - HeaderBytes - ScreenSubHeader;
        if (dataLen <= 0) return;

        byte[]?[] ready;
        int width, height;
        FrameAssembly asm;
        lock (_assembling)
        {
            if (!_assembling.TryGetValue(peer.SenderId, out asm!))
            {
                asm = new FrameAssembly();
                _assembling[peer.SenderId] = asm;
            }

            // Quadro novo: joga fora o anterior incompleto. Pedaço atrasado de um
            // quadro velho e ignorado (ushort da a volta, dai a comparacao circular).
            if (asm.FrameId != frameId || asm.Total != total)
            {
                bool newer = asm.Chunks.Length == 0 || (ushort)(frameId - asm.FrameId) < 32768;
                if (!newer) return;
                asm.FrameId = frameId;
                asm.Total = total;
                asm.Chunks = new byte[total][];
                asm.Parity = null;
                asm.Have = 0;
                asm.DataLength = 0;
                asm.Width = w;
                asm.Height = h;
                asm.StartedTicks = DateTime.UtcNow.Ticks;
            }

            if (asm.Chunks[idx] != null) return;   // duplicado
            var slice = new byte[dataLen];
            Buffer.BlockCopy(buf, HeaderBytes + ScreenSubHeader, slice, 0, dataLen);
            asm.Chunks[idx] = slice;
            asm.Have++;

            if (!TryCompleteWithParity(asm)) return;
            ready = asm.Chunks;
            asm.Chunks = Array.Empty<byte[]?>();
            asm.Parity = null;
            asm.Have = 0;
            asm.Total = 0;
            width = asm.Width;
            height = asm.Height;
        }

        int size = 0;
        foreach (var c in ready) size += c!.Length;
        var jpeg = new byte[size];
        int pos = 0;
        foreach (var c in ready)
        {
            Buffer.BlockCopy(c!, 0, jpeg, pos, c!.Length);
            pos += c.Length;
        }
        ScreenFrameReceived?.Invoke(peer.SenderId, jpeg, width, height);
    }

    private void HandleScreenParity(RemotePeer peer, byte[] buf, int len)
    {
        if (len < HeaderBytes + ScreenParitySubHeader) return;

        ushort frameId = ReadUInt16(buf, 9);
        ushort total = ReadUInt16(buf, 11);
        ushort w = ReadUInt16(buf, 13);
        ushort h = ReadUInt16(buf, 15);
        uint dataLength = ReadUInt32(buf, 17);
        int parityLen = len - HeaderBytes - ScreenParitySubHeader;
        if (total == 0 || parityLen <= 0 || parityLen > ChunkPayload ||
            dataLength == 0 || dataLength > (uint)total * ChunkPayload) return;

        byte[]?[] ready;
        int width, height;
        lock (_assembling)
        {
            FrameAssembly asm;
            if (!_assembling.TryGetValue(peer.SenderId, out asm!))
            {
                asm = new FrameAssembly();
                _assembling[peer.SenderId] = asm;
            }

            if (asm.FrameId != frameId || asm.Total != total)
            {
                bool newer = asm.Chunks.Length == 0 || (ushort)(frameId - asm.FrameId) < 32768;
                if (!newer) return;
                asm.FrameId = frameId;
                asm.Total = total;
                asm.Chunks = new byte[total][];
                asm.Have = 0;
                asm.DataLength = 0;
                asm.Width = w;
                asm.Height = h;
                asm.StartedTicks = DateTime.UtcNow.Ticks;
            }

            var parity = new byte[parityLen];
            Buffer.BlockCopy(buf, HeaderBytes + ScreenParitySubHeader, parity, 0, parityLen);
            asm.Parity = parity;
            asm.DataLength = (int)dataLength;
            if (!TryCompleteWithParity(asm)) return;

            ready = asm.Chunks;
            asm.Chunks = Array.Empty<byte[]?>();
            asm.Parity = null;
            asm.Have = 0;
            asm.Total = 0;
            width = asm.Width;
            height = asm.Height;
        }

        int size = 0;
        foreach (var c in ready) size += c!.Length;
        var frame = new byte[size];
        int pos = 0;
        foreach (var c in ready)
        {
            Buffer.BlockCopy(c!, 0, frame, pos, c!.Length);
            pos += c.Length;
        }
        ScreenFrameReceived?.Invoke(peer.SenderId, frame, width, height);
    }

    /// <summary>Completa o quadro inteiro ou reconstroi uma unica perda via XOR.</summary>
    private static bool TryCompleteWithParity(FrameAssembly asm)
    {
        if (asm.Have == asm.Total) return true;
        if (asm.Parity == null || asm.Have != asm.Total - 1 || asm.DataLength <= 0) return false;

        int missing = -1;
        for (int i = 0; i < asm.Total; i++)
            if (asm.Chunks[i] == null) { missing = i; break; }
        if (missing < 0) return true;

        int missingLength = missing == asm.Total - 1
            ? asm.DataLength - (asm.Total - 1) * ChunkPayload
            : ChunkPayload;
        if (missingLength <= 0 || missingLength > ChunkPayload || asm.Parity.Length < missingLength)
            return false;

        var recovered = new byte[missingLength];
        Buffer.BlockCopy(asm.Parity, 0, recovered, 0, missingLength);
        for (int i = 0; i < asm.Total; i++)
        {
            var chunk = asm.Chunks[i];
            if (chunk == null) continue;
            int count = Math.Min(missingLength, chunk.Length);
            for (int p = 0; p < count; p++) recovered[p] ^= chunk[p];
        }
        asm.Chunks[missing] = recovered;
        asm.Have++;
        return true;
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

    private static void WriteUInt16(byte[] b, int off, ushort v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8);
    }

    private static ushort ReadUInt16(byte[] b, int off)
        => (ushort)(b[off] | (b[off + 1] << 8));
}
