using Concentus;
using Concentus.Enums;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace Primicord;

/// <summary>
/// Voz por WebRTC: uma <see cref="RTCPeerConnection"/> por pessoa da sala, com
/// Opus em cima. Substitui o caminho de voz da malha UDP propria.
/// </summary>
/// <remarks>
/// O QUE ISTO TROCA, EM NUMERO (medido, nao estimado): a malha antiga manda PCM
/// 48kHz 16-bit CRU — 768 kbps de payload, ~798 kbps com cabecalho, por par e por
/// direcao. Numa sala de 6 cada um subia 4 Mbps so de voz.
///
///   falando sem parar     49,2 kbps por par   (29,2 de Opus + 20,0 de RTP/UDP/IP)
///   call real (~50% fala) 27,2 kbps por par   (o DTX corta o silencio)
///
/// Uma sala de 6 sai de ~4 Mbps pra ~250 kbps no pior caso. E 16x falando sem
/// parar, ~29x no uso normal.
///
/// ALEM DA BANDA, o que vem junto de graca por ser RTP/SRTP de verdade:
///   - criptografia (DTLS-SRTP) — a malha antiga mandava voz em texto puro;
///   - FEC em banda do Opus + PLC, entao perda de pacote vira imperfeicao e nao buraco;
///   - ICE, que tenta varios caminhos em vez do nosso furo unico por STUN.
///
/// O QUE ISTO **NAO** RESOLVE: continua sem relay. ICE sem servidor TURN esbarra
/// no mesmo NAT simetrico dos dois lados que a malha antiga esbarrava. O ganho
/// aqui e codec e seguranca, nao alcance. Pra alcance, ou entra um TURN, ou entra
/// a rede privada (ver PLANO-DE-MIGRACAO.md).
///
/// CONVIVENCIA COM A MALHA ANTIGA: a <see cref="RoomSession"/> continua viva e
/// continua carregando tela, musica, cinema e presenca — este objeto so tira a VOZ
/// de la. Ele inclusive usa a RoomSession como lista de presenca: quem descobre
/// que fulano entrou na sala continua sendo o poll de presenca dela.
///
/// POR QUE UMA CONEXAO POR PESSOA (malha) e nao uma SFU: o mesmo motivo da malha
/// antiga — nao tem servidor, ninguem paga hospedagem, e com Opus a subida de uma
/// sala de 6 cabe folgada em qualquer banda larga. Acima de ~10 pessoas a conta
/// vira ruim e ai sim precisaria de SFU.
/// </remarks>
public sealed class WebRtcVoiceMesh : IVoiceTransport, IDisposable
{
    // ─── formato ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 20ms por pacote em vez dos 10ms da malha antiga. O payload Opus quase nao
    /// muda, mas o cabecalho (RTP 12 + UDP 8 + IPv4 20 + tag SRTP 10 = 50 bytes)
    /// passa a ser pago 50 vezes por segundo em vez de 100 — economiza 20 kbps por
    /// par, quase metade do custo total. Os 10ms a mais de latencia somem perto do
    /// jitter da rede.
    /// </summary>
    private const int SamplesPerFrame = 960;              // 20ms @ 48kHz
    private const int PcmBytesPerFrame = SamplesPerFrame * 2;
    private const int OpusPayloadType = 111;              // dinamico, o de praxe pra Opus
    private const int OpusBitrate = 32_000;
    private const int MaxOpusPacket = 1275;               // teto de um frame Opus

    /// <summary>
    /// MONO de proposito. O padrao do WebRTC anuncia opus/48000/2 porque
    /// navegador espera estereo, mas o Primicord e mono de ponta a ponta (o
    /// microfone, o mixer, o gravador de clipe) e os dois lados aqui sao sempre
    /// Primicord. Anunciar 1 canal evita conversao inutil e corta a taxa pela metade.
    /// </summary>
    private static AudioFormat OpusFormat =>
        new(AudioCodecsEnum.OPUS, OpusPayloadType, 48000, 1, "useinbandfec=1");

    // ─── tempos ──────────────────────────────────────────────────────────────

    private const int TickMs = 2000;            // mesmo compasso do poll de presenca
    private const int GatherTimeoutMs = 4000;   // teto pra coleta de candidatos ICE
    private const int RenegotiateAfterMs = 15000;

    // ─── estado ──────────────────────────────────────────────────────────────

    private readonly WebRtcSignaling _sig;
    private readonly RoomSession _session;
    private readonly string _myPeerId;

    private readonly object _linksLock = new();
    private readonly Dictionary<string, Link> _links = new(StringComparer.Ordinal);

    private readonly IOpusEncoder _encoder;
    private readonly FrameAccumulator _micAcc = new();
    private readonly byte[] _pcmFrame = new byte[PcmBytesPerFrame];
    private readonly short[] _shortFrame = new short[SamplesPerFrame];
    private readonly byte[] _opusOut = new byte[MaxOpusPacket];
    private readonly object _encodeLock = new();

    private CancellationTokenSource? _cts;
    private volatile bool _running;

    public bool Muted { get; set; }

    /// <summary>PCM decodificado de alguem — o <see cref="VoiceEngine"/> escuta aqui.</summary>
    public event Action<uint, byte[], int, int>? VoiceReceived;

    /// <summary>Mudou o estado de alguma conexao (pra UI redesenhar).</summary>
    public event Action? StateChanged;

    /// <summary>Erro que o usuario precisa ver.</summary>
    public event Action<string>? Failed;

    /// <summary>Uma pessoa da sala e a conexao WebRTC pra ela.</summary>
    private sealed class Link
    {
        public string PeerId = "";
        public string Nick = "";
        public uint SenderId;
        public bool IAmOfferer;

        public RTCPeerConnection? Pc;
        public IOpusDecoder? Decoder;

        /// <summary>Qual tentativa de negociacao esta valendo (timestamp do ofertante).</summary>
        public long Epoch;

        public long NegotiationStartedMs;
        public bool RemoteApplied;
        public bool Busy;

        public RTCPeerConnectionState State = RTCPeerConnectionState.closed;

        // decodificacao (por peer: o decodificador Opus tem estado por fluxo)
        public readonly short[] PcmOut = new short[SamplesPerFrame * 2];
        public readonly byte[] ByteOut = new byte[SamplesPerFrame * 4];
        public ushort LastSeq;
        public bool HaveSeq;

        public void CloseConnection()
        {
            try { Pc?.close(); } catch { }
            try { Pc?.Dispose(); } catch { }
            Pc = null;
            Decoder = null;
            RemoteApplied = false;
            HaveSeq = false;
            State = RTCPeerConnectionState.closed;
        }
    }

    public WebRtcVoiceMesh(Firestore fs, string roomId, string myPeerId, RoomSession session)
    {
        _sig = new WebRtcSignaling(fs, roomId, myPeerId);
        _session = session;
        _myPeerId = myPeerId;

        _encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = OpusBitrate;
        _encoder.UseVBR = true;
        // DTX: silencio nao vira pacote. Numa sala de 6 quase todo mundo esta calado
        // quase o tempo todo, entao isto sozinho corta a maior parte do trafego.
        _encoder.UseDTX = true;
        // FEC em banda: o Opus embute uma copia degradada do quadro anterior, entao
        // um pacote perdido e reconstruido em vez de virar buraco.
        _encoder.UseInbandFEC = true;
        _encoder.PacketLossPercent = 5;
        _encoder.Complexity = 8;
    }

    /// <summary>Quantas conexoes de voz estao de pe.</summary>
    public int ConnectedCount
    {
        get { lock (_linksLock) return _links.Values.Count(l => l.State == RTCPeerConnectionState.connected); }
    }

    /// <summary>Total de pessoas com quem se esta tentando falar.</summary>
    public int LinkCount { get { lock (_linksLock) return _links.Count; } }

    /// <summary>Estado da conexao com alguem, pra UI.</summary>
    public RTCPeerConnectionState StateOf(uint senderId)
    {
        lock (_linksLock)
            foreach (var l in _links.Values)
                if (l.SenderId == senderId) return l.State;
        return RTCPeerConnectionState.closed;
    }

    // ─── CICLO DE VIDA ───────────────────────────────────────────────────────

    public void Start()
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        Log.Write("webrtc: malha de voz iniciada");
    }

    public void Dispose()
    {
        if (!_running) return;
        _running = false;
        try { _cts?.Cancel(); } catch { }

        List<Link> links;
        lock (_linksLock) { links = _links.Values.ToList(); _links.Clear(); }

        foreach (var l in links)
        {
            l.CloseConnection();
            // Melhor esforco: some com o SDP pra proxima sessao nao achar oferta morta.
            if (l.IAmOfferer) try { _sig.ClearAsync(l.PeerId).Wait(1000); } catch { }
        }

        try { _encoder.ResetState(); } catch { }
        try { _cts?.Dispose(); } catch { }
        Log.Write("webrtc: malha de voz encerrada");
    }

    // ─── LAÇO DE NEGOCIACAO ──────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write("webrtc: tick falhou: " + ex.Message); }

            try { await Task.Delay(TickMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // A RoomSession continua sendo quem descobre quem esta na sala.
        var peers = _session.Peers;
        var alive = new HashSet<string>(peers.Select(p => p.PeerId), StringComparer.Ordinal);
        bool changed = false;

        lock (_linksLock)
        {
            foreach (var p in peers)
            {
                if (_links.ContainsKey(p.PeerId)) continue;
                _links[p.PeerId] = new Link
                {
                    PeerId = p.PeerId,
                    Nick = p.Nick,
                    SenderId = p.SenderId,
                    IAmOfferer = _sig.IsOfferer(p.PeerId),
                };
                changed = true;
                Log.Write($"webrtc: novo par {p.Nick} ({p.PeerId}) — eu " +
                          (_sig.IsOfferer(p.PeerId) ? "oferto" : "respondo"));
            }

            foreach (string gone in _links.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                var l = _links[gone];
                _links.Remove(gone);
                l.CloseConnection();
                if (l.IAmOfferer) _ = _sig.ClearAsync(gone);
                changed = true;
                Log.Write($"webrtc: par {l.Nick} saiu");
            }
        }

        if (changed) StateChanged?.Invoke();

        List<Link> snapshot;
        lock (_linksLock) snapshot = _links.Values.ToList();

        foreach (var link in snapshot)
        {
            if (link.Busy) continue;
            try
            {
                if (link.IAmOfferer) await DriveOffererAsync(link, ct).ConfigureAwait(false);
                else await DriveAnswererAsync(link, ct).ConfigureAwait(false);
            }
            catch (FirestoreException ex)
            {
                Log.Write($"webrtc: sinalizacao com {link.Nick} falhou: {ex.Message}");
                if (ex.IsPermissionDenied)
                {
                    Failed?.Invoke("O Firestore recusou a sinalizacao do WebRTC — falta "
                                 + "publicar as rules de pc_rooms/{sala}/signal no Console.");
                    return;
                }
            }
            catch (Exception ex) { Log.Write($"webrtc: negociar com {link.Nick} falhou: {ex.Message}"); }
        }
    }

    /// <summary>Lado que oferta: publica a oferta e espera a resposta aparecer.</summary>
    private async Task DriveOffererAsync(Link link, CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        bool needsOffer = link.Pc is null;
        // Nao respondeu e nao conectou: a oferta se perdeu, ou o outro lado
        // reiniciou depois de ler. Refaz do zero (epoch novo forca ele a reagir).
        if (!needsOffer && !link.RemoteApplied &&
            link.State != RTCPeerConnectionState.connected &&
            now - link.NegotiationStartedMs > RenegotiateAfterMs)
        {
            Log.Write($"webrtc: {link.Nick} nao respondeu em {RenegotiateAfterMs / 1000}s, reofertando");
            link.CloseConnection();
            needsOffer = true;
        }

        if (needsOffer)
        {
            link.Busy = true;
            try
            {
                var pc = CreateConnection(link);
                var offer = pc.createOffer(null);
                await pc.setLocalDescription(offer).ConfigureAwait(false);

                var cands = await GatherAsync(pc, ct).ConfigureAwait(false);

                link.Epoch = now;
                link.NegotiationStartedMs = now;
                await _sig.PublishOfferAsync(link.PeerId, link.Epoch, offer.sdp, cands, ct)
                          .ConfigureAwait(false);
            }
            finally { link.Busy = false; }
            return;
        }

        if (link.RemoteApplied) return;

        var doc = await _sig.ReadAsync(link.PeerId, ct).ConfigureAwait(false);
        if (doc is null || !doc.HasAnswer || doc.AnswerAt < link.NegotiationStartedMs) return;

        link.Busy = true;
        try
        {
            var init = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = doc.Answer };
            var result = link.Pc!.setRemoteDescription(init);
            if (result != SetDescriptionResultEnum.OK)
            {
                Log.Write($"webrtc: resposta de {link.Nick} recusada: {result}");
                link.CloseConnection();
                return;
            }
            ApplyCandidates(link.Pc, doc.AnswerCandidates);
            link.RemoteApplied = true;
            Log.Write($"webrtc: resposta de {link.Nick} aceita");
        }
        finally { link.Busy = false; }
    }

    /// <summary>Lado que responde: espera a oferta, devolve a resposta.</summary>
    private async Task DriveAnswererAsync(Link link, CancellationToken ct)
    {
        var doc = await _sig.ReadAsync(link.PeerId, ct).ConfigureAwait(false);
        if (doc is null || !doc.HasOffer) return;

        // Ja negociei esta oferta. Oferta com epoch novo = ele refez, entao refaco tambem.
        if (doc.Epoch == link.Epoch && link.Pc != null) return;

        link.Busy = true;
        try
        {
            if (link.Pc != null) link.CloseConnection();

            var pc = CreateConnection(link);
            var offerInit = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = doc.Offer };
            var result = pc.setRemoteDescription(offerInit);
            if (result != SetDescriptionResultEnum.OK)
            {
                Log.Write($"webrtc: oferta de {link.Nick} recusada: {result}");
                link.CloseConnection();
                return;
            }
            ApplyCandidates(pc, doc.OfferCandidates);

            var answer = pc.createAnswer(null);
            await pc.setLocalDescription(answer).ConfigureAwait(false);

            var cands = await GatherAsync(pc, ct).ConfigureAwait(false);

            link.Epoch = doc.Epoch;
            link.NegotiationStartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            link.RemoteApplied = true;
            await _sig.PublishAnswerAsync(link.PeerId, answer.sdp, cands, ct).ConfigureAwait(false);
        }
        finally { link.Busy = false; }
    }

    // ─── CONEXAO ─────────────────────────────────────────────────────────────

    private RTCPeerConnection CreateConnection(Link link)
    {
        var config = new RTCConfiguration
        {
            iceServers = Stun.DefaultServers
                .Select(s => new RTCIceServer { urls = "stun:" + s })
                .ToList(),
        };

        var pc = new RTCPeerConnection(config);
        pc.addTrack(new MediaStreamTrack(OpusFormat, MediaStreamStatusEnum.SendRecv));

        link.Decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        link.Pc = pc;
        link.HaveSeq = false;

        pc.onconnectionstatechange += state =>
        {
            link.State = state;
            Log.Write($"webrtc: {link.Nick} -> {state}");
            StateChanged?.Invoke();

            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected)
            {
                // Deixa o laco reofertar: zera pra que o proximo tick recomece.
                link.RemoteApplied = false;
                link.NegotiationStartedMs = 0;
            }
        };

        pc.OnRtpPacketReceived += (_, media, pkt) =>
        {
            if (media != SDPMediaTypesEnum.audio) return;
            OnAudioPacket(link, pkt);
        };

        return pc;
    }

    /// <summary>
    /// Espera a coleta de candidatos ICE terminar e devolve todos de uma vez.
    /// </summary>
    /// <remarks>
    /// E aqui que o "nao-trickle" acontece (ver <see cref="WebRtcSignaling"/>). O
    /// timeout existe porque um servidor STUN mudo deixaria a coleta pendurada pra
    /// sempre — melhor publicar so os candidatos locais e conectar em LAN do que
    /// nao publicar nada.
    /// </remarks>
    private static async Task<List<string>> GatherAsync(RTCPeerConnection pc, CancellationToken ct)
    {
        var cands = new List<string>();
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();

        void OnCandidate(RTCIceCandidate c)
        {
            if (c is null) return;
            try { lock (gate) cands.Add(c.toJSON()); } catch { }
        }

        void OnGathering(RTCIceGatheringState s)
        {
            if (s == RTCIceGatheringState.complete) done.TrySetResult(true);
        }

        pc.onicecandidate += OnCandidate;
        pc.onicegatheringstatechange += OnGathering;
        try
        {
            if (pc.iceGatheringState == RTCIceGatheringState.complete) done.TrySetResult(true);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GatherTimeoutMs);
            using (timeout.Token.Register(() => done.TrySetResult(false)))
                await done.Task.ConfigureAwait(false);
        }
        finally
        {
            pc.onicecandidate -= OnCandidate;
            pc.onicegatheringstatechange -= OnGathering;
        }

        lock (gate)
        {
            Log.Write($"webrtc: {cands.Count} candidatos coletados");
            return cands.ToList();
        }
    }

    private static void ApplyCandidates(RTCPeerConnection pc, string blob)
    {
        int ok = 0;
        foreach (string json in WebRtcSignaling.SplitCandidates(blob))
        {
            try
            {
                if (!RTCIceCandidateInit.TryParse(json, out var init)) continue;
                pc.addIceCandidate(init);
                ok++;
            }
            catch (Exception ex) { Log.Write("webrtc: candidato ilegivel: " + ex.Message); }
        }
        Log.Write($"webrtc: {ok} candidatos remotos aplicados");
    }

    // ─── MICROFONE -> REDE ───────────────────────────────────────────────────

    public void SendVoice(byte[] payload, int offset, int count)
    {
        if (!_running || Muted || count <= 0) return;

        // O VoiceEngine entrega 10ms; o Opus daqui trabalha em 20ms. O acumulador
        // junta dois frames antes de comprimir.
        _micAcc.Append(payload, offset, count);

        while (_micAcc.TryDequeueFrame(_pcmFrame, 0, PcmBytesPerFrame))
        {
            byte[] packet;
            lock (_encodeLock)
            {
                for (int i = 0; i < SamplesPerFrame; i++)
                    _shortFrame[i] = (short)(_pcmFrame[i * 2] | (_pcmFrame[i * 2 + 1] << 8));

                int len;
                try { len = _encoder.Encode(_shortFrame, SamplesPerFrame, _opusOut, _opusOut.Length); }
                catch (Exception ex) { Log.Write("webrtc: encode falhou: " + ex.Message); return; }

                // DTX ligado devolve pacotes de 1-2 bytes (ou zero) no silencio:
                // nao ha o que transmitir.
                if (len <= 2) continue;

                packet = new byte[len];
                Buffer.BlockCopy(_opusOut, 0, packet, 0, len);
            }

            List<Link> targets;
            lock (_linksLock)
                targets = _links.Values.Where(l => l.State == RTCPeerConnectionState.connected).ToList();

            foreach (var l in targets)
            {
                try { l.Pc?.SendAudio(SamplesPerFrame, packet); }
                catch (Exception ex) { Log.Write($"webrtc: envio p/ {l.Nick} falhou: {ex.Message}"); }
            }
        }
    }

    // ─── REDE -> ALTO-FALANTE ────────────────────────────────────────────────

    private void OnAudioPacket(Link link, RTPPacket pkt)
    {
        var dec = link.Decoder;
        if (dec is null || !_running) return;

        byte[] payload = pkt.Payload;
        if (payload is null || payload.Length == 0) return;

        try
        {
            ushort seq = pkt.Header.SequenceNumber;

            // Um pacote perdido: pede ao Opus pra inventar o quadro que faltou (PLC)
            // antes de decodificar o que chegou. Sem isso a perda vira um estalo.
            if (link.HaveSeq && (ushort)(seq - link.LastSeq) == 2)
                Emit(link, dec.Decode(ReadOnlySpan<byte>.Empty, link.PcmOut, SamplesPerFrame, false));

            link.LastSeq = seq;
            link.HaveSeq = true;

            Emit(link, dec.Decode(payload, link.PcmOut, SamplesPerFrame, false));
        }
        catch (Exception ex) { Log.Write($"webrtc: decode de {link.Nick} falhou: {ex.Message}"); }
    }

    private void Emit(Link link, int samples)
    {
        if (samples <= 0) return;
        int bytes = samples * 2;
        if (bytes > link.ByteOut.Length) return;

        for (int i = 0; i < samples; i++)
        {
            short s = link.PcmOut[i];
            link.ByteOut[i * 2] = (byte)s;
            link.ByteOut[i * 2 + 1] = (byte)(s >> 8);
        }
        VoiceReceived?.Invoke(link.SenderId, link.ByteOut, 0, bytes);
    }
}
