using SpeexDSPSharp.Core;

namespace Primicord;

/// <summary>
/// Limpa o microfone antes dele ir pra rede: tira o eco, o chiado e nivela o volume.
/// </summary>
/// <remarks>
/// POR QUE EXISTE: o README manda "todo mundo de fone" porque nao havia cancelamento
/// de eco — quem usasse caixa de som devolvia a voz dos outros pelo proprio microfone
/// e a sala inteira se ouvia com atraso. O SpeexDSPSharp esta no .csproj desde o
/// primeiro commit e nunca foi usado; isto e ele finalmente ligado.
///
/// NAO VEIO COM O WEBRTC. E facil supor que sim, porque o WebRTC-o-padrao tem AEC —
/// mas quem faz aquilo e o NAVEGADOR. O SIPSorcery e so o transporte. Por isso isto
/// mora no VoiceEngine, ANTES do transporte, e vale igual pros dois caminhos (malha
/// UDP e WebRTC).
///
/// COMO O AEC FUNCIONA: ele precisa de dois sinais — o que o microfone captou (perto)
/// e o que saiu no alto-falante (longe). Sabendo o que foi tocado, ele aprende como a
/// sala devolve aquilo pro microfone e subtrai. O sinal de longe ja existia de graca:
/// e o <c>TapProvider</c> do VoiceEngine, que fica entre o mixer e a placa de som.
///
/// AS DUAS THREADS: o microfone chega numa thread (NAudio WaveIn) e a reproducao
/// noutra (a placa de som pedindo amostras). O estado do Speex NAO e seguro pra isso.
/// Em vez de travar os dois lados, a thread de reproducao so ENFILEIRA quadros aqui,
/// e todas as chamadas ao Speex acontecem na thread do microfone. A fila e limitada:
/// se a reproducao correr na frente (relogios de placa diferentes nunca batem exato),
/// o quadro mais velho e descartado em vez de acumular atraso pra sempre.
///
/// O QUE ELE NAO CANCELA: so o que o PRIMICORD toca. Som de jogo, Spotify e qualquer
/// outra coisa do sistema que vaze no microfone continua vazando — nao temos esse
/// sinal como referencia. E a mesma limitacao de qualquer AEC de aplicativo.
///
/// LIMITE DESTE WRAPPER, E ELE TEM CONSEQUENCIA: o SpeexDSPSharp nao expoe o handle
/// nativo do cancelador, entao nao da pra ligar o preprocessador nele
/// (SPEEX_PREPROCESS_SET_ECHO_STATE). Sem esse elo o preprocessador nao sabe o que e
/// residuo de eco, e o AGC trata residuo como voz baixinha e AMPLIFICA de volta.
/// Medido no caminho sintetico dos testes:
///
///   AEC puro ................ 16,0 dB de cancelamento
///   AEC + denoise ........... 16,1 dB   (o denoise sai de graca)
///   AEC + denoise + AGC ...... 2,5 dB   (o AGC come 13,5 dB)
///
/// Por isso o AGC vem DESLIGADO (ver <see cref="AutoGain"/>). Se um dia o
/// nivelamento com eco virar necessidade, o caminho e usar NativeSpeexDSP direto e
/// fazer o elo na mao.
/// </remarks>
public sealed class MicPreprocessor : IDisposable
{
    /// <summary>480 amostras = 10ms @ 48kHz — o mesmo quadro que o VoiceEngine ja corta.</summary>
    private const int FrameSamples = VoiceEngine.FrameBytes / 2;

    /// <summary>
    /// 200ms de cauda. Precisa cobrir a latencia da placa de saida (50 a 100ms aqui)
    /// mais o tempo que o som leva pra voltar pelo ar e a reverberacao da sala. Medido:
    /// 1,8% de um nucleo, entao nao vale economizar aqui e ficar curto.
    /// </summary>
    private const int FilterSamples = 48000 / 5;

    /// <summary>
    /// Teto da fila de referencia (200ms). Cheia = a reproducao correu na frente;
    /// descartar o mais velho e melhor que carregar atraso que nunca se paga.
    /// </summary>
    private const int MaxQueuedFrames = 20;

    private SpeexDSPEchoCanceler? _echo;
    private SpeexDSPPreprocessor? _preproc;

    private readonly object _queueLock = new();
    private readonly Queue<short[]> _playback = new();
    private readonly FrameAccumulator _playAcc = new();
    private readonly byte[] _playBytes = new byte[VoiceEngine.FrameBytes];

    private readonly short[] _mic = new short[FrameSamples];
    private readonly short[] _clean = new short[FrameSamples];
    private readonly short[] _silence = new short[FrameSamples];

    /// <summary>false quando o Speex nao subiu — o audio segue sem tratamento.</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// Nivelar o volume do microfone automaticamente. DESLIGADO por padrao: sem o
    /// elo com o cancelador (ver remarks da classe) ele desfaz 13,5 dB dos 16 dB de
    /// cancelamento de eco. Vale ligar SO pra quem usa fone.
    /// </summary>
    public bool AutoGain { get; init; }

    /// <summary>Quantos quadros de referencia esperam na fila (diagnostico).</summary>
    public int QueuedFrames { get { lock (_queueLock) return _playback.Count; } }

    public MicPreprocessor()
    {
        try
        {
            _echo = new SpeexDSPEchoCanceler(FrameSamples, FilterSamples);
            // Sem isto o Speex assume 8kHz e as constantes de adaptacao saem erradas.
            int rate = 48000;
            _echo.Ctl(EchoCancellationCtl.SPEEX_ECHO_SET_SAMPLING_RATE, ref rate);

            _preproc = new SpeexDSPPreprocessor(FrameSamples, 48000);
            int on = 1;
            int noiseSuppress = -25;   // dB de atenuacao do ruido de fundo
            _preproc.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_DENOISE, ref on);
            _preproc.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_NOISE_SUPPRESS, ref noiseSuppress);

            if (AutoGain)
            {
                int agc = 1;
                int agcMaxGain = 20;   // teto de amplificacao, pra sala silenciosa
                                       // nao virar chiado amplificado
                _preproc.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC, ref agc);
                _preproc.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_MAX_GAIN, ref agcMaxGain);

                // CUIDADO: AGC_LEVEL e o UNICO destes que o Speex le como FLOAT — os
                // outros sao inteiros. Passar `int 16000` aqui nao da erro nenhum: os
                // bytes sao reinterpretados como float, viram um alvo absurdo, e o AGC
                // ZERA o microfone. Voz muda pra todo mundo, sem uma linha de log.
                // (Custou um teste pra achar; ver MicPreprocessorTests.)
                float agcLevel = 16000f;   // alvo de volume, ~metade da escala
                _preproc.Ctl(PreprocessorCtl.SPEEX_PREPROCESS_SET_AGC_LEVEL, ref agcLevel);
            }

            Active = true;
            Log.Write($"mic: tratamento ligado (eco {FilterSamples * 1000 / 48000}ms, denoise"
                    + (AutoGain ? ", agc" : "") + ")");
        }
        catch (Exception ex)
        {
            // Biblioteca nativa que nao carregou nao pode derrubar a voz: sem
            // tratamento e pior que com, mas mudo e pior que os dois.
            Log.Write("mic: tratamento indisponivel, seguindo sem ele: " + ex.Message);
            Dispose();
            Active = false;
        }
    }

    /// <summary>
    /// O que esta indo pro alto-falante, pra servir de referencia do eco. Chamado da
    /// thread da placa de som — aqui so enfileira.
    /// </summary>
    public void PushPlayback(byte[] pcm, int offset, int count)
    {
        if (!Active || count <= 0) return;

        _playAcc.Append(pcm, offset, count);
        while (_playAcc.TryDequeueFrame(_playBytes, 0, VoiceEngine.FrameBytes))
        {
            var frame = new short[FrameSamples];
            for (int i = 0; i < FrameSamples; i++)
                frame[i] = (short)(_playBytes[i * 2] | (_playBytes[i * 2 + 1] << 8));

            lock (_queueLock)
            {
                if (_playback.Count >= MaxQueuedFrames) _playback.Dequeue();
                _playback.Enqueue(frame);
            }
        }
    }

    /// <summary>
    /// Limpa um quadro do microfone NO LUGAR. Espera exatamente
    /// <see cref="VoiceEngine.FrameBytes"/> bytes. Roda na thread do microfone.
    /// </summary>
    public void Process(byte[] pcm, int offset, int count)
    {
        if (!Active || count != VoiceEngine.FrameBytes) return;
        var echo = _echo;
        var preproc = _preproc;
        if (echo == null || preproc == null) return;

        try
        {
            for (int i = 0; i < FrameSamples; i++)
                _mic[i] = (short)(pcm[offset + i * 2] | (pcm[offset + i * 2 + 1] << 8));

            // Fila vazia = ninguem falando do outro lado. Alimentar silencio mantem o
            // filtro em passo com o microfone em vez de desalinhar quando a voz voltar.
            short[] reference;
            lock (_queueLock) reference = _playback.Count > 0 ? _playback.Dequeue() : _silence;

            echo.EchoPlayback(reference);
            echo.EchoCapture(_mic, _clean);
            preproc.Run(_clean);

            for (int i = 0; i < FrameSamples; i++)
            {
                pcm[offset + i * 2] = (byte)_clean[i];
                pcm[offset + i * 2 + 1] = (byte)(_clean[i] >> 8);
            }
        }
        catch (Exception ex)
        {
            Log.Write("mic: tratamento falhou, desligando: " + ex.Message);
            Active = false;   // o quadro segue cru; melhor que picotar a cada 10ms
        }
    }

    public void Dispose()
    {
        Active = false;
        try { _echo?.Dispose(); } catch { }
        try { _preproc?.Dispose(); } catch { }
        _echo = null;
        _preproc = null;
        lock (_queueLock) _playback.Clear();
    }
}
