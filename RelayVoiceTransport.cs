using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Concentus;
using Concentus.Enums;

namespace Primicord;

/// <summary>
/// Voz Opus por WebSocket dentro da tailnet. Diferente da malha UDP, cada PC so
/// abre uma conexao de SAIDA para uma replica Primicord; nenhum participante
/// precisa receber uma porta aleatoria pelo Firewall do Windows.
/// </summary>
public sealed class RelayVoiceTransport : IVoiceTransport, IDisposable
{
    private const byte AudioPacket = 1;
    private const int HeaderBytes = 7; // tipo + senderId + sequencia
    private const int SamplesPerFrame = 960; // 20ms @ 48kHz
    private const int PcmBytesPerFrame = SamplesPerFrame * 2;
    private const int MaxOpusPacket = 1275;

    private readonly IClusterEndpointProvider _endpoints;
    private readonly string _roomId;
    private readonly string _peerId;
    private readonly string _nick;
    private readonly uint _senderId;
    private readonly FrameAccumulator _mic = new();
    private readonly byte[] _pcm = new byte[PcmBytesPerFrame];
    private readonly short[] _samples = new short[SamplesPerFrame];
    private readonly byte[] _opus = new byte[MaxOpusPacket];
    private readonly object _encodeLock = new();
    private readonly IOpusEncoder _encoder;
    private readonly Channel<byte[]> _outgoing = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(12)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private readonly object _peerLock = new();
    private readonly HashSet<uint> _relayPeers = new();
    private readonly Dictionary<uint, DecoderState> _decoders = new();
    private CancellationTokenSource? _cts;
    private bool _running;
    private volatile bool _connected;
    private ushort _sequence;
    private int _reconnectFailures;

    public RelayVoiceTransport(IClusterEndpointProvider endpoints, string roomId,
                               string peerId, string nick)
    {
        _endpoints = endpoints;
        _roomId = roomId;
        _peerId = peerId;
        _nick = nick;
        _senderId = RoomSession.HashId(peerId);
        _encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 32_000;
        _encoder.UseVBR = true;
        _encoder.UseDTX = true;
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 3;
        _encoder.Complexity = 7;
    }

    public bool Muted { get; set; }
    public bool Connected => _connected;
    public int ConnectedPeerCount { get { lock (_peerLock) return _relayPeers.Count; } }
    public event Action<uint, byte[], int, int>? VoiceReceived;
    public event Action? StateChanged;

    public bool HasPeer(uint senderId)
    {
        lock (_peerLock) return _connected && _relayPeers.Contains(senderId);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunAsync(_cts.Token));
    }

    public void SendVoice(byte[] payload, int offset, int count)
    {
        if (!_running || !_connected || Muted || count <= 0) return;
        _mic.Append(payload, offset, count);
        while (_mic.TryDequeueFrame(_pcm, 0, PcmBytesPerFrame))
        {
            byte[] packet;
            lock (_encodeLock)
            {
                for (int i = 0; i < SamplesPerFrame; i++)
                    _samples[i] = (short)(_pcm[i * 2] | (_pcm[i * 2 + 1] << 8));
                int encoded;
                try { encoded = _encoder.Encode(_samples, SamplesPerFrame, _opus, _opus.Length); }
                catch (Exception ex) { Log.Write("relay voz: encode: " + ex.Message); return; }
                if (encoded <= 2) continue;

                packet = new byte[HeaderBytes + encoded];
                packet[0] = AudioPacket;
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1, 4), _senderId);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(5, 2), ++_sequence);
                Buffer.BlockCopy(_opus, 0, packet, HeaderBytes, encoded);
            }
            _outgoing.Writer.TryWrite(packet);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                IReadOnlyList<ClusterEndpoint> endpoints =
                    await _endpoints.GetEndpointsAsync(ct).ConfigureAwait(false);
                foreach (ClusterEndpoint endpoint in endpoints)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        await RunConnectionAsync(endpoint.Url, ct).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex)
                    {
                        Log.Write($"relay voz indisponivel em {endpoint.Url}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Write("relay voz: descoberta: " + ex.Message); }

            SetConnected(false, null);
            // Mesmo se um servidor aceitar e fechar imediatamente, nao cria um
            // loop de centenas de conexoes por minuto. Conexao estavel zera o
            // recuo dentro de RunConnectionAsync.
            int failures = Interlocked.Increment(ref _reconnectFailures);
            int delay = Math.Min(10_000, 750 * (1 << Math.Min(failures - 1, 4)));
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RunConnectionAsync(string baseUrl, CancellationToken outerCt)
    {
        Uri uri = RelayUri(baseUrl);
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        connectCts.CancelAfter(TimeSpan.FromSeconds(4));
        await socket.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);

        Log.Write("relay voz conectado: " + uri.GetLeftPart(UriPartial.Authority));
        SetConnected(true, Array.Empty<uint>());
        DateTimeOffset connectedAt = DateTimeOffset.UtcNow;
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        Task send = SendLoopAsync(socket, connectionCts.Token);
        Task receive = ReceiveLoopAsync(socket, connectionCts.Token);
        await Task.WhenAny(send, receive).ConfigureAwait(false);
        // Depois de alguns segundos o servidor provou que a conexao e estavel;
        // uma queda posterior volta ao primeiro nivel de reconexao.
        if (DateTimeOffset.UtcNow - connectedAt >= TimeSpan.FromSeconds(5))
            Interlocked.Exchange(ref _reconnectFailures, 0);
        connectionCts.Cancel();
        try { await Task.WhenAll(send, receive).ConfigureAwait(false); } catch { }
        SetConnected(false, null);
    }

    private Uri RelayUri(string baseUrl)
    {
        var source = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = "/v1/voice/" + Uri.EscapeDataString(_roomId),
            Query = "peer=" + Uri.EscapeDataString(_peerId)
                  + "&sender=" + _senderId
                  + "&nick=" + Uri.EscapeDataString(_nick),
        };
        return builder.Uri;
    }

    private async Task SendLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        await foreach (byte[] packet in _outgoing.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            await socket.SendAsync(packet, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[4096];
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (message.Length + result.Count > buffer.Length) return;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            byte[] payload = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text) ReadPeerSnapshot(payload);
            else if (result.MessageType == WebSocketMessageType.Binary) ReadAudio(payload);
        }
    }

    private void ReadPeerSnapshot(byte[] payload)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(Encoding.UTF8.GetString(payload));
            if (root?["type"]?.GetValue<string>() != "peers" || root["ids"] is not JsonArray ids)
                return;
            var peers = ids.Select(node => node?.GetValue<uint>() ?? 0)
                           .Where(id => id != 0 && id != _senderId).ToArray();
            SetConnected(true, peers);
        }
        catch (Exception ex) { Log.Write("relay voz: snapshot: " + ex.Message); }
    }

    private void ReadAudio(byte[] packet)
    {
        if (packet.Length <= HeaderBytes || packet[0] != AudioPacket) return;
        uint sender = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1, 4));
        if (sender == 0 || sender == _senderId) return;
        bool discovered;
        lock (_peerLock) discovered = _relayPeers.Add(sender);
        if (discovered) StateChanged?.Invoke();
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(5, 2));

        DecoderState state;
        lock (_peerLock)
        {
            if (!_decoders.TryGetValue(sender, out state!))
            {
                state = new DecoderState();
                _decoders[sender] = state;
            }
        }
        try
        {
            if (state.HaveSequence && (ushort)(sequence - state.LastSequence) == 2)
                Emit(sender, state, state.Decoder.Decode(
                    ReadOnlySpan<byte>.Empty, state.Pcm, SamplesPerFrame, false));
            state.LastSequence = sequence;
            state.HaveSequence = true;
            Emit(sender, state, state.Decoder.Decode(
                packet.AsSpan(HeaderBytes), state.Pcm, SamplesPerFrame, false));
        }
        catch (Exception ex) { Log.Write("relay voz: decode: " + ex.Message); }
    }

    private void Emit(uint sender, DecoderState state, int samples)
    {
        if (samples <= 0 || samples * 2 > state.Bytes.Length) return;
        for (int i = 0; i < samples; i++)
        {
            short sample = state.Pcm[i];
            state.Bytes[i * 2] = (byte)sample;
            state.Bytes[i * 2 + 1] = (byte)(sample >> 8);
        }
        VoiceReceived?.Invoke(sender, state.Bytes, 0, samples * 2);
    }

    private void SetConnected(bool connected, IReadOnlyCollection<uint>? peers)
    {
        bool changed;
        lock (_peerLock)
        {
            changed = _connected != connected;
            _connected = connected;
            if (peers != null)
            {
                changed |= !_relayPeers.SetEquals(peers);
                _relayPeers.Clear();
                _relayPeers.UnionWith(peers);
            }
            else if (_relayPeers.Count > 0)
            {
                _relayPeers.Clear();
                changed = true;
            }
        }
        if (!connected)
        {
            while (_outgoing.Reader.TryRead(out _)) { }
            _mic.Reset();
        }
        if (changed) StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }
        _outgoing.Writer.TryComplete();
        SetConnected(false, null);
    }

    private sealed class DecoderState
    {
        public readonly IOpusDecoder Decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        public readonly short[] Pcm = new short[SamplesPerFrame * 2];
        public readonly byte[] Bytes = new byte[SamplesPerFrame * 4];
        public ushort LastSequence;
        public bool HaveSequence;
    }
}

/// <summary>
/// Envia pelo relay e mantem o transporte antigo como fallback/migração. Enquanto
/// houver alguem na sala sem relay, o caminho antigo continua recebendo audio;
/// quando os dois lados aparecem no hub, o audio recebido direto e ignorado para
/// nao tocar duplicado.
/// </summary>
public sealed class ResilientVoiceTransport : IVoiceTransport, IDisposable
{
    private readonly RelayVoiceTransport _relay;
    private readonly IVoiceTransport _fallback;
    private readonly RoomSession _presence;
    private bool _muted;

    public ResilientVoiceTransport(RelayVoiceTransport relay, IVoiceTransport fallback,
                                   RoomSession presence)
    {
        _relay = relay;
        _fallback = fallback;
        _presence = presence;
        _relay.VoiceReceived += OnRelayVoice;
        _fallback.VoiceReceived += OnFallbackVoice;
    }

    public bool Muted
    {
        get => _muted;
        set { _muted = value; _relay.Muted = value; _fallback.Muted = value; }
    }

    public bool Connected => _relay.Connected;
    public int ConnectedPeerCount => _relay.ConnectedPeerCount;
    public event Action<uint, byte[], int, int>? VoiceReceived;
    public event Action? StateChanged
    {
        add => _relay.StateChanged += value;
        remove => _relay.StateChanged -= value;
    }

    public bool HasRelayPeer(uint senderId) => _relay.HasPeer(senderId);
    public void Start() => _relay.Start();

    public void SendVoice(byte[] payload, int offset, int count)
    {
        _relay.SendVoice(payload, offset, count);
        // Durante atualizacao mista, continua falando com clientes antigos. Assim
        // que todos os peers conhecidos aparecem no relay, corta o PCM cru.
        var peers = _presence.Peers;
        if (!_relay.Connected || peers.Any(peer => !_relay.HasPeer(peer.SenderId)))
            _fallback.SendVoice(payload, offset, count);
    }

    private void OnRelayVoice(uint sender, byte[] buffer, int offset, int count)
        => VoiceReceived?.Invoke(sender, buffer, offset, count);

    private void OnFallbackVoice(uint sender, byte[] buffer, int offset, int count)
    {
        if (!_relay.HasPeer(sender)) VoiceReceived?.Invoke(sender, buffer, offset, count);
    }

    public void Dispose()
    {
        _relay.VoiceReceived -= OnRelayVoice;
        _fallback.VoiceReceived -= OnFallbackVoice;
        _relay.Dispose();
    }
}
