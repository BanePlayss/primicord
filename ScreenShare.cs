using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Primicord;

/// <summary>
/// Compartilhar tela: captura o monitor e manda pela malha SO OS PEDACOS QUE MUDARAM.
/// </summary>
/// <remarks>
/// POR QUE MUDOU (a versao anterior ficava horrivel): antes ia o quadro INTEIRO em
/// JPEG a cada 100ms. Como cada quadro custava tudo de novo, pra caber na banda a
/// qualidade tinha que despencar (~45KB por quadro de tela cheia) e ainda encolhia a
/// imagem pra 1152 de largura — resultado: texto virava borrao.
///
/// Agora a tela e dividida numa grade de blocos de 128px. A cada quadro comparamos
/// os pixels bloco a bloco com o quadro anterior e mandamos so os que mudaram. Numa
/// tela normal (navegador, planilha, jogo de camera parada) a maioria dos blocos e
/// identica, entao sobra banda — e a mesma banda vira QUALIDADE: resolucao ate 1600
/// de largura e JPEG em 40..92 em vez de 20..70.
///
/// A cada ~3s (ou quando entra gente nova) vai um quadro COMPLETO, pra consertar
/// bloco que tenha se perdido no caminho e pra quem acabou de chegar ver a tela toda.
///
/// Em modo de nitidez máxima, blocos estáticos usam PNG sem perda e blocos em
/// movimento usam JPEG 100 para não transformar texto em borrão. O delta continua
/// sendo necessário para que esse ganho não exploda a banda a cada quadro.
/// </remarks>
public sealed class ScreenSender : IDisposable
{
    private const int TileSize = 128;

    /// <summary>
    /// Degraus de resolucao, do melhor pro pior. Sob movimento intenso nao adianta
    /// insistir na resolucao cheia: melhor CEDER RESOLUCAO e manter a fluidez do que
    /// segurar tamanho grande com qualidade destruida — e o que codec de verdade faz,
    /// e era o motivo do "144p" (antes so a qualidade caia, nunca a resolucao).
    ///
    /// O degrau 0 e ZERO = tamanho NATIVO da fonte: sem reamostragem nenhuma, que e
    /// ao mesmo tempo o mais nitido e o mais barato (copia direta).
    /// </summary>
    private static readonly int[] WidthLadder = { 0, 1280, 1024, 800 };

    /// <summary>
    /// Blocos re-enviados por quadro mesmo sem terem mudado. E o conserto de perda:
    /// bloco que se perdeu no caminho volta sozinho, sem precisar de quadro completo
    /// (que e grande demais pra passar de uma vez).
    /// </summary>
    // Seis blocos por quadro fecham um ciclo de ressincronização em ~0,4 s
    // numa tela 1080p (135 blocos), em vez de deixar artefatos por vários
    // segundos quando um datagrama UDP se perde.
    private const int RefreshTilesPerFrame = 6;

    /// <summary>
    /// Teto TOTAL de subida da tela (bytes/s), dividido entre os espectadores.
    /// </summary>
    /// <remarks>
    /// O limite de banda divide o envio entre espectadores. Captura, compressão
    /// por CPU e remontagem também limitam a fluidez e são medidas separadamente.
    /// </remarks>
    public int TotalUploadBudget { get; set; } = 900_000;
    /// <summary>Alvo de captura; a taxa efetiva depende do tempo de processamento.</summary>
    public int TargetFps { get; set; } = 60;
    public int EffectiveTargetFps => Math.Clamp(TargetFps, 30, 60);
    public int MaxWidth { get; set; } = 1920;
    /// <summary>Não reduzir resolução automaticamente e priorizar nitidez.</summary>
    public bool MaxQuality { get; set; } = true;
    public string CaptureBackend => _target.UsingGpu ? "DXGI Desktop Duplication" : "GDI window compatibility";
    public string? LastError { get; private set; }

    /// <summary>
    /// Teto quando TODO MUNDO da sala esta na mesma rede local. LAN e gigabit: nao
    /// existe gargalo de subida, entao nao faz sentido apertar a imagem. Com isso a
    /// tela fica em resolucao nativa e qualidade alta.
    /// </summary>
    private const int LanBudget = 8_000_000;

    /// <summary>true quando a sessao inteira e local (mostrado na UI).</summary>
    public bool LanSession { get; private set; }

    private readonly RoomSession _session;
    private readonly CaptureTarget _target;
    private Thread? _thread;
    private volatile bool _running;

    private readonly ImageCodecInfo _jpegCodec;
    private long _quality = 100;
    private volatile bool _sceneBusy;

    /// <summary>Liga a producao de quadros inteiros (custa um encode a mais) pro clipe.</summary>
    public bool NeedFullFrames { get; set; }

    public event Action<byte[], int, int>? FullFrameProduced;
    /// <summary>Prévia local desacoplada da rede, limitada a alguns quadros por segundo.</summary>
    public event Action<Bitmap>? PreviewFrameProduced;

    public int Fps { get; private set; }
    public int SentFps { get; private set; }
    public int IdlePolls { get; private set; }
    public int KbPerSecond { get; private set; }
    public int Quality => (int)_quality;
    public int TilesLastFrame { get; private set; }
    public int CoveragePercent { get; private set; }
    public int OutWidth { get; private set; }
    public int OutHeight { get; private set; }

    /// <summary>Tempo gasto em cada etapa do ultimo quadro (ms) — pra achar gargalo.</summary>
    public int MsCapture { get; private set; }
    public int MsScale { get; private set; }
    public int MsCompare { get; private set; }
    public int MsEncode { get; private set; }
    public int MsSend { get; private set; }
    public int MsTotal => MsCapture + MsScale + MsCompare + MsEncode + MsSend;
    public string TimingSummary { get; private set; } = "aguardando quadros";

    /// <summary>Avisa quando o alvo sumiu (janela fechada) pra UI parar sozinha.</summary>
    public event Action? TargetLost;

    public ScreenSender(RoomSession session, CaptureTarget target)
    {
        _session = session;
        _target = target;
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "primicord-screen" };
        _thread.Start();
        Log.Write($"tela: compartilhando {_target.Name}");
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(800); } catch { }
        _thread = null;
        Log.Write("tela: parou de compartilhar");
    }

    private void Loop()
    {
        int ladder = 0;                 // indice em WidthLadder
        int srcW = 0, srcH = 0;         // tamanho do alvo na ultima montagem
        int outW = 0, outH = 0, cols = 0, rows = 0, stride = 0;

        Bitmap? scaled = null;
        Graphics? gScale = null;

        // prev = estado JA TRANSMITIDO (nao o quadro anterior). Bloco que nao coube
        // no orcamento deste quadro fica com o valor velho aqui e continua marcado
        // como "mudado", entao entra no proximo — nada se perde por falta de espaco.
        byte[] prev = Array.Empty<byte>();
        byte[] cur = Array.Empty<byte>();
        bool primed = false;
        int refreshCursor = 0;
        int transmitCursor = 0;

        var payload = new MemoryStream(512 * 1024);
        var tileMs = new MemoryStream(64 * 1024);
        var ep = new EncoderParameters(1);

        int lastViewers = 0;
        long lastStat = Environment.TickCount64;
        using var clipEncoder = new ScreenClipEncoder((jpeg, w, h) =>
        {
            if (NeedFullFrames) FullFrameProduced?.Invoke(jpeg, w, h);
        });
        double nextFullFrameAt = 0;
        double nextPreviewAt = 0;
        int framesSec = 0, sentSec = 0, idleSec = 0, bytesSec = 0;
        long nextDiagnosticAt = Environment.TickCount64 + 5000;
        long captureMs = 0, copyMs = 0, compareMs = 0, encodeMs = 0, sendMs = 0;
        int timedFrames = 0;
        int overBudgetStreak = 0, underBudgetStreak = 0;

        void Rebuild(int targetW, int targetH, int ladderIdx)
        {
            int w = targetW, h = targetH;
            int maxW = MaxQuality ? 0 : WidthLadder[ladderIdx];
            if (!MaxQuality && MaxWidth > 0) maxW = maxW == 0 ? MaxWidth : Math.Min(maxW, MaxWidth);
            // 0 = nativo (sem reduzir). Tambem nao amplia: se a fonte ja e menor
            // que o degrau, fica no tamanho dela.
            if (maxW > 0 && w > maxW) { h = (int)Math.Round(h * (maxW / (double)w)); w = maxW; }
            w -= w % 2; h -= h % 2;
            if (w < 2 || h < 2) return;

            gScale?.Dispose(); scaled?.Dispose();
            _target.ReleaseWindowDc();

            // Nao existe mais bitmap de tamanho nativo: o StretchBlt entrega ja reduzido.
            scaled = new Bitmap(w, h, PixelFormat.Format32bppRgb);

            gScale = Graphics.FromImage(scaled);

            outW = w; outH = h;
            OutWidth = w; OutHeight = h;
            cols = (outW + TileSize - 1) / TileSize;
            rows = (outH + TileSize - 1) / TileSize;
            srcW = targetW; srcH = targetH;
            primed = false;     // resolucao mudou: reenvia tudo
            Log.Write($"tela: {outW}x{outH} (fonte {targetW}x{targetH}), grade {cols}x{rows}");
        }

        while (_running)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            MsCapture = MsScale = MsCompare = MsEncode = MsSend = 0;
            try
            {
                if (!_target.StillAlive())
                {
                    Log.Write("tela: a janela compartilhada sumiu");
                    TargetLost?.Invoke();
                    break;
                }

                var size = _target.CurrentSize();
                if (size.Width < 2 || size.Height < 2) { Thread.Sleep(200); continue; }

                // A janela pode ser redimensionada a qualquer momento.
                if (size.Width != srcW || size.Height != srcH || scaled == null)
                    Rebuild(size.Width, size.Height, ladder);
                if (scaled == null) { Thread.Sleep(200); continue; }

                // Captura JA reduzida: uma operacao so, sem bitmap de 8MB no meio.
                // Em cena com movimento usa o modo rapido (vale ~10 fps); parada,
                // usa o nitido, que e quando da pra ler texto na tela do outro.
                var tCap = System.Diagnostics.Stopwatch.StartNew();
                var grabbed = _target.Capture(scaled, gScale!, fast: _sceneBusy);
                if (grabbed == CaptureTarget.CaptureResult.Failed) { Thread.Sleep(80); continue; }
                if (grabbed == CaptureTarget.CaptureResult.NoChange)
                {
                    // A GPU avisou que nada mudou na tela. Nao ha o que comparar nem
                    // mandar — so o rodizio de conserto, que segue no proximo ciclo.
                    MsCapture = (int)tCap.ElapsedMilliseconds;
                    idleSec++;
                    goto pacing;
                }
                framesSec++;
                DrawCursorScaled(gScale!, _target.ScreenRect(), outW, outH);
                tCap.Stop();
                MsCapture = (int)tCap.ElapsedMilliseconds;

                var tScale = System.Diagnostics.Stopwatch.StartNew();
                // Copia os pixels pra memoria gerenciada, pra comparar bloco a bloco.
                var bits = scaled.LockBits(new Rectangle(0, 0, outW, outH),
                                           ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                stride = bits.Stride;
                int total = stride * outH;
                if (cur.Length < total) cur = GC.AllocateUninitializedArray<byte>(total, pinned: true);
                Marshal.Copy(bits.Scan0, cur, 0, total);
                scaled.UnlockBits(bits);
                tScale.Stop();
                MsScale = (int)tScale.ElapsedMilliseconds;

                // Local preview reuses the capture, at up to 30 FPS. The UI
                // keeps only the newest bitmap so a slow paint never queues video.
                long previewNow = Environment.TickCount64;
                if (PreviewFrameProduced != null && previewNow >= nextPreviewAt)
                {
                    nextPreviewAt = Math.Max(nextPreviewAt + 1000d / 30, previewNow);
                    try { PreviewFrameProduced(scaled.Clone(new Rectangle(Point.Empty, scaled.Size), scaled.PixelFormat)); } catch { }
                }

                int viewers = Math.Max(1, _session.Peers.Count(p => p.Locked != null));
                bool newViewer = viewers > lastViewers;
                lastViewers = viewers;

                if (prev.Length < total) { prev = new byte[total]; primed = false; }
                // Entrou gente nova (ou mudou a resolucao): tudo precisa ir de novo.
                if (newViewer || !primed) { Array.Clear(prev, 0, total); primed = true; }

                int tileCount = cols * rows;
                // Orcamento DESTE quadro. Quem nao couber fica sujo e vai no proximo,
                // entao o movimento continua fluido em vez de travar esperando.
                // Sessao em LAN: todos os espectadores conectaram por endereco privado.
                var conectados = _session.Peers.Where(p => p.Locked != null).ToList();
                LanSession = conectados.Count > 0 && conectados.All(p => p.OnLan);

                int budget = LanSession ? LanBudget : TotalUploadBudget;
                int perViewer = budget / Math.Max(1, viewers);
                int frameBudget = Math.Max(12_000, perViewer / EffectiveTargetFps);

                var tCmp = System.Diagnostics.Stopwatch.StartNew();
                var dirty = new List<(int C, int R)>();
                // Scan from the last transmitted tile, not always from the
                // top-left. Otherwise a busy scene can starve the bottom forever.
                var refresh = new HashSet<int>();
                for (int i = 0; i < RefreshTilesPerFrame; i++)
                {
                    refresh.Add(refreshCursor++ % tileCount);
                }
                int dirtyTotal = 0;
                for (int i = 0; i < tileCount; i++)
                {
                    int index = (transmitCursor + i) % tileCount;
                    int r = index / cols, c = index % cols;
                    int ty = r * TileSize, th = Math.Min(TileSize, outH - ty);
                    int tx = c * TileSize, tw = Math.Min(TileSize, outW - tx);
                    bool changed = TileChanged(prev, cur, stride, tx, ty, tw, th);
                    if (changed) dirtyTotal++;
                    if (changed || refresh.Contains(index)) dirty.Add((c, r));
                }

                tCmp.Stop();
                MsCompare = (int)tCmp.ElapsedMilliseconds;

                var tEnc = System.Diagnostics.Stopwatch.StartNew();
                payload.SetLength(0);
                payload.Position = 8;      // reserva o cabecalho
                int tilesSent = 0;
                ep.Param[0]?.Dispose();
                ep.Param[0] = new EncoderParameter(Encoder.Quality, _quality);

                foreach (var (c, r) in dirty)
                {
                    if (payload.Length >= frameBudget) break;   // o resto vai no proximo quadro

                    int tx = c * TileSize, ty = r * TileSize;
                    int tw = Math.Min(TileSize, outW - tx), th = Math.Min(TileSize, outH - ty);
                    if (tw <= 0 || th <= 0) continue;

                    // Read-only view into this cycle's pinned pixels. Avoid a
                    // GDI+ DrawImage and its pixel conversion for every tile.
                    using var tile = TileView(cur, stride, tx, ty, tw, th);

                    tileMs.SetLength(0);
                    if (MaxQuality && !_sceneBusy)
                    {
                        // Texto, HUD e janelas paradas ficam realmente sem perda.
                        // Em movimento, PNG custaria mais que o orçamento inteiro.
                        tile.Save(tileMs, ImageFormat.Png);
                        if (tileMs.Length > ushort.MaxValue)
                        {
                            tileMs.SetLength(0);
                            tile.Save(tileMs, _jpegCodec, ep);
                        }
                    }
                    else tile.Save(tileMs, _jpegCodec, ep);

                    int jlen = (int)tileMs.Length;
                    if (jlen > ushort.MaxValue) continue;
                    payload.WriteByte((byte)c);
                    payload.WriteByte((byte)r);
                    payload.WriteByte((byte)jlen);
                    payload.WriteByte((byte)(jlen >> 8));
                    payload.Write(tileMs.GetBuffer(), 0, jlen);
                    tilesSent++;
                    transmitCursor = (r * cols + c + 1) % tileCount;

                    // Bloco transmitido: marca como sincronizado copiando pro prev.
                    for (int row = 0; row < th; row++)
                    {
                        int off = (ty + row) * stride + tx * 4;
                        Buffer.BlockCopy(cur, off, prev, off, tw * 4);
                    }
                }

                tEnc.Stop();
                MsEncode = (int)tEnc.ElapsedMilliseconds;

                var tSend = System.Diagnostics.Stopwatch.StartNew();
                if (tilesSent > 0)
                {
                    var buf = payload.GetBuffer();
                    buf[0] = 0;
                    buf[1] = (byte)tilesSent;
                    buf[2] = (byte)(tilesSent >> 8);
                    buf[3] = TileSize / 8;
                    buf[4] = (byte)outW; buf[5] = (byte)(outW >> 8);
                    buf[6] = (byte)outH; buf[7] = (byte)(outH >> 8);

                    int plen = (int)payload.Length;
                    _session.SendScreenFrame(buf, plen, outW, outH);
                    if (conectados.Count > 0) { sentSec++; bytesSec += plen; }
                }

                tSend.Stop();
                MsSend = (int)tSend.ElapsedMilliseconds;
                TilesLastFrame = tilesSent;
                CoveragePercent = tilesSent * 100 / tileCount;

                // ── adaptacao ──
                // Muita coisa mudando e nao coube: e cena de movimento. Primeiro
                // baixa qualidade; se persistir, DESCE DE RESOLUCAO — o que salva a
                // fluidez de verdade. Com folga, sobe de volta.
                bool overBudget = dirtyTotal > tilesSent + 2;
                // Cena "agitada" = mais de um terco dos blocos mudou. Alimenta a
                // escolha do modo de captura do proximo quadro.
                _sceneBusy = dirtyTotal > tileCount / 3;
                if (overBudget)
                {
                    underBudgetStreak = 0;
                    if (++overBudgetStreak > 6)
                    {
                        overBudgetStreak = 0;
                        if (MaxQuality)
                        {
                            if (_quality > 92) _quality -= 2;
                        }
                        else if (_quality > 45) _quality -= 8;
                        else if (ladder < WidthLadder.Length - 1)
                        {
                            ladder++;
                            _quality = 70;
                            Rebuild(srcW, srcH, ladder);
                        }
                    }
                }
                else
                {
                    overBudgetStreak = 0;
                    if (++underBudgetStreak > 25)
                    {
                        underBudgetStreak = 0;
                        if (MaxQuality)
                        {
                            if (_quality < 100) _quality += 2;
                        }
                        else if (_quality < 92) _quality += 4;
                        else if (ladder > 0)
                        {
                            ladder--;
                            Rebuild(srcW, srcH, ladder);
                        }
                    }
                }

                long nowFull = Environment.TickCount64;
                if (NeedFullFrames && nowFull >= nextFullFrameAt)
                {
                    nextFullFrameAt = Math.Max(nextFullFrameAt + 1000d / 30, nowFull);
                    clipEncoder.Publish(scaled.Clone(new Rectangle(Point.Empty, scaled.Size), scaled.PixelFormat));
                }
                captureMs += MsCapture; copyMs += MsScale; compareMs += MsCompare;
                encodeMs += MsEncode; sendMs += MsSend; timedFrames++;

            }
            catch (Exception ex)
            {
                Log.Write("captura de tela falhou: " + ex.Message);
                LastError = ex.Message;
                Thread.Sleep(300);
            }

        pacing:
            if (Environment.TickCount64 - lastStat >= 1000)
            {
                double seconds = (Environment.TickCount64 - lastStat) / 1000d;
                Fps = (int)Math.Round(framesSec / seconds);
                SentFps = (int)Math.Round(sentSec / seconds);
                IdlePolls = (int)Math.Round(idleSec / seconds);
                KbPerSecond = (int)(bytesSec / seconds / 1024);
                if (timedFrames > 0)
                    TimingSummary = $"{captureMs / (double)timedFrames:F1}/{copyMs / (double)timedFrames:F1}/" +
                        $"{compareMs / (double)timedFrames:F1}/{encodeMs / (double)timedFrames:F1}/{sendMs / (double)timedFrames:F1}";
                captureMs = copyMs = compareMs = encodeMs = sendMs = 0;
                timedFrames = 0;
                framesSec = sentSec = idleSec = bytesSec = 0;
                lastStat = Environment.TickCount64;
            }
            if (Environment.TickCount64 >= nextDiagnosticAt)
            {
                nextDiagnosticAt = Environment.TickCount64 + 5000;
                Log.Write($"screen-perf: capture={Fps} sent={SentFps} idle={IdlePolls} target={EffectiveTargetFps} " +
                    $"avg-ms capture/copy/compare/encode/send={TimingSummary} " +
                    $"backend={CaptureBackend} size={OutWidth}x{OutHeight} coverage={CoveragePercent}% clip={NeedFullFrames}");
            }

            ScreenPacing.Wait(t0, EffectiveTargetFps);
        }

        gScale?.Dispose(); scaled?.Dispose();
        _target.ReleaseWindowDc();
        _target.ReleaseGpu();
        ep.Dispose();
        payload.Dispose();
        tileMs.Dispose();
    }

    /// <summary>Compara um bloco entre o quadro anterior e o atual, linha por linha.</summary>
    private static bool TileChanged(byte[] prev, byte[] cur, int stride, int x, int y, int w, int h)
    {
        int byteX = x * 4;
        int byteW = w * 4;
        for (int row = 0; row < h; row++)
        {
            int off = (y + row) * stride + byteX;
            if (!new ReadOnlySpan<byte>(prev, off, byteW)
                    .SequenceEqual(new ReadOnlySpan<byte>(cur, off, byteW)))
                return true;
        }
        return false;
    }

    private static unsafe Bitmap TileView(byte[] pixels, int stride, int x, int y, int w, int h)
    {
        // The caller allocates pixels on the pinned object heap and keeps it
        // alive until every tile view is disposed, before the next capture.
        fixed (byte* ptr = pixels)
            return new Bitmap(w, h, stride, PixelFormat.Format32bppRgb, (IntPtr)(ptr + y * stride + x * 4));
    }

    // ─── cursor (o CopyFromScreen nao traz) ──────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize; public int flags; public IntPtr hCursor; public Point ptScreenPos;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo pci);
    [DllImport("user32.dll")] private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);
    private const int CursorShowing = 0x00000001;

    /// <summary>
    /// Desenha o cursor na posicao proporcional — a captura ja vem reduzida, entao a
    /// coordenada da tela precisa ser convertida pra escala do quadro.
    /// </summary>
    private static void DrawCursorScaled(Graphics g, Rectangle area, int outW, int outH)
    {
        try
        {
            if (area.Width <= 0 || area.Height <= 0) return;
            var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & CursorShowing) == 0) return;
            if (!area.Contains(ci.ptScreenPos)) return;

            int x = (int)((ci.ptScreenPos.X - area.X) * (outW / (double)area.Width));
            int y = (int)((ci.ptScreenPos.Y - area.Y) * (outH / (double)area.Height));
            IntPtr hdc = g.GetHdc();
            try { DrawIcon(hdc, x, y, ci.hCursor); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { /* cursor e enfeite; nunca derruba a captura */ }
    }
}

/// <summary>
/// Remonta a tela de cada pessoa aplicando os blocos recebidos sobre uma tela
/// persistente (o que nao muda continua valendo do quadro anterior).
/// </summary>
public sealed class ScreenReceiver : IDisposable
{
    private sealed class Canvas
    {
        public Bitmap Bmp = null!;
        public Graphics G = null!;
        public long LastTicks;
        public int W, H;
    }

    private readonly object _lock = new();
    private readonly Dictionary<uint, Canvas> _canvases = new();

    public event Action<uint>? FrameUpdated;

    /// <summary>Aplica um pacote de blocos. Devolve false se o pacote veio estranho.</summary>
    public bool OnUpdate(uint senderId, byte[] payload, int w, int h)
    {
        if (payload.Length < 8) return false;

        int tileCount = payload[1] | (payload[2] << 8);
        int tileSize = payload[3] * 8;
        int frameW = payload[4] | (payload[5] << 8);
        int frameH = payload[6] | (payload[7] << 8);
        if (tileSize <= 0 || frameW <= 0 || frameH <= 0 || frameW > 7680 || frameH > 4320
            || tileCount <= 0 || tileCount > 4096) return false;

        Canvas canvas;
        lock (_lock)
        {
            if (!_canvases.TryGetValue(senderId, out canvas!) ||
                canvas.W != frameW || canvas.H != frameH)
            {
                // Cria a tela no PRIMEIRO pacote, seja ele qual for, preta no que
                // ainda nao chegou. Exigir um quadro completo pra comecar era o bug:
                // o quadro completo era grande demais pra passar e a tela nunca
                // nascia — todo delta era recusado.
                if (canvas != null) { try { canvas.G.Dispose(); canvas.Bmp.Dispose(); } catch { } }
                var bmp = new Bitmap(frameW, frameH, PixelFormat.Format24bppRgb);
                // Network frame dimensions are pixels, never physical display units.
                bmp.SetResolution(96, 96);
                canvas = new Canvas { Bmp = bmp, G = Graphics.FromImage(bmp), W = frameW, H = frameH };
                canvas.G.PageUnit = GraphicsUnit.Pixel;
                canvas.G.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                canvas.G.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                canvas.G.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                _canvases[senderId] = canvas;
            }
        }

        int pos = 8;
        int applied = 0;
        for (int i = 0; i < tileCount && pos + 4 <= payload.Length; i++)
        {
            int col = payload[pos];
            int row = payload[pos + 1];
            int jlen = payload[pos + 2] | (payload[pos + 3] << 8);
            pos += 4;
            if (jlen <= 0 || pos + jlen > payload.Length) break;
            if (col * tileSize >= frameW || row * tileSize >= frameH) { pos += jlen; continue; }

            try
            {
                using var ms = new MemoryStream(payload, pos, jlen, writable: false);
                using var img = Image.FromStream(ms);
                int x = col * tileSize, y = row * tileSize;
                int expectedW = Math.Min(tileSize, frameW - x), expectedH = Math.Min(tileSize, frameH - y);
                if (img.Width != expectedW || img.Height != expectedH) { pos += jlen; continue; }
                lock (_lock)
                {
                    // DrawImageUnscaled uses PHYSICAL size (source/destination DPI),
                    // despite its name. A 128px/144-DPI tile became ~85px at 96 DPI,
                    // leaving a regular black grid. Explicit pixel rectangles preserve
                    // 1:1 geometry for old senders too, regardless of PNG/JPEG metadata.
                    canvas.G.DrawImage(img, new Rectangle(x, y, expectedW, expectedH),
                        0, 0, expectedW, expectedH, GraphicsUnit.Pixel);
                }
                applied++;
            }
            catch { /* bloco corrompido: o proximo keyframe conserta */ }
            pos += jlen;
        }

        if (applied == 0) return false;
        lock (_lock) canvas.LastTicks = DateTime.UtcNow.Ticks;
        FrameUpdated?.Invoke(senderId);
        return true;
    }

    /// <summary>Tela de alguem, ou null se parou de mandar ha mais de 4s.</summary>
    public Bitmap? FrameOf(uint senderId)
    {
        lock (_lock)
        {
            if (!_canvases.TryGetValue(senderId, out var c)) return null;
            if (DateTime.UtcNow.Ticks - c.LastTicks > TimeSpan.TicksPerSecond * 4) return null;
            // A cópia impede que o paint da UI leia uma superfície enquanto a
            // thread UDP aplica um tile novo nela. O clone é descartado pelo palco.
            try { return new Bitmap(c.Bmp); } catch { return null; }
        }
    }

    /// <summary>Codifica a tela atual em JPEG (o gravador de clipe precisa assim).</summary>
    public byte[]? EncodeFrame(uint senderId, long quality = 70)
    {
        lock (_lock)
        {
            if (!_canvases.TryGetValue(senderId, out var c)) return null;
            try
            {
                var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                using var ms = new MemoryStream(256 * 1024);
                c.Bmp.Save(ms, codec, ep);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }

    public (int W, int H) SizeOf(uint senderId)
    {
        lock (_lock) return _canvases.TryGetValue(senderId, out var c) ? (c.W, c.H) : (0, 0);
    }

    public void Remove(uint senderId)
    {
        lock (_lock)
        {
            if (!_canvases.TryGetValue(senderId, out var c)) return;
            try { c.G.Dispose(); c.Bmp.Dispose(); } catch { }
            _canvases.Remove(senderId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _canvases.Values) { try { c.G.Dispose(); c.Bmp.Dispose(); } catch { } }
            _canvases.Clear();
        }
    }
}
