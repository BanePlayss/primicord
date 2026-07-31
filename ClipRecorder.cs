namespace Primicord;

/// <summary>
/// Buffer rolante dos ultimos segundos de tela + som, pra salvar clipe DEPOIS que
/// a coisa engraçada ja aconteceu (estilo Shadowplay).
/// </summary>
/// <remarks>
/// Guarda em memoria os quadros JPEG que estao passando e dois fluxos de audio:
/// o que EU ouço (vozes dos outros + musica do DJ) e o meu proprio microfone.
/// Sao somados na hora de salvar — se eu misturasse na entrada, um pico do mic
/// estouraria o audio dos outros e nao teria como desfazer.
///
/// Memoria: 60s a 10fps com quadro de ~45KB da ~27MB de video + ~11MB de audio.
/// </remarks>
public sealed class ClipRecorder : IDisposable
{
    private const int SampleRate = 48000;
    private const int BytesPerSample = 2;

    private readonly int _windowMs;
    private readonly object _lock = new();

    private readonly LinkedList<(long At, byte[] Jpeg, int W, int H)> _frames = new();
    private readonly LinkedList<(long At, byte[] Pcm)> _heard = new();
    private readonly LinkedList<(long At, byte[] Pcm)> _mic = new();

    private long _bytesHeld;

    public bool Active { get; private set; }
    public int BufferSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_frames.Count == 0) return 0;
                return (int)((Now - _frames.First!.Value.At) / 1000);
            }
        }
    }
    public int BufferMegabytes { get { lock (_lock) return (int)(_bytesHeld / (1024 * 1024)); } }

    private static long Now => Environment.TickCount64;

    public ClipRecorder(int windowSeconds = 60) => _windowMs = windowSeconds * 1000;

    public void Start()
    {
        lock (_lock) { Active = true; }
        Log.Write($"clipe: buffer rolante ligado ({_windowMs / 1000}s)");
    }

    public void Stop()
    {
        lock (_lock)
        {
            Active = false;
            _frames.Clear(); _heard.Clear(); _mic.Clear();
            _bytesHeld = 0;
        }
        Log.Write("clipe: buffer rolante desligado");
    }

    public void PushFrame(byte[] jpeg, int w, int h)
    {
        lock (_lock)
        {
            if (!Active) return;
            _frames.AddLast((Now, jpeg, w, h));
            _bytesHeld += jpeg.Length;
            Trim();
        }
    }

    public void PushHeard(byte[] pcm, int offset, int count) => PushAudio(_heard, pcm, offset, count);
    public void PushMic(byte[] pcm, int offset, int count) => PushAudio(_mic, pcm, offset, count);

    private void PushAudio(LinkedList<(long At, byte[] Pcm)> list, byte[] pcm, int offset, int count)
    {
        if (count <= 0) return;
        lock (_lock)
        {
            if (!Active) return;
            var copy = new byte[count];
            Buffer.BlockCopy(pcm, offset, copy, 0, count);
            list.AddLast((Now, copy));
            _bytesHeld += count;
            Trim();
        }
    }

    /// <summary>Descarta o que ja saiu da janela (chamado com o lock preso).</summary>
    private void Trim()
    {
        long cutoff = Now - _windowMs;
        while (_frames.First != null && _frames.First.Value.At < cutoff)
        {
            _bytesHeld -= _frames.First.Value.Jpeg.Length;
            _frames.RemoveFirst();
        }
        TrimAudio(_heard, cutoff);
        TrimAudio(_mic, cutoff);
    }

    private void TrimAudio(LinkedList<(long At, byte[] Pcm)> list, long cutoff)
    {
        while (list.First != null && list.First.Value.At < cutoff)
        {
            _bytesHeld -= list.First.Value.Pcm.Length;
            list.RemoveFirst();
        }
    }

    /// <summary>
    /// Salva os ultimos <paramref name="seconds"/> segundos em AVI. Devolve o caminho,
    /// ou null se o buffer ainda nao tem quadro nenhum.
    /// </summary>
    public string? SaveClip(int seconds, string roomName)
    {
        List<(long At, byte[] Jpeg, int W, int H)> frames;
        List<(long At, byte[] Pcm)> heard, mic;
        long from, to;

        lock (_lock)
        {
            if (_frames.Count == 0) return null;
            to = Now;
            from = Math.Max(_frames.First!.Value.At, to - seconds * 1000L);
            frames = _frames.Where(f => f.At >= from).ToList();
            heard = _heard.Where(a => a.At >= from).ToList();
            mic = _mic.Where(a => a.At >= from).ToList();
        }
        if (frames.Count == 0) return null;

        int w = frames[0].W, h = frames[0].H;
        // So entram quadros do mesmo tamanho — trocar de monitor no meio mudaria a
        // resolucao e o AVI tem uma so por arquivo.
        frames = frames.Where(f => f.W == w && f.H == h).ToList();
        if (frames.Count == 0) return null;

        int spanMs = (int)Math.Max(1000, to - from);
        int fps = Math.Max(1, (int)Math.Round(frames.Count * 1000.0 / spanMs));

        string safeRoom = new(roomName.ToLowerInvariant()
            .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
        if (safeRoom.Length == 0) safeRoom = "sala";
        string path = Path.Combine(AppEnv.ClipsDir,
            $"primicord-{safeRoom}-{DateTime.Now:yyyyMMdd-HHmmss}.avi");

        byte[] audio = MixAudio(heard, mic, from, spanMs);

        try
        {
            using var avi = new AviWriter(path, w, h, fps);
            // Intercala: cada quadro leva junto a fatia de audio daquele intervalo.
            int audioPos = 0;
            int bytesPerFrame = (SampleRate * BytesPerSample) / fps;
            bytesPerFrame -= bytesPerFrame % BytesPerSample;

            foreach (var f in frames)
            {
                avi.WriteVideoFrame(f.Jpeg, f.Jpeg.Length);
                int take = Math.Min(bytesPerFrame, audio.Length - audioPos);
                if (take > 0) { avi.WriteAudio(audio, audioPos, take); audioPos += take; }
            }
            if (audioPos < audio.Length) avi.WriteAudio(audio, audioPos, audio.Length - audioPos);
            avi.Finish();
        }
        catch (Exception ex)
        {
            Log.Write("salvar clipe falhou: " + ex);
            return null;
        }

        Log.Write($"clipe salvo: {path} ({frames.Count} quadros @ {fps}fps)");
        return path;
    }

    /// <summary>
    /// Junta os dois fluxos numa trilha so, alinhando pelo horario de chegada e
    /// somando com saturacao (sem estourar o 16-bit).
    /// </summary>
    private static byte[] MixAudio(List<(long At, byte[] Pcm)> heard, List<(long At, byte[] Pcm)> mic,
                                    long from, int spanMs)
    {
        int totalSamples = (int)((long)spanMs * SampleRate / 1000);
        var acc = new int[totalSamples];

        void Lay(List<(long At, byte[] Pcm)> src)
        {
            foreach (var (at, pcm) in src)
            {
                int startSample = (int)((at - from) * SampleRate / 1000);
                int n = pcm.Length / BytesPerSample;
                for (int i = 0; i < n; i++)
                {
                    int idx = startSample + i;
                    if (idx < 0 || idx >= totalSamples) continue;
                    acc[idx] += (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
                }
            }
        }
        Lay(heard);
        Lay(mic);

        var outBytes = new byte[totalSamples * BytesPerSample];
        for (int i = 0; i < totalSamples; i++)
        {
            int v = Math.Clamp(acc[i], short.MinValue, short.MaxValue);
            outBytes[i * 2] = (byte)v;
            outBytes[i * 2 + 1] = (byte)(v >> 8);
        }
        return outBytes;
    }

    public void Dispose() => Stop();
}
