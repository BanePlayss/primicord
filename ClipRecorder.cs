namespace Primicord;

public enum ClipSaveStatus { Saved, Busy, NoFrames, Disabled, Cancelled, Failed }

public sealed record ClipSaveResult(ClipSaveStatus Status, string? Path = null, string? Error = null,
                                    double DurationSeconds = 0, int Frames = 0, int Fps = 0)
{
    public bool Success => Status == ClipSaveStatus.Saved;
}

/// <summary>
/// Opt-in, bounded replay buffer. Snapshotting is brief; audio mixing and disk I/O
/// run on one background worker. At most one export can retain a second buffer.
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    private const int SampleRate = 48000, BytesPerSample = 2;
    private readonly int _windowMs, _captureFps;
    private readonly long _maxBytes;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly LinkedList<(long At, byte[] Jpeg, int W, int H)> _frames = new();
    private readonly LinkedList<(long At, byte[] Pcm)> _heard = new(), _mic = new();
    private long _bytesHeld;
    private double _nextFrameAt;
    private bool _active, _disposed;
    private int _saving;
    private static long Now => Environment.TickCount64;

    public bool Active { get { lock (_lock) return _active; } }
    public bool IsSaving => Volatile.Read(ref _saving) != 0;
    public int CaptureFps => _captureFps;
    public long MaxBufferBytes => _maxBytes;
    /// <summary>Allows the caller to skip an otherwise unnecessary JPEG encode.</summary>
    public bool WantsFrame { get { lock (_lock) return _active && Now >= _nextFrameAt; } }
    public int BufferSeconds
    {
        get
        {
            lock (_lock)
            {
                Trim();
                return _frames.First is { } first ? (int)((Now - first.Value.At) / 1000) : 0;
            }
        }
    }
    public int BufferMegabytes { get { lock (_lock) { Trim(); return (int)(_bytesHeld / (1024 * 1024)); } } }

    public ClipRecorder(int windowSeconds = 60, int maxBufferMegabytes = 96, int captureFps = 30)
    {
        _windowMs = Math.Clamp(windowSeconds, 1, 120) * 1000;
        _maxBytes = Math.Clamp(maxBufferMegabytes, 8, 256) * 1024L * 1024;
        _captureFps = Math.Clamp(captureFps, 1, 60);
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _active) return;
            _active = true;
            _nextFrameAt = 0;
        }
        Log.Write($"clipe: buffer ligado ({_windowMs / 1000}s, teto {_maxBytes / 1048576} MiB, {_captureFps}fps)");
    }

    public void Stop()
    {
        lock (_lock)
        {
            _active = false;
            _frames.Clear(); _heard.Clear(); _mic.Clear();
            _bytesHeld = 0;
            _nextFrameAt = 0;
        }
    }

    public void PushFrame(byte[] jpeg, int w, int h)
    {
        if (jpeg.Length == 0 || jpeg.Length > 8 * 1024 * 1024 || w <= 0 || h <= 0 || w > 8192 || h > 8192) return;
        lock (_lock)
        {
            long at = Now;
            if (!_active || at < _nextFrameAt) return;
            if (_frames.Last is { } last && (last.Value.W != w || last.Value.H != h))
            {
                foreach (var f in _frames) _bytesHeld -= f.Jpeg.Length;
                _frames.Clear();
            }
            _nextFrameAt = _nextFrameAt == 0 || at - _nextFrameAt > 1000
                ? at + 1000.0 / _captureFps : _nextFrameAt + 1000.0 / _captureFps;
            // Own our memory: capture/receiver may recycle their input after returning.
            _frames.AddLast((at, (byte[])jpeg.Clone(), w, h));
            _bytesHeld += jpeg.Length;
            Trim();
        }
    }

    /// <summary>Inputs are mono signed 16-bit little-endian PCM at 48 kHz.</summary>
    public void PushHeard(byte[] pcm, int offset, int count) => PushAudio(_heard, pcm, offset, count);
    public void PushMic(byte[] pcm, int offset, int count) => PushAudio(_mic, pcm, offset, count);

    private void PushAudio(LinkedList<(long At, byte[] Pcm)> list, byte[] pcm, int offset, int count)
    {
        if (count <= 0 || count > 1024 * 1024 || offset < 0 || offset > pcm.Length - count) return;
        count -= count % BytesPerSample;
        if (count == 0) return;
        lock (_lock)
        {
            if (!_active) return;
            var copy = new byte[count];
            Buffer.BlockCopy(pcm, offset, copy, 0, count);
            long at = Now - count * 1000L / (SampleRate * BytesPerSample);
            list.AddLast((at, copy));
            _bytesHeld += count;
            Trim();
        }
    }

    private void Trim()
    {
        long cutoff = Now - _windowMs;
        while (_frames.First is { } f && f.Value.At < cutoff) DropFrame();
        TrimAudio(_heard, cutoff); TrimAudio(_mic, cutoff);
        while (_bytesHeld > _maxBytes || _frames.Count + _heard.Count + _mic.Count > 30000)
        {
            long videoAt = _frames.First?.Value.At ?? long.MaxValue;
            long heardAt = _heard.First?.Value.At ?? long.MaxValue;
            long micAt = _mic.First?.Value.At ?? long.MaxValue;
            if (videoAt <= heardAt && videoAt <= micAt) DropFrame();
            else DropAudio(heardAt <= micAt ? _heard : _mic);
        }
    }

    private void DropFrame()
    {
        if (_frames.First is not { } f) return;
        _bytesHeld -= f.Value.Jpeg.Length; _frames.RemoveFirst();
    }
    private void DropAudio(LinkedList<(long At, byte[] Pcm)> list)
    {
        if (list.First is not { } a) return;
        _bytesHeld -= a.Value.Pcm.Length; list.RemoveFirst();
    }
    private void TrimAudio(LinkedList<(long At, byte[] Pcm)> list, long cutoff)
    {
        while (list.First is { } a && a.Value.At + a.Value.Pcm.Length * 1000L / 96000 < cutoff) DropAudio(list);
    }

    /// <summary>Returns Busy immediately instead of queueing repeated hotkeys. Does not block the UI.</summary>
    public Task<ClipSaveResult> SaveClipAsync(int seconds, string roomName, CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _saving, 1, 0) != 0)
            return Task.FromResult(new ClipSaveResult(ClipSaveStatus.Busy));
        lock (_lock)
        {
            if (_disposed || !_active) return Reject(ClipSaveStatus.Disabled);
            if (ct.IsCancellationRequested) return Reject(ClipSaveStatus.Cancelled);
            Trim();
            if (_frames.Count == 0) return Reject(ClipSaveStatus.NoFrames);
            long to = Now;
            long from = Math.Max(_frames.First!.Value.At, to - Math.Clamp(seconds, 1, _windowMs / 1000) * 1000L);
            var frames = _frames.Where(f => f.At >= from).ToList();
            var preceding = _frames.LastOrDefault(f => f.At <= from);
            if (preceding.Jpeg != null && (frames.Count == 0 || frames[0].At != preceding.At)) frames.Insert(0, preceding);
            if (frames.Count == 0) return Reject(ClipSaveStatus.NoFrames);
            var heard = _heard.Where(a => a.At + a.Pcm.Length * 1000L / 96000 >= from).ToList();
            var mic = _mic.Where(a => a.At + a.Pcm.Length * 1000L / 96000 >= from).ToList();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
            return Task.Run(() =>
            {
                try { return WriteSnapshot(frames, heard, mic, from, to, roomName, linked.Token); }
                finally { linked.Dispose(); Volatile.Write(ref _saving, 0); }
            });
        }
    }

    private Task<ClipSaveResult> Reject(ClipSaveStatus status)
    {
        Volatile.Write(ref _saving, 0);
        return Task.FromResult(new ClipSaveResult(status));
    }

    /// <summary>Legacy blocking API. UI callers must use SaveClipAsync.</summary>
    public string? SaveClip(int seconds, string roomName) => SaveClipAsync(seconds, roomName).GetAwaiter().GetResult().Path;

    private ClipSaveResult WriteSnapshot(List<(long At, byte[] Jpeg, int W, int H)> frames,
        List<(long At, byte[] Pcm)> heard, List<(long At, byte[] Pcm)> mic,
        long from, long to, string roomName, CancellationToken ct)
    {
        string? pendingPath = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            int fps = _captureFps;
            int outputFrames = Math.Max(1, (int)Math.Ceiling((to - from) * fps / 1000.0));
            int spanMs = (int)Math.Ceiling(outputFrames * 1000.0 / fps);
            byte[] audio = MixAudio(heard, mic, from, spanMs, ct);
            string safeRoom = new((roomName ?? "sala").ToLowerInvariant()
                .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').Take(48).ToArray());
            if (safeRoom.Length == 0) safeRoom = "sala";
            string path = Path.Combine(AppEnv.ClipsDir,
                $"primicord-{safeRoom}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}.avi");
            pendingPath = path + ".partial";
            using (var avi = new AviWriter(pendingPath, frames[0].W, frames[0].H, fps))
            {
                int source = 0, audioPos = 0;
                for (int i = 0; i < outputFrames; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    long targetAt = from + i * 1000L / fps;
                    while (source + 1 < frames.Count && frames[source + 1].At <= targetAt) source++;
                    // Hold missing frames at their timestamp instead of accelerating video.
                    var frame = frames[source];
                    avi.WriteVideoFrame(frame.Jpeg, frame.Jpeg.Length);
                    int audioEnd = (int)Math.Min(audio.Length, (i + 1L) * SampleRate / fps * BytesPerSample);
                    if (audioEnd > audioPos) avi.WriteAudio(audio, audioPos, audioEnd - audioPos);
                    audioPos = audioEnd;
                }
                avi.Finish();
            }
            ct.ThrowIfCancellationRequested();
            File.Move(pendingPath, path);
            pendingPath = null;
            Log.Write($"clipe salvo: {path} ({outputFrames} quadros @ {fps}fps)");
            return new(ClipSaveStatus.Saved, path, DurationSeconds: outputFrames / (double)fps, Frames: outputFrames, Fps: fps);
        }
        catch (OperationCanceledException) { return new(ClipSaveStatus.Cancelled); }
        catch (Exception ex)
        {
            Log.Write("salvar clipe falhou: " + ex);
            return new(ClipSaveStatus.Failed, Error: ex.Message);
        }
        finally
        {
            if (pendingPath != null) try { File.Delete(pendingPath); } catch { }
        }
    }

    private static byte[] MixAudio(List<(long At, byte[] Pcm)> heard, List<(long At, byte[] Pcm)> mic,
                                    long from, int spanMs, CancellationToken ct)
    {
        int totalSamples = (int)((long)spanMs * SampleRate / 1000);
        var acc = new int[totalSamples];
        void Lay(List<(long At, byte[] Pcm)> src)
        {
            foreach (var (at, pcm) in src)
            {
                ct.ThrowIfCancellationRequested();
                int startSample = (int)((at - from) * SampleRate / 1000);
                for (int i = Math.Max(0, -startSample); i < pcm.Length / BytesPerSample && startSample + i < totalSamples; i++)
                    acc[startSample + i] += (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            }
        }
        Lay(heard); Lay(mic);
        var output = new byte[totalSamples * BytesPerSample];
        for (int i = 0; i < totalSamples; i++)
        {
            int v = Math.Clamp(acc[i], short.MinValue, short.MaxValue);
            output[i * 2] = (byte)v; output[i * 2 + 1] = (byte)(v >> 8);
        }
        return output;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _shutdown.Cancel();
            _shutdown.Dispose();
        }
    }
}
