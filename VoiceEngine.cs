using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Primicord;

public sealed record OutputDeviceItem(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed record InputDeviceItem(int DeviceNumber, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Captura o microfone, manda pra sala e toca a voz de todo mundo mixada.
/// </summary>
/// <remarks>
/// Formato: PCM 48kHz mono 16-bit, frames de 10ms (960 bytes) — mesmo do CherrySpy,
/// entao a qualidade e latencia sao as ja conhecidas. Cada participante ganha seu
/// proprio jitter buffer (a rede entrega os pacotes em rajadas e fora de ordem; o
/// buffer segura ~50ms pra reproduzir liso) e entra como uma fonte do mixer.
///
/// SEM CANCELAMENTO DE ECO: se alguem usar caixa de som em vez de fone, a voz dos
/// outros volta pelo microfone dele e todo mundo escuta o eco. TODOS DE FONE.
/// </remarks>
public sealed class VoiceEngine : IDisposable
{
    public const int FrameBytes = 960;   // 10ms @ 48k mono 16-bit
    private const int JitterPrimeMs = 50;
    private const int JitterMaxMs = 1000;

    private static readonly WaveFormat Pcm48Mono = new(48000, 16, 1);
    private static readonly WaveFormat Float48Mono = WaveFormat.CreateIeeeFloatWaveFormat(48000, 1);

    private WaveInEvent? _mic;
    private IWavePlayer? _out;
    private MixingSampleProvider? _mixer;
    private readonly FrameAccumulator _micAcc = new();
    private readonly byte[] _sendBuf = new byte[FrameBytes];

    /// <summary>Tira eco e ruido do microfone. null = tratamento desligado no config.</summary>
    private MicPreprocessor? _preproc;

    /// <summary>Ligar o tratamento do microfone (cancelar eco, tirar ruido).</summary>
    public bool PreprocessMic { get; init; } = true;

    /// <summary>
    /// Nivelar o volume do microfone. Desligado por padrao porque atrapalha o
    /// cancelamento de eco — ver <see cref="MicPreprocessor.AutoGain"/>.
    /// </summary>
    public bool MicAutoGain { get; init; }

    /// <summary>true se o tratamento subiu de verdade (a nativa do Speex carregou).</summary>
    public bool MicPreprocessActive => _preproc?.Active == true;

    /// <summary>
    /// Por onde a voz vai e vem. Pode ser a malha UDP (<see cref="RoomSession"/>) ou
    /// o WebRTC (<see cref="WebRtcVoiceMesh"/>) — daqui os dois sao a mesma coisa.
    /// </summary>
    private IVoiceTransport? _voiceTx;

    /// <summary>
    /// A musica do DJ continua vindo pela malha UDP mesmo quando a voz migrou pro
    /// WebRTC: e um fluxo separado, com volume proprio, e a malha segue de pe pra
    /// carregar tela e cinema de qualquer jeito.
    /// </summary>
    private RoomSession? _musicSource;

    private volatile bool _running;

    private readonly object _streamsLock = new();
    private readonly Dictionary<uint, PeerStream> _streams = new();

    /// <summary>Nivel do meu microfone (0..1) — pra barrinha e "estou falando".</summary>
    public float MyPeak { get; private set; }

    /// <summary>Erro fatal de audio pra mostrar na UI.</summary>
    public event Action<string>? Failed;

    /// <summary>PCM do meu microfone (o gravador de clipe escuta aqui).</summary>
    public event Action<byte[], int, int>? MicPcm;

    /// <summary>PCM do que EU ouço — vozes dos outros + musica do DJ, ja mixado.</summary>
    public event Action<byte[], int, int>? HeardPcm;

    /// <summary>Volume da musica do DJ (0..2), separado do volume das vozes.</summary>
    public float MusicVolume
    {
        get => _musicVolume;
        set
        {
            _musicVolume = Math.Clamp(value, 0f, 2f);
            lock (_streamsLock)
                foreach (var (key, st) in _streams)
                    if ((key & MusicFlag) != 0)
                    {
                        st.TargetVolume = _musicVolume;
                        st.Volume.Volume = _outputMuted ? 0f : st.TargetVolume;
                    }
        }
    }
    private float _musicVolume = 0.7f;
    private bool _outputMuted;

    /// <summary>Silencia/restaura tudo que vem da sala sem mexer no microfone.</summary>
    public bool OutputMuted
    {
        get => _outputMuted;
        set
        {
            _outputMuted = value;
            lock (_streamsLock)
                foreach (var st in _streams.Values)
                    st.Volume.Volume = value ? 0f : st.TargetVolume;
        }
    }

    /// <summary>
    /// Bit que separa a musica da voz da MESMA pessoa. O DJ manda dois fluxos ao
    /// mesmo tempo; sem isso eles cairiam no mesmo jitter buffer e picotariam.
    /// </summary>
    private const uint MusicFlag = 0x8000_0000;

    private sealed class PeerStream
    {
        public BufferedWaveProvider Jitter = null!;
        public VolumeSampleProvider Volume = null!;
        public float TargetVolume = 1f;
        public bool Primed;
        public float Peak;
        public long LastAudioTicks;
    }

    // ─── DISPOSITIVOS ────────────────────────────────────────────────────────

    public static List<InputDeviceItem> ListInputs()
    {
        var list = new List<InputDeviceItem>();
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            try { list.Add(new InputDeviceItem(i, WaveInEvent.GetCapabilities(i).ProductName)); }
            catch { }
        }
        return list;
    }

    public static List<OutputDeviceItem> ListOutputs()
    {
        var list = new List<OutputDeviceItem>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add(new OutputDeviceItem(d.ID, d.FriendlyName)); } catch { }
            }
        }
        catch (Exception ex) { Log.Write("enumerar saidas falhou: " + ex.Message); }
        return list;
    }

    // ─── CICLO DE VIDA ───────────────────────────────────────────────────────

    public void Start(int micDeviceNumber, string? outputDeviceId)
    {
        if (_running) return;
        _running = true;

        _mixer = new MixingSampleProvider(Float48Mono) { ReadFully = true };

        if (PreprocessMic) _preproc = new MicPreprocessor { AutoGain = MicAutoGain };

        // Deriva o que sai pro fone, convertido pra PCM 16-bit. Serve pra duas coisas:
        // o clipe grava "o que eu ouvi", e o cancelador de eco usa como REFERENCIA —
        // ele so consegue tirar do microfone aquilo que sabe que foi tocado.
        var tap = new TapProvider(_mixer, (buf, count) =>
        {
            _preproc?.PushPlayback(buf, 0, count);
            HeardPcm?.Invoke(buf, 0, count);
        });

        _out = OpenOutput(outputDeviceId);
        _out.Init(tap);
        _out.Play();

        if (micDeviceNumber >= 0)
        {
            _mic = new WaveInEvent
            {
                DeviceNumber = micDeviceNumber,
                WaveFormat = Pcm48Mono,
                BufferMilliseconds = 20,
                NumberOfBuffers = 3,
            };
            _mic.DataAvailable += OnMicData;
            try { _mic.StartRecording(); }
            catch (Exception ex)
            {
                Log.Write("microfone falhou: " + ex.Message);
                Failed?.Invoke("Nao consegui abrir o microfone: " + ex.Message);
                _mic.Dispose();
                _mic = null;
            }
        }

        Log.Write($"audio iniciado (mic={micDeviceNumber}, saida={outputDeviceId ?? "padrao"})");
    }

    private static IWavePlayer OpenOutput(string? deviceId)
    {
        if (!string.IsNullOrEmpty(deviceId))
        {
            try
            {
                using var en = new MMDeviceEnumerator();
                var dev = en.GetDevice(deviceId);
                if (dev != null && dev.State == DeviceState.Active)
                    return new WasapiOut(dev, AudioClientShareMode.Shared, true, 50);
            }
            catch (Exception ex) { Log.Write("saida escolhida falhou, usando padrao: " + ex.Message); }
        }
        return new WaveOutEvent { DesiredLatency = 100 };
    }

    /// <summary>
    /// Liga o motor de audio na rede: <paramref name="voice"/> carrega a voz,
    /// <paramref name="musicSource"/> entrega o audio do DJ. Normalmente sao o
    /// mesmo objeto (a malha UDP); com o WebRTC ligado, a voz vem de outro lugar.
    /// </summary>
    public void AttachTransport(IVoiceTransport voice, RoomSession musicSource)
    {
        _voiceTx = voice;
        voice.VoiceReceived += OnVoiceReceived;

        _musicSource = musicSource;
        musicSource.MusicReceived += OnMusicReceived;
    }

    public void Dispose()
    {
        _running = false;
        if (_voiceTx != null) _voiceTx.VoiceReceived -= OnVoiceReceived;
        if (_musicSource != null) _musicSource.MusicReceived -= OnMusicReceived;

        try { if (_mic != null) { _mic.DataAvailable -= OnMicData; _mic.StopRecording(); _mic.Dispose(); } }
        catch { }
        _mic = null;

        try { _out?.Stop(); _out?.Dispose(); } catch { }
        _out = null;

        // Depois do microfone e da saida pararem: e de la que o Process e o
        // PushPlayback sao chamados.
        try { _preproc?.Dispose(); } catch { }
        _preproc = null;

        lock (_streamsLock) _streams.Clear();
        _mixer = null;
        Log.Write("audio encerrado");
    }

    // ─── MICROFONE -> REDE ───────────────────────────────────────────────────

    private void OnMicData(object? sender, WaveInEventArgs a)
    {
        if (!_running) return;
        var transport = _voiceTx;
        if (transport == null) return;

        // Nivel do microfone CRU de proposito: a barrinha responde ao que o
        // dispositivo esta captando. Se ela lesse depois do tratamento, o AGC
        // nivelaria tudo e ela pararia de servir pra "meu mic esta pegando?".
        MyPeak = ComputePeak(a.Buffer, 0, a.BytesRecorded);

        // O driver entrega blocos de tamanho arbitrario; o acumulador recorta em
        // frames exatos de 10ms pra rede receber sempre o mesmo tamanho — que por
        // sorte e tambem o quadro que o Speex quer.
        _micAcc.Append(a.Buffer, 0, a.BytesRecorded);
        while (_micAcc.TryDequeueFrame(_sendBuf, 0, FrameBytes))
        {
            _preproc?.Process(_sendBuf, 0, FrameBytes);

            // O clipe grava o microfone JA LIMPO: e o que os outros ouviram. Gravar
            // o cru colocaria no clipe o eco que acabamos de tirar da sala — e pior,
            // somado ao HeardPcm ele apareceria duas vezes.
            if (!transport.Muted) MicPcm?.Invoke(_sendBuf, 0, FrameBytes);

            transport.SendVoice(_sendBuf, 0, FrameBytes);
        }
    }

    // ─── REDE -> ALTO-FALANTE ────────────────────────────────────────────────

    private void OnVoiceReceived(uint senderId, byte[] data, int offset, int count)
        => OnStreamData(senderId, data, offset, count, music: false);

    private void OnMusicReceived(uint senderId, byte[] data, int offset, int count)
        => OnStreamData(senderId | MusicFlag, data, offset, count, music: true);

    private void OnStreamData(uint key, byte[] data, int offset, int count, bool music)
    {
        if (!_running || count <= 0) return;

        PeerStream st;
        lock (_streamsLock)
        {
            if (!_streams.TryGetValue(key, out st!))
            {
                var mixer = _mixer;
                if (mixer == null) return;

                var jitter = new BufferedWaveProvider(Pcm48Mono)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(JitterMaxMs),
                    DiscardOnBufferOverflow = true,
                };
                float targetVolume = music ? _musicVolume : 1.0f;
                var vol = new VolumeSampleProvider(jitter.ToSampleProvider())
                { Volume = _outputMuted ? 0f : targetVolume };
                st = new PeerStream { Jitter = jitter, Volume = vol, TargetVolume = targetVolume };
                _streams[key] = st;
                mixer.AddMixerInput(vol);
                Log.Write($"stream de {(music ? "musica" : "voz")} criada p/ {key:X8}");
            }
        }

        // Primeira amostra: enche 50ms de silencio antes de tocar. Sem isso o buffer
        // comeca vazio, o mixer le silencio e da um clique/estalo no primeiro som.
        if (!st.Primed)
        {
            st.Primed = true;
            int silence = Pcm48Mono.AverageBytesPerSecond * JitterPrimeMs / 1000;
            silence -= silence % Pcm48Mono.BlockAlign;
            if (silence > 0) { try { st.Jitter.AddSamples(new byte[silence], 0, silence); } catch { } }
        }

        try { st.Jitter.AddSamples(data, offset, count); } catch { }
        st.Peak = ComputePeak(data, offset, count);
        st.LastAudioTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>Tira do mixer a voz E a musica de quem saiu (senao ficam fontes mortas).</summary>
    public void RemovePeer(uint senderId)
    {
        foreach (uint key in new[] { senderId, senderId | MusicFlag })
        {
            lock (_streamsLock)
            {
                if (!_streams.TryGetValue(key, out var st)) continue;
                _streams.Remove(key);
                try { _mixer?.RemoveMixerInput(st.Volume); } catch { }
            }
            Log.Write($"stream removida ({key:X8})");
        }
    }

    /// <summary>Nivel de voz de um participante (0..1); zera se ele parou de falar.</summary>
    public float PeakOf(uint senderId)
    {
        lock (_streamsLock)
        {
            if (!_streams.TryGetValue(senderId, out var st)) return 0f;
            if ((DateTime.UtcNow.Ticks - st.LastAudioTicks) > TimeSpan.TicksPerMillisecond * 250) return 0f;
            return st.Peak;
        }
    }

    /// <summary>Volume individual (0..2) — pra abaixar quem ta gritando.</summary>
    public void SetPeerVolume(uint senderId, float volume)
    {
        lock (_streamsLock)
            if (_streams.TryGetValue(senderId, out var st))
            {
                st.TargetVolume = Math.Clamp(volume, 0f, 2f);
                st.Volume.Volume = _outputMuted ? 0f : st.TargetVolume;
            }
    }

    /// <summary>
    /// Passa o audio adiante sem mudar nada, entregando uma copia em PCM 16-bit pra
    /// quem quiser gravar. Fica ENTRE o mixer e a placa de som.
    /// </summary>
    private sealed class TapProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly Action<byte[], int> _onPcm;
        private byte[] _pcm = Array.Empty<byte>();

        public WaveFormat WaveFormat => _source.WaveFormat;

        public TapProvider(ISampleProvider source, Action<byte[], int> onPcm)
        {
            _source = source;
            _onPcm = onPcm;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            int read = _source.Read(buffer, offset, count);
            if (read <= 0) return read;
            try
            {
                if (_pcm.Length < read * 2) _pcm = new byte[read * 2];
                for (int i = 0; i < read; i++)
                {
                    short s = (short)(Math.Clamp(buffer[offset + i], -1f, 1f) * 32767);
                    _pcm[i * 2] = (byte)s;
                    _pcm[i * 2 + 1] = (byte)(s >> 8);
                }
                _onPcm(_pcm, read * 2);
            }
            catch { /* gravacao nunca pode derrubar o audio */ }
            return read;
        }
    }

    /// <summary>
    /// Abre um microfone SO pra medir nivel (a barrinha do teste nas configuracoes).
    /// Nao manda nada pra rede nem toca em lugar nenhum.
    /// </summary>
    public sealed class MicMonitor : IDisposable
    {
        private WaveInEvent? _mic;
        public event Action<float>? LevelChanged;

        public MicMonitor(int deviceNumber)
        {
            _mic = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = Pcm48Mono,
                BufferMilliseconds = 50,
                NumberOfBuffers = 3,
            };
            _mic.DataAvailable += (_, a) => LevelChanged?.Invoke(ComputePeak(a.Buffer, 0, a.BytesRecorded));
            _mic.StartRecording();
        }

        public void Dispose()
        {
            try { _mic?.StopRecording(); _mic?.Dispose(); } catch { }
            _mic = null;
        }
    }

    private static float ComputePeak(byte[] buf, int offset, int count)
    {
        int peak = 0;
        for (int i = offset; i + 1 < offset + count; i += 2)
        {
            int s = (short)(buf[i] | (buf[i + 1] << 8));
            s = Math.Abs(s);
            if (s > peak) peak = s;
        }
        return peak / 32768f;
    }
}

/// <summary>
/// Recorta o fluxo do driver em frames de tamanho fixo. O WaveIn entrega blocos de
/// tamanho variavel; a rede quer sempre 960 bytes.
/// </summary>
public sealed class FrameAccumulator
{
    private readonly object _lock = new();
    private byte[] _buf = new byte[8192];
    private int _count;

    public void Append(byte[] data, int offset, int length)
    {
        if (length <= 0) return;
        lock (_lock)
        {
            EnsureCapacity(_count + length);
            Buffer.BlockCopy(data, offset, _buf, _count, length);
            _count += length;
        }
    }

    public bool TryDequeueFrame(byte[] frame, int frameOffset, int frameBytes)
    {
        lock (_lock)
        {
            if (_count < frameBytes) return false;
            Buffer.BlockCopy(_buf, 0, frame, frameOffset, frameBytes);
            int rest = _count - frameBytes;
            if (rest > 0) Buffer.BlockCopy(_buf, frameBytes, _buf, 0, rest);
            _count = rest;
            return true;
        }
    }

    public void Reset() { lock (_lock) _count = 0; }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length) return;
        int cap = _buf.Length * 2;
        while (cap < needed) cap *= 2;
        Array.Resize(ref _buf, cap);
    }
}
