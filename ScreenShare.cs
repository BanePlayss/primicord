using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Primicord;

/// <summary>
/// Compartilhar tela: captura o monitor, comprime em JPEG e manda picado pela malha.
/// </summary>
/// <remarks>
/// POR QUE JPEG E NAO H.264: H.264 precisaria de Media Foundation por COM (muito
/// interop) ou de um binario de FFmpeg junto do exe. JPEG quadro-a-quadro (o velho
/// "motion JPEG") usa so o que o .NET ja tem, e pra "assiste eu jogar" a 10 fps
/// resolve. Custa mais banda: ~2 Mbps por pessoa contra ~0,3 do H.264 — por isso a
/// resolucao cai pra 1152 de largura e a qualidade se ajusta sozinha.
///
/// Como e malha, o mesmo quadro sobe uma vez POR PESSOA na sala. Com 4 assistindo
/// sao ~8 Mbps de subida; acima disso o app baixa a qualidade sozinho.
/// </remarks>
public sealed class ScreenSender : IDisposable
{
    private const int MaxWidth = 1152;
    private const int TargetFps = 10;

    private readonly RoomSession _session;
    private readonly Rectangle _area;
    private Thread? _thread;
    private volatile bool _running;

    private Bitmap? _grab, _scaled;
    private readonly ImageCodecInfo _jpegCodec;
    private long _quality = 55;

    /// <summary>Ultimo quadro codificado (o gravador de clipe consome daqui).</summary>
    public byte[]? LastFrame { get; private set; }
    public int LastWidth { get; private set; }
    public int LastHeight { get; private set; }
    public event Action<byte[], int, int>? FrameProduced;

    public int Fps { get; private set; }
    public int KbPerSecond { get; private set; }

    public ScreenSender(RoomSession session, Rectangle area)
    {
        _session = session;
        _area = area;
        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    /// <summary>Monitores disponiveis pra escolher qual compartilhar.</summary>
    public static List<(string Name, Rectangle Bounds)> ListScreens()
    {
        var list = new List<(string, Rectangle)>();
        for (int i = 0; i < Screen.AllScreens.Length; i++)
        {
            var s = Screen.AllScreens[i];
            string name = $"Monitor {i + 1} ({s.Bounds.Width}x{s.Bounds.Height})"
                        + (s.Primary ? " — principal" : "");
            list.Add((name, s.Bounds));
        }
        return list;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "primicord-screen" };
        _thread.Start();
        Log.Write($"tela: compartilhando {_area.Width}x{_area.Height}");
    }

    public void Dispose()
    {
        _running = false;
        try { _thread?.Join(600); } catch { }
        _thread = null;
        try { _grab?.Dispose(); } catch { }
        try { _scaled?.Dispose(); } catch { }
        _grab = _scaled = null;
        Log.Write("tela: parou de compartilhar");
    }

    private void Loop()
    {
        int outW = _area.Width, outH = _area.Height;
        if (outW > MaxWidth)
        {
            outH = (int)Math.Round(outH * (MaxWidth / (double)outW));
            outW = MaxWidth;
        }
        outW -= outW % 2; outH -= outH % 2;

        _grab = new Bitmap(_area.Width, _area.Height, PixelFormat.Format32bppArgb);
        _scaled = new Bitmap(outW, outH, PixelFormat.Format24bppRgb);

        using var gGrab = Graphics.FromImage(_grab);
        using var gScale = Graphics.FromImage(_scaled);
        gScale.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        gScale.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;

        var ms = new MemoryStream(256 * 1024);
        int frameMs = 1000 / TargetFps;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastStatTicks = Environment.TickCount64;
        int framesThisSecond = 0, bytesThisSecond = 0;

        while (_running)
        {
            long t0 = sw.ElapsedMilliseconds;
            try
            {
                gGrab.CopyFromScreen(_area.X, _area.Y, 0, 0, _area.Size, CopyPixelOperation.SourceCopy);
                DrawCursor(gGrab, _area);
                gScale.DrawImage(_grab, 0, 0, outW, outH);

                ms.SetLength(0);
                using (var ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(Encoder.Quality, _quality);
                    _scaled.Save(ms, _jpegCodec, ep);
                }
                int len = (int)ms.Length;
                byte[] jpeg = ms.GetBuffer();

                var copy = new byte[len];
                Buffer.BlockCopy(jpeg, 0, copy, 0, len);
                LastFrame = copy; LastWidth = outW; LastHeight = outH;
                FrameProduced?.Invoke(copy, outW, outH);

                _session.SendScreenFrame(copy, len, outW, outH);

                framesThisSecond++;
                bytesThisSecond += len;

                // Qualidade adaptativa PELO NUMERO DE ESPECTADORES. Como e malha, o
                // mesmo quadro sobe uma vez por pessoa: medido, 47KB/quadro a 10fps
                // da 3,7 Mbps por espectador — com 4 assistindo seriam ~15 Mbps de
                // subida, mais do que muita casa aguenta. Entao o orcamento total
                // fica fixo (~90KB por ciclo) e e dividido entre quem esta olhando.
                int viewers = Math.Max(1, _session.Peers.Count(p => p.Locked != null));
                int targetBytes = Math.Clamp(90_000 / viewers, 18_000, 55_000);
                if (len > targetBytes * 1.25 && _quality > 20) _quality -= 5;
                else if (len < targetBytes * 0.7 && _quality < 70) _quality += 5;
            }
            catch (Exception ex)
            {
                Log.Write("captura de tela falhou: " + ex.Message);
                Thread.Sleep(500);
            }

            if (Environment.TickCount64 - lastStatTicks >= 1000)
            {
                Fps = framesThisSecond;
                KbPerSecond = bytesThisSecond / 1024;
                framesThisSecond = 0; bytesThisSecond = 0;
                lastStatTicks = Environment.TickCount64;
            }

            int elapsed = (int)(sw.ElapsedMilliseconds - t0);
            int wait = frameMs - elapsed;
            if (wait > 0) Thread.Sleep(wait);
        }
        ms.Dispose();
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

    private static void DrawCursor(Graphics g, Rectangle area)
    {
        try
        {
            var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
            if (!GetCursorInfo(ref ci) || (ci.flags & CursorShowing) == 0) return;
            if (!area.Contains(ci.ptScreenPos)) return;
            IntPtr hdc = g.GetHdc();
            try { DrawIcon(hdc, ci.ptScreenPos.X - area.X, ci.ptScreenPos.Y - area.Y, ci.hCursor); }
            finally { g.ReleaseHdc(hdc); }
        }
        catch { /* cursor e enfeite; nunca derruba a captura */ }
    }
}

/// <summary>
/// Guarda o ultimo quadro recebido de cada pessoa que esta compartilhando tela.
/// </summary>
public sealed class ScreenReceiver : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<uint, Bitmap> _frames = new();
    private readonly Dictionary<uint, byte[]> _rawFrames = new();
    private readonly Dictionary<uint, long> _lastTicks = new();

    public event Action<uint>? FrameUpdated;

    public void OnFrame(uint senderId, byte[] jpeg, int w, int h)
    {
        Bitmap bmp;
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms);
            bmp = new Bitmap(img);   // copia: o stream nao pode continuar vivo
        }
        catch { return; }   // quadro corrompido (pedaco perdido) — ignora

        lock (_lock)
        {
            if (_frames.TryGetValue(senderId, out var old)) { try { old.Dispose(); } catch { } }
            _frames[senderId] = bmp;
            _rawFrames[senderId] = jpeg;
            _lastTicks[senderId] = DateTime.UtcNow.Ticks;
        }
        FrameUpdated?.Invoke(senderId);
    }

    /// <summary>Ultimo quadro de alguem, ou null se parou de mandar ha mais de 3s.</summary>
    public Bitmap? FrameOf(uint senderId)
    {
        lock (_lock)
        {
            if (!_frames.TryGetValue(senderId, out var b)) return null;
            if (DateTime.UtcNow.Ticks - _lastTicks[senderId] > TimeSpan.TicksPerSecond * 3) return null;
            return b;
        }
    }

    /// <summary>JPEG cru do ultimo quadro (o gravador de clipe usa direto).</summary>
    public byte[]? RawFrameOf(uint senderId)
    {
        lock (_lock) return _rawFrames.TryGetValue(senderId, out var b) ? b : null;
    }

    public bool IsSharing(uint senderId) => FrameOf(senderId) != null;

    public void Remove(uint senderId)
    {
        lock (_lock)
        {
            if (_frames.TryGetValue(senderId, out var b)) { try { b.Dispose(); } catch { } }
            _frames.Remove(senderId);
            _rawFrames.Remove(senderId);
            _lastTicks.Remove(senderId);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var b in _frames.Values) { try { b.Dispose(); } catch { } }
            _frames.Clear();
            _rawFrames.Clear();
            _lastTicks.Clear();
        }
    }
}
