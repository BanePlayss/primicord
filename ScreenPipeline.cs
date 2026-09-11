using System.Diagnostics;
using System.Drawing.Imaging;

namespace Primicord;

/// <summary>Clip encoding cannot stall live capture; at most one frame waits.</summary>
internal sealed class ScreenClipEncoder : IDisposable
{
    private readonly LatestScreenFrame<Bitmap> _frames = new();
    private readonly AutoResetEvent _ready = new(false);
    private readonly Action<byte[], int, int> _produced;
    private readonly Thread _worker;
    private volatile bool _running = true;
    private int _disposed;

    public ScreenClipEncoder(Action<byte[], int, int> produced)
    {
        _produced = produced;
        _worker = new Thread(Run) { IsBackground = true, Name = "primicord-screen-clip" };
        _worker.Start();
    }

    public void Publish(Bitmap frame)
    {
        _frames.Publish(frame);
        _ready.Set();
    }

    private void Run()
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 70L);
        using var output = new MemoryStream(512 * 1024);
        try
        {
            while (_running)
            {
                _ready.WaitOne(100);
                using var frame = _frames.Take();
                if (frame == null || !_running) continue;
                try
                {
                    output.SetLength(0);
                    frame.Save(output, codec, parameters);
                    if (_running) _produced(output.ToArray(), frame.Width, frame.Height);
                }
                catch (Exception ex) { Log.Write("screen-clip: " + ex.Message); }
            }
        }
        finally { _frames.Dispose(); _ready.Dispose(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _running = false;
        _ready.Set();
        _worker.Join();
    }
}

/// <summary>A single pending item: a slow consumer never accumulates stale video.</summary>
internal sealed class LatestScreenFrame<T> : IDisposable where T : class, IDisposable
{
    private readonly object _gate = new();
    private T? _pending;
    private bool _disposed;
    public long Dropped { get; private set; }

    public void Publish(T value)
    {
        T? old;
        lock (_gate)
        {
            if (_disposed) { old = value; }
            else
            {
                old = _pending;
                _pending = value;
                if (old != null) Dropped++;
            }
        }
        old?.Dispose();
    }

    public T? Take()
    {
        lock (_gate)
        {
            var value = _pending;
            _pending = null;
            return value;
        }
    }

    public void Dispose()
    {
        T? old;
        lock (_gate) { _disposed = true; old = _pending; _pending = null; }
        old?.Dispose();
    }
}

/// <summary>Accounts for all recipients and packet headers, with bounded bursts.</summary>
internal sealed class ScreenUploadBudget
{
    private double _tokens;
    private double _lastSeconds;
    private bool _started;

    public int Available(int bytesPerSecond, int viewers, double nowSeconds)
    {
        bytesPerSecond = Math.Clamp(bytesPerSecond, 100_000, 100_000_000);
        double capacity = Math.Max(48_000, bytesPerSecond * .15);
        if (!_started) { _started = true; _tokens = capacity; }
        else _tokens = Math.Min(capacity, _tokens + Math.Max(0, nowSeconds - _lastSeconds) * bytesPerSecond);
        _lastSeconds = nowSeconds;
        return (int)Math.Min(48_000, _tokens / Math.Max(1, viewers) / 1.025);
    }

    public void Spend(int payloadBytes, int viewers)
    {
        // RoomSession uses 1,181-byte chunks and 19 bytes of protocol headers;
        // include IPv4 + UDP too. A conservative estimate is preferable here.
        int packets = (payloadBytes + 1180) / 1181;
        _tokens = Math.Max(0, _tokens - (payloadBytes + packets * 47L) * Math.Max(1, viewers));
    }
}

internal static class ScreenPacing
{
    /// <summary>
    /// Espera com precisão de sub-milisegundo. Thread.Sleep(16) sozinho pode ser
    /// arredondado pelo timer do Windows e fazer um alvo de 60 FPS cair para ~30.
    /// Dormimos quase todo o intervalo e queimamos apenas o último milissegundo.
    /// </summary>
    public static void Wait(long cycleStart, int fps)
    {
        double frameMs = 1000d / Math.Clamp(fps, 30, 60);
        while (true)
        {
            double remaining = frameMs - Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
            if (remaining <= 0) return;
            if (remaining > 2.0)
            {
                Thread.Sleep(Math.Max(1, (int)Math.Floor(remaining - 1.0)));
                continue;
            }
            Thread.SpinWait(2000);
        }
    }
}
