using NAudio.Wave;

namespace Primicord;

public sealed class ProcessLoopbackUnavailableException : Exception
{
    public ProcessLoopbackUnavailableException(string message, Exception inner)
        : base(message, inner) { }
}

/// <summary>
/// Modo DJ: transmite o audio do SISTEMA (Spotify, YouTube, o que estiver tocando)
/// pra todo mundo da sala.
/// </summary>
/// <remarks>
/// POR QUE NAO E UMA "JAM DO SPOTIFY": o Jam nao tem API publica — nao da pra criar
/// nem entrar numa sessao por programa. Entao em vez de sincronizar players, um cara
/// vira DJ e manda o proprio audio. Vantagem: funciona com QUALQUER fonte (Spotify,
/// YouTube, SoundCloud) e ninguem precisa de Premium. Desvantagem: e uma transmissao,
/// entao cada um ouve com o atraso da rede e nao controla o player (so o DJ).
/// O nome da faixa vai junto, lido do Windows (ver NowPlaying).
///
/// ECO: usa ProcessLoopbackCapture EXCLUINDO o proprio processo, senao o audio das
/// vozes que o Primicord esta tocando seria recapturado e devolvido pra sala — todo
/// mundo se ouviria com atraso. Se o Windows nao oferecer o isolamento, o audio da
/// tela fica desligado em vez de cair no loopback comum e criar eco.
/// </remarks>
public sealed class MusicShare : IDisposable
{
    private const int FrameBytes = 960;   // 10ms @ 48k mono 16-bit (igual a voz)

    private readonly ISharedAudioTransport _transport;
    private readonly uint? _targetProcessId;
    private IWaveIn? _capture;
    private readonly FrameAccumulator _acc = new();
    private readonly byte[] _frame = new byte[FrameBytes];
    private float[] _floatBuf = Array.Empty<float>();
    private byte[] _pcmBuf = Array.Empty<byte>();

    /// <summary>Compatibilidade de UI; desde 0.6.16 nunca habilitamos captura com eco.</summary>
    public bool EchoRisk { get; private set; }

    public bool Running { get; private set; }
    public float Peak { get; private set; }

    /// <param name="targetProcessId">
    /// Processo da janela compartilhada. Quando existe, capturamos somente esse
    /// processo e seus filhos; para monitor inteiro capturamos tudo menos o proprio
    /// Primicord, evitando devolver as vozes para a sala.
    /// </param>
    public MusicShare(ISharedAudioTransport transport, uint? targetProcessId = null)
    {
        _transport = transport;
        _targetProcessId = targetProcessId is > 0 ? targetProcessId : null;
    }

    public void Start()
    {
        if (Running) return;

        try
        {
            var proc = _targetProcessId is uint processId
                ? ProcessLoopbackCapture.IncludingProcess(processId)
                : ProcessLoopbackCapture.ExcludingSelf();
            // Pedimos o formato: o engine converte. Evita o problema de saida 7.1
            // entregar 8 canais (o fone do Lucas faz isso).
            proc.WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
            proc.Prepare();
            _capture = proc;
            EchoRisk = false;
            Log.Write(_targetProcessId is uint id
                ? $"audio compartilhado: processo {id} e filhos (sem eco)"
                : "audio compartilhado: sistema menos Primicord (sem eco)");
        }
        catch (Exception ex)
        {
            Log.Write("DJ: process loopback indisponivel; audio bloqueado para evitar eco: "
                    + ex.Message);
            throw new ProcessLoopbackUnavailableException(
                "O audio da tela foi desligado porque o Windows nao conseguiu isolar "
              + "as vozes do Primicord.", ex);
        }

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
        Running = true;
    }

    public void Dispose()
    {
        Running = false;
        if (_capture != null)
        {
            try { _capture.DataAvailable -= OnData; } catch { }
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        _acc.Reset();
        Peak = 0;
        Log.Write("DJ: parou");
    }

    private void OnData(object? sender, WaveInEventArgs a)
    {
        if (!Running || a.BytesRecorded <= 0) return;
        var fmt = _capture?.WaveFormat;
        if (fmt == null) return;

        try
        {
            int channels = Math.Max(1, fmt.Channels);
            int bytesPerSample = fmt.BitsPerSample / 8;
            int frames = a.BytesRecorded / (bytesPerSample * channels);
            if (frames <= 0) return;

            if (_floatBuf.Length < frames) _floatBuf = new float[frames];

            // Downmix pra mono: media dos canais (o ToMono do NAudio so aceita 2).
            if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && bytesPerSample == 4)
            {
                for (int i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToSingle(a.Buffer, (i * channels + c) * 4);
                    _floatBuf[i] = sum / channels;
                }
            }
            else if (bytesPerSample == 2)
            {
                for (int i = 0; i < frames; i++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        int off = (i * channels + c) * 2;
                        sum += (short)(a.Buffer[off] | (a.Buffer[off + 1] << 8));
                    }
                    _floatBuf[i] = sum / (float)channels / 32768f;
                }
            }
            else return;   // formato inesperado

            // Resample simples (nearest) se o engine nao entregou 48k.
            int outFrames = frames;
            if (fmt.SampleRate != 48000)
            {
                outFrames = (int)((long)frames * 48000 / fmt.SampleRate);
                if (outFrames <= 0) return;
                var resampled = new float[outFrames];
                for (int i = 0; i < outFrames; i++)
                    resampled[i] = _floatBuf[(int)((long)i * frames / outFrames)];
                _floatBuf = resampled;
            }

            if (_pcmBuf.Length < outFrames * 2) _pcmBuf = new byte[outFrames * 2];
            float peak = 0;
            for (int i = 0; i < outFrames; i++)
            {
                float f = Math.Clamp(_floatBuf[i], -1f, 1f);
                if (Math.Abs(f) > peak) peak = Math.Abs(f);
                short s = (short)(f * 32767);
                _pcmBuf[i * 2] = (byte)s;
                _pcmBuf[i * 2 + 1] = (byte)(s >> 8);
            }
            Peak = peak;

            _acc.Append(_pcmBuf, 0, outFrames * 2);
            while (_acc.TryDequeueFrame(_frame, 0, FrameBytes))
                _transport.SendSharedAudio(_frame, 0, FrameBytes);
        }
        catch (Exception ex) { Log.Write("DJ: erro no audio: " + ex.Message); }
    }
}

/// <summary>
/// Le a faixa que esta tocando no Windows (SMTC) — pega Spotify, navegador, etc.
/// </summary>
public static class NowPlaying
{
    /// <summary>"Artista - Titulo", ou "" se nada estiver tocando.</summary>
    public static async Task<string> ReadAsync()
    {
        try
        {
            var mgr = await Windows.Media.Control
                .GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = mgr?.GetCurrentSession();
            if (session == null) return "";
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return "";
            string artist = props.Artist ?? "";
            string title = props.Title ?? "";
            if (title.Length == 0) return "";
            return artist.Length > 0 ? artist + " - " + title : title;
        }
        catch (Exception ex)
        {
            Log.Write("tocando agora falhou: " + ex.Message);
            return "";
        }
    }
}
