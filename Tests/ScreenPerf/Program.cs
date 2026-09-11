using System.Diagnostics;
using System.Net;
using Primicord;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        VerifyTileViews();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        using var form = new MotionScene();
        form.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(500);
                var bounds = Screen.FromControl(form).Bounds;
                await Task.Run(async () =>
                {
                    using var tx = new RoomSession(new Firestore(), "perf-local", "perf-tx", "test");
                    using var rx = new RoomSession(new Firestore(), "perf-local", "perf-rx", "test");
                    using var receiver = new ScreenReceiver();
                    await tx.StartNetworkOnlyAsync(false);
                    await rx.StartNetworkOnlyAsync(false);
                    tx.AddPeerDirect("perf-rx", "test", [new IPEndPoint(IPAddress.Loopback, rx.LocalPort)]);
                    rx.AddPeerDirect("perf-tx", "test", [new IPEndPoint(IPAddress.Loopback, tx.LocalPort)]);
                    await Task.Delay(500);
                    long received = 0;
                    var touched = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), byte>();
                    rx.ScreenFrameReceived += (id, data, w, h) =>
                    {
                        if (receiver.OnUpdate(id, data, w, h))
                        {
                            Interlocked.Increment(ref received);
                            int pos = 8, count = data[1] | data[2] << 8;
                            for (int i = 0; i < count && pos + 4 <= data.Length; i++)
                            {
                                touched[(data[pos], data[pos + 1])] = 1;
                                pos += 4 + (data[pos + 2] | data[pos + 3] << 8);
                            }
                        }
                    };
                    foreach (bool clip in new[] { false, true })
                    {
                        using var recorder = new ClipRecorder();
                        if (clip) recorder.Start();
                        using var sender = new ScreenSender(tx, new CaptureTarget { Name = "perf-motion", MonitorBounds = bounds })
                        {
                            TargetFps = 60, MaxQuality = true, TotalUploadBudget = 4_096_000,
                            NeedFullFrames = clip
                        };
                        long previews = 0;
                        sender.PreviewFrameProduced += bmp => { bmp.Dispose(); Interlocked.Increment(ref previews); };
                        sender.FullFrameProduced += recorder.PushFrame;
                        sender.Start();
                        await Task.Delay(1500);
                        long start = Stopwatch.GetTimestamp();
                        touched.Clear();
                        long r0 = Interlocked.Read(ref received), p0 = Interlocked.Read(ref previews);
                        for (int second = 0; second < 6; second++)
                        {
                            await Task.Delay(1000);
                            Console.WriteLine($"clip={clip} fps={sender.Fps} avg_ms_cap_copy_compare_encode_send={sender.TimingSummary} tiles={sender.TilesLastFrame} kb={sender.KbPerSecond} backend={sender.CaptureBackend}");
                        }
                        double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                        int expectedTiles = (bounds.Width + 127) / 128 * ((bounds.Height + 127) / 128);
                        Console.WriteLine($"RESULT clip={clip} received_updates_per_second={(Interlocked.Read(ref received)-r0)/seconds:F1} preview_fps={(Interlocked.Read(ref previews)-p0)/seconds:F1} regions={touched.Count}/{expectedTiles} error={sender.LastError ?? "none"}");
                        if (touched.Count != expectedTiles || sender.LastError != null)
                            throw new InvalidOperationException("Incomplete screen coverage or capture error.");
                    }
                });
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static void VerifyTileViews()
    {
        // A view must honor its offset and full-image stride, including edge
        // tiles. A wrong stride silently produces repeated/corrupted pixels.
        const int width = 262, height = 134, stride = width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(stride * height, pinned: true);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int p = y * stride + x * 4;
            pixels[p] = (byte)x; pixels[p + 1] = (byte)y; pixels[p + 2] = (byte)(x + y); pixels[p + 3] = 255;
        }
        var create = typeof(ScreenSender).GetMethod("TileView", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        // Baseline releases used DrawImage and have no view helper.
        if (create == null) return;
        foreach (var area in new[] { new Rectangle(128, 0, 128, 128), new Rectangle(256, 128, 6, 6) })
        {
            using var tile = (Bitmap)create.Invoke(null, [pixels, stride, area.X, area.Y, area.Width, area.Height])!;
            using var data = new MemoryStream();
            tile.Save(data, System.Drawing.Imaging.ImageFormat.Png);
            data.Position = 0;
            using var decoded = new Bitmap(data);
            for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
            {
                var actual = decoded.GetPixel(x, y);
                var expected = Color.FromArgb((byte)(x + area.X + y + area.Y), (byte)(y + area.Y), (byte)(x + area.X));
                if (actual.ToArgb() != expected.ToArgb()) throw new InvalidOperationException("Tile pixels changed.");
            }
        }
        Console.WriteLine("PASS: PNG tile views preserve every pixel, including edge tiles and row stride.");
    }
}

internal sealed class MotionScene : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private int _tick;
    public MotionScene()
    {
        Text = "Primicord — teste local de transmissão (fecha automaticamente)";
        DoubleBuffered = true;
        Bounds = Screen.PrimaryScreen!.Bounds;
        FormBorderStyle = FormBorderStyle.None;
        _timer.Tick += (_, _) => { _tick++; Invalidate(); };
        _timer.Start();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Color.FromArgb(24, 25, 30));
        for (int y = 0; y < Height; y += 48)
        for (int x = -48; x < Width; x += 48)
        {
            using var brush = new SolidBrush(Color.FromArgb(70 + (x + 48 + y) % 170, 50 + y % 190, 80 + (x + 48) % 150));
            e.Graphics.FillRectangle(brush, x + _tick * 5 % 48, y, 35, 35);
        }
        e.Graphics.FillRectangle(Brushes.Black, 20, 20, 620, 90);
        e.Graphics.DrawString($"Teste local Primicord — movimento 1080p\nQuadro da fonte: {_tick}. Nenhuma imagem é salva.", Font, Brushes.White, 35, 40);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
