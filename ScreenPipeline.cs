using System.Diagnostics;

namespace Primicord;

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
