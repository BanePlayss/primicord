using System.Net;
using System.Net.Sockets;
using System.Buffers.Binary;

namespace Primicord;

/// <summary>Um primitivo do outro lado da linha.</summary>
public sealed class RemotePeer
{
    public string PeerId = "";
    public uint SenderId;
    public string Nick = "";
    public bool Muted;
    public bool Sharing;
    private readonly object _socialLock = new();
    private SocialPosition _social;
    public SocialPosition Social
    {
        get { lock (_socialLock) return _social; }
        set { lock (_socialLock) _social = value; }
    }

    /// <summary>Enderecos onde ele PODE estar (publico + todos os locais).</summary>
    public readonly List<IPEndPoint> Candidates = new();

    /// <summary>O endereco que respondeu de verdade. null = ainda furando o NAT.</summary>
    public IPEndPoint? Locked;

    public long LastRecvTicks;
    public long LastSeenMs;

    /// <summary>Quando este par apareceu na sala.</summary>
    public readonly long JoinedTicks = DateTime.UtcNow.Ticks;

    /// <summary>
    /// Ha quantos segundos tentamos furar sem UM pacote sequer ter chegado. Zero
    /// quando ja conectou. O furo normal fecha em menos de 5s; passar muito disso
    /// quer dizer que nao vai fechar — NAT simetrico dos dois lados, ou firewall.
    /// </summary>
    public int SilentSeconds => Locked != null ? 0
        : (int)((DateTime.UtcNow.Ticks - JoinedTicks) / TimeSpan.TicksPerSecond);

    /// <summary>Pra o aviso de "desisti" sair uma vez, e nao a cada 250ms.</summary>
    public bool GaveUpLogged;

    public bool Connected => Locked != null &&
        (DateTime.UtcNow.Ticks - Interlocked.Read(ref LastRecvTicks)) < TimeSpan.TicksPerSecond * 6;

    /// <summary>
    /// true quando o caminho que fechou foi por endereco PRIVADO — ou seja, os dois
    /// estao na mesma rede local. Muda tudo: LAN e gigabit e nao tem teto de subida,
    /// entao da pra mandar tela em qualidade cheia com audio sem pesar em nada.
    /// </summary>
    public bool OnLan => Locked != null && AppEnv.IsPrivateAddress(Locked.Address);
}

/// <summary>
/// A sala: malha UDP ponto-a-ponto entre todos os participantes.
/// </summary>
/// <remarks>
/// Cada um manda a propria voz DIRETO pra cada outro (malha). Sem servidor no meio:
/// o coordenador so serve pra trocar enderecos; depois disso o audio nao passa por
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
public sealed class RoomSession : IVoiceTransport, IDisposable
{
    // ─── protocolo ───────────────────────────────────────────────────────────
    public const int HeaderBytes = 9;
    public const byte TypeVoice = 0;
    public const byte TypePunch = 1;   // "estou aqui, me responde"
    public const byte TypePunchAck = 2;   // "te ouvi"
    public const byte TypeBye = 3;   // saida limpa
    public const byte TypeScreen = 4;   // quadro de tela (fragmentado)
    public const byte TypeMusic = 5;   // audio do sistema do DJ
    public const byte TypeCinemaCtl = 6;   // controle do cinema (oferta, nack, play)
    public const byte TypeCinemaData = 7;   // pedaco do arquivo de video
    public const byte TypeWebcam = 8;   // quadro da camera (fragmentado, igual a tela)
    public const byte TypeSocial = 9;   // x/y/tamanho da bolinha (3 floats, 12 bytes)

    // Um quadro de tela nao cabe num datagrama, entao vai picado. Sub-cabecalho de
    // 10 bytes depois do cabecalho comum: frameId, indice, total, largura, altura.
    private const int ScreenSubHeader = 10;
    private const int ChunkPayload = 1100;   // total ~1119 bytes, abaixo do MTU

    private const int PunchIntervalMs = 250;   // enquanto nao conectou
    private const int KeepAliveMs = 1000;  // depois de conectado (mantem o NAT aberto)
    private const int PresenceSyncMs = 10_000;  // descoberta de pares; midia segue P2P
    private const int PeerStaleMs = 45_000; // tolera tres ciclos perdidos sem expulsar

    private readonly IDocumentStore _fs;
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
    private readonly object _socialLock = new();
    private SocialPosition _social;
    private long _lastSocialSendMs;

    public bool Muted { get; set; }
    public bool Sharing { get; set; }

    public SocialPosition Social
    {
        get { lock (_socialLock) return _social; }
    }

    public string RoomId => _roomId;
    public string PeerId => _peerId;
    public IPEndPoint? PublicEndpoint => _publicEp;

    /// <summary>Voz recebida de alguem (thread de rede — nao toque na UI daqui).</summary>
    public event Action<uint, byte[], int, int>? VoiceReceived;

    /// <summary>Audio do sistema do DJ (modo musica).</summary>
    public event Action<uint, byte[], int, int>? MusicReceived;

    /// <summary>Quadro de tela COMPLETO ja remontado: (quem, jpeg, largura, altura).</summary>
    public event Action<uint, byte[], int, int>? ScreenFrameReceived;

    /// <summary>Quadro de camera COMPLETO ja remontado: (quem, jpeg, largura, altura).</summary>
    public event Action<uint, byte[], int, int>? WebcamFrameReceived;

    /// <summary>Mensagem de controle do cinema (JSON).</summary>
    public event Action<uint, string>? CinemaControl;

    /// <summary>Pedaco do arquivo de video: (quem, indice, buffer, offset, tamanho).</summary>
    public event Action<uint, int, byte[], int, int>? CinemaChunk;

    /// <summary>Movimento social recebido pela malha (thread de rede).</summary>
    public event Action<uint, SocialPosition>? SocialMoved;

    /// <summary>A lista de participantes mudou (entrou, saiu, mutou, conectou).</summary>
    public event Action? PeersChanged;

    /// <summary>Erro que o usuario precisa ver (ex.: rules do Firestore faltando).</summary>
    public event Action<string>? Failed;

    public RoomSession(IDocumentStore fs, string roomId, string peerId, string nick)
    {
        _fs = fs;
        _roomId = roomId;
        _peerId = peerId;
        _nick = nick;
        _mySenderId = HashId(peerId);
        _social = SocialPosition.DefaultFor(_mySenderId);
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
                p = new RemotePeer
                {
                    PeerId = peerId, SenderId = sid,
                    Social = SocialPosition.DefaultFor(sid),
                };
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
        foreach (var ip in OrderedLocalAddresses()) _localEps.Add(new IPEndPoint(ip, localPort));

        if (useStun) _publicEp = await Stun.DiscoverAsync(_socket, ct: _cts.Token).ConfigureAwait(false);

        _rxThread = new Thread(ReceiveLoop) { IsBackground = true, Name = "primicord-rx" };
        _rxThread.Start();
        _punchTimer = new System.Threading.Timer(_ => PunchTick(), null, 0, PunchIntervalMs);
    }

    /// <summary>Porta UDP local (o harness precisa pra montar o endpoint de loopback).</summary>
    public int LocalPort => _socket?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    /// <summary>
    /// Atualiza a bolinha local e envia no maximo 20 vezes/s. final ignora o limite,
    /// garantindo que todos recebam exatamente o ponto em que o jogador soltou.
    /// </summary>
    public void UpdateSocial(SocialPosition position, bool final = false)
    {
        position = position.Normalized();
        lock (_socialLock) _social = position;

        long now = Environment.TickCount64;
        long previous = Interlocked.Read(ref _lastSocialSendMs);
        if (!final && now - previous < 50) return;
        Interlocked.Exchange(ref _lastSocialSendMs, now);

        var payload = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0, 4), (float)position.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(4, 4), (float)position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8, 4), position.Scale);
        SendToAll(TypeSocial, payload, 0, payload.Length);
    }

    // ─── CICLO DE VIDA ───────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = true;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        AppEnv.DisableUdpConnReset(_socket);
        // Buffers graudos: a tela vai em rajadas de dezenas de datagramas e o padrao
        // (~64KB) transborda, derrubando pedacos e perdendo o quadro inteiro.
        try { _socket.ReceiveBufferSize = 4 * 1024 * 1024; } catch { }
        try { _socket.SendBufferSize = 2 * 1024 * 1024; } catch { }
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));   // porta efemera: o STUN descobre qual
        int localPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

        foreach (var ip in OrderedLocalAddresses()) _localEps.Add(new IPEndPoint(ip, localPort));
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

    // ─── PRESENCA (mini servidor; Firestore durante a migracao) ─────────────

    private async Task PresenceLoopAsync(CancellationToken ct)
    {
        int delayMs = PresenceSyncMs;
        while (_running && !ct.IsCancellationRequested)
        {
            try
            {
                await PublishPresenceAsync(full: false).ConfigureAwait(false);
                await SyncPeersAsync(ct).ConfigureAwait(false);
                delayMs = PresenceSyncMs;
            }
            catch (DocumentStoreException ex)
            {
                Log.Write("presenca falhou: " + ex.Message);
                if (ex.IsPermissionDenied)
                {
                    Failed?.Invoke("O servidor de coordenacao recusou a presenca desta sala.");
                    return;
                }
                delayMs = ex.IsQuotaExceeded
                    ? Math.Min(15 * 60_000, Math.Max(60_000, delayMs * 2))
                    : Math.Min(5 * 60_000, Math.Max(30_000, delayMs * 2));
            }
            catch (Exception ex)
            {
                Log.Write("presenca falhou: " + ex.Message);
                delayMs = Math.Min(5 * 60_000, Math.Max(30_000, delayMs * 2));
            }

            try { await Task.Delay(delayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PublishPresenceAsync(bool full)
    {
        SocialPosition social = Social;
        var fields = new Dictionary<string, object?>
        {
            ["lastSeen"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["muted"] = Muted,
            ["sharing"] = Sharing,
            ["socialX"] = social.X,
            ["socialY"] = social.Y,
            ["socialScale"] = social.Scale,
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
                    p = new RemotePeer
                    {
                        PeerId = id, SenderId = sid,
                        Social = SocialPosition.DefaultFor(sid),
                    };
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

                var social = new SocialPosition(
                    Firestore.Real(f, "socialX", p.Social.X),
                    Firestore.Real(f, "socialY", p.Social.Y),
                    (int)Firestore.Num(f, "socialScale", p.Social.Scale)).Normalized();
                if (social != p.Social)
                {
                    p.Social = social;
                    SocialMoved?.Invoke(sid, social);
                }

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

    /// <summary>
    /// O candidato 100.64/10 vai primeiro: o Tailscale escolhe rota direta ou DERP
    /// sozinho. LAN e STUN continuam publicados como plano B da mesma sessao.
    /// </summary>
    private static IEnumerable<IPAddress> OrderedLocalAddresses()
        => AppEnv.LocalIPv4().Distinct().OrderByDescending(TailscaleIntegration.IsTailnetAddress);

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

            // Passou muito do normal (o furo fecha em menos de 5s quando fecha):
            // deixa registrado o que foi tentado, senao o diagnostico depois vira
            // adivinhacao. Continua tentando — so o silencio e que fica explicado.
            if (!p.GaveUpLogged && p.SilentSeconds >= 25)
            {
                p.GaveUpLogged = true;
                Log.Write($"{p.Nick}: sem rota apos {p.SilentSeconds}s. Nenhum pacote chegou. "
                        + $"Candidatos tentados: [{string.Join(", ", p.Candidates)}]. "
                        + $"Meu publico: {_publicEp?.ToString() ?? "NENHUM (STUN falhou)"}. "
                        + "Causas tipicas: NAT simetrico dos dois lados (4G/CGNAT) ou firewall.");
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

    /// <summary>Manda um frame do audio do sistema (modo DJ).</summary>
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
    private ushort _webcamFrameId;

    /// <summary>
    /// Pica um quadro JPEG em datagramas e manda pra todo mundo.
    /// </summary>
    /// <remarks>
    /// Sem retransmissao de proposito: se um pedaco se perde, o quadro inteiro e
    /// descartado e o proximo (100ms depois) assume. Pra tela ao vivo, chegar
    /// atrasado e pior que faltar um quadro.
    /// </remarks>
    public void SendScreenFrame(byte[] jpeg, int length, int width, int height)
        => SendFragmented(TypeScreen, ref _screenFrameId, jpeg, length, width, height);

    /// <summary>
    /// Um quadro da camera. Mesmo empacotamento da tela — e o mesmo problema:
    /// nao cabe num datagrama, e chegar atrasado e pior que faltar.
    /// </summary>
    public void SendWebcamFrame(byte[] jpeg, int length, int width, int height)
        => SendFragmented(TypeWebcam, ref _webcamFrameId, jpeg, length, width, height);

    private void SendFragmented(byte type, ref ushort frameCounter,
                                byte[] jpeg, int length, int width, int height)
    {
        var sock = _socket;
        if (sock == null) return;

        ushort frameId = frameCounter++;
        int chunks = (length + ChunkPayload - 1) / ChunkPayload;
        if (chunks == 0 || chunks > ushort.MaxValue) return;

        var targets = Peers.Where(p => p.Locked != null).Select(p => p.Locked!).ToList();
        if (targets.Count == 0) return;

        for (int i = 0; i < chunks; i++)
        {
            int off = i * ChunkPayload;
            int len = Math.Min(ChunkPayload, length - off);
            byte[] packet = new byte[HeaderBytes + ScreenSubHeader + len];

            packet[0] = type;
            WriteUInt32(packet, 1, _mySenderId);
            WriteUInt32(packet, 5, frameId);
            WriteUInt16(packet, 9, frameId);
            WriteUInt16(packet, 11, (ushort)i);
            WriteUInt16(packet, 13, (ushort)chunks);
            WriteUInt16(packet, 15, (ushort)width);
            WriteUInt16(packet, 17, (ushort)height);
            Buffer.BlockCopy(jpeg, off, packet, HeaderBytes + ScreenSubHeader, len);

            foreach (var ep in targets)
            {
                try { sock.SendTo(packet, ep); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
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
                case TypeWebcam:
                    HandleFragmentedChunk(peer, type, buf, len);
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

                case TypeSocial:
                    if (len != HeaderBytes + 12) break;
                    var social = new SocialPosition(
                        BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(HeaderBytes, 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(HeaderBytes + 4, 4)),
                        (int)Math.Round(BinaryPrimitives.ReadSingleLittleEndian(
                            buf.AsSpan(HeaderBytes + 8, 4)))).Normalized();
                    peer.Social = social;
                    SocialMoved?.Invoke(senderId, social);
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
        public int Have;
        public int Total;
        public int Width, Height;
        public long StartedTicks;
    }

    /// <summary>
    /// Remontagem em curso, por (quem, que tipo de video).
    /// </summary>
    /// <remarks>
    /// O TIPO faz parte da chave de proposito. Indexando so por remetente, a mesma
    /// pessoa compartilhando tela E camera ao mesmo tempo teria os dois fluxos
    /// disputando o mesmo estado: cada quadro de um jogaria fora o quadro pela
    /// metade do outro, e nenhum dos dois fecharia nunca.
    /// </remarks>
    private readonly Dictionary<(uint Sender, byte Type), FrameAssembly> _assembling = new();

    private void HandleFragmentedChunk(RemotePeer peer, byte type, byte[] buf, int len)
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
            var key = (peer.SenderId, type);
            if (!_assembling.TryGetValue(key, out asm!))
            {
                asm = new FrameAssembly();
                _assembling[key] = asm;
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
                asm.Have = 0;
                asm.Width = w;
                asm.Height = h;
                asm.StartedTicks = DateTime.UtcNow.Ticks;
            }

            if (asm.Chunks[idx] != null) return;   // duplicado
            var slice = new byte[dataLen];
            Buffer.BlockCopy(buf, HeaderBytes + ScreenSubHeader, slice, 0, dataLen);
            asm.Chunks[idx] = slice;
            asm.Have++;

            if (asm.Have != asm.Total) return;

            // Completo: TIRA o array daqui de dentro e ja deixa um vazio no lugar.
            // Montar fora do lock lendo asm.Chunks seria corrida — o proximo quadro
            // pode trocar o array no meio da copia.
            ready = asm.Chunks;
            asm.Chunks = new byte[asm.Total][];
            asm.Have = 0;
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
        if (type == TypeWebcam) WebcamFrameReceived?.Invoke(peer.SenderId, jpeg, width, height);
        else ScreenFrameReceived?.Invoke(peer.SenderId, jpeg, width, height);
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
