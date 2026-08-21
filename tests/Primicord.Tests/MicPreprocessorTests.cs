using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// O tratamento do microfone, medido. O teste monta um caminho de eco sintetico
/// (atraso + atenuacao) e conta quanta energia sobra depois do cancelador.
/// </summary>
/// <remarks>
/// ATENCAO AO LER O NUMERO: aqui o eco e linear, sem ruido e sem deriva de relogio,
/// entao a atenuacao sai MUITO maior do que sai numa sala de verdade (onde ha
/// reverberacao, alto-falante nao-linear e placas de som com relogios diferentes).
/// O piso do teste e baixo de proposito: ele existe pra provar que a ligacao esta
/// certa e que o filtro converge, NAO pra prometer numero de campo.
/// </remarks>
public sealed class MicPreprocessorTests
{
    private const int FrameBytes = VoiceEngine.FrameBytes;   // 960 = 10ms
    private const int FrameSamples = FrameBytes / 2;
    private const int Rate = 48000;

    /// <summary>Sinal tipo voz: varios harmonicos com envelope que liga e desliga.</summary>
    private static short FarEnd(int n)
    {
        double t = n / (double)Rate;
        double v = Math.Sin(2 * Math.PI * 220 * t) * 0.5
                 + Math.Sin(2 * Math.PI * 440 * t) * 0.3
                 + Math.Sin(2 * Math.PI * 880 * t) * 0.15;
        double env = (n / (Rate / 2)) % 2 == 0 ? 1.0 : 0.15;
        return (short)(v * env * 11000);
    }

    private static void WriteFrame(byte[] dst, Func<int, short> sample, int baseIdx)
    {
        for (int i = 0; i < FrameSamples; i++)
        {
            short s = sample(baseIdx + i);
            dst[i * 2] = (byte)s;
            dst[i * 2 + 1] = (byte)(s >> 8);
        }
    }

    private static double Energy(byte[] pcm)
    {
        double e = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            e += (double)s * s;
        }
        return e;
    }

    [Fact]
    public void A_nativa_do_speex_carrega()
    {
        // Nao e trivial: a nativa vai dentro do exe de arquivo unico e so se
        // extrai em tempo de execucao. Se isto quebrar num build futuro, o app
        // segue funcionando (o MicPreprocessor se desliga sozinho) mas SEM AEC —
        // e o sintoma seria "voltou o eco", dificil de rastrear sem este teste.
        using var p = new MicPreprocessor();
        Assert.True(p.Active, "o tratamento do microfone deveria ter subido");
    }

    [Fact]
    public void Cancela_o_eco_do_que_saiu_no_alto_falante()
    {
        using var p = new MicPreprocessor();
        Assert.True(p.Active);

        const int delaySamples = 2400;   // 50ms de ida e volta pelo ar
        const float echoGain = 0.5f;
        int frames = Rate * 10 / FrameSamples;
        int convergedAt = frames / 2;

        var play = new byte[FrameBytes];
        var mic = new byte[FrameBytes];
        double antes = 0, depois = 0;

        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * FrameSamples;
            WriteFrame(play, FarEnd, baseIdx);
            // O microfone ouve SO o eco — sem voz de perto. Assim toda energia que
            // sobrar na saida e residuo, e a conta nao tem como se enganar.
            WriteFrame(mic, n => n - delaySamples >= 0 ? (short)(FarEnd(n - delaySamples) * echoGain) : (short)0,
                       baseIdx);

            double entrada = Energy(mic);
            p.PushPlayback(play, 0, FrameBytes);
            p.Process(mic, 0, FrameBytes);

            if (f >= convergedAt) { antes += entrada; depois += Energy(mic); }
        }

        // Medido ~16 dB na configuracao padrao. O piso de 10 dB tem folga pra
        // variacao de maquina, mas NAO tem folga pra ligar o AGC por padrao: com
        // ele o cancelamento desaba pra ~2,5 dB e este teste quebra na hora. E de
        // proposito — e o unico guarda-costas dessa decisao.
        double db = depois <= 0 ? 99 : 10 * Math.Log10(antes / depois);
        Assert.True(db > 10, $"deveria atenuar o eco; atenuou so {db:F1} dB");
    }

    [Fact]
    public void Nao_engole_a_voz_quando_nao_ha_nada_tocando()
    {
        using var p = new MicPreprocessor();
        Assert.True(p.Active);

        // Sem PushPlayback: a referencia e silencio, entao nao ha eco a tirar e a
        // voz tem que sair do outro lado. Se o denoise/AGC estivessem destruindo o
        // sinal, e aqui que apareceria.
        var mic = new byte[FrameBytes];
        double energia = 0;
        int frames = Rate * 2 / FrameSamples;

        for (int f = 0; f < frames; f++)
        {
            WriteFrame(mic, FarEnd, f * FrameSamples);
            p.Process(mic, 0, FrameBytes);
            if (f > frames / 2) energia += Energy(mic);
        }

        Assert.True(energia > 0, "a voz sumiu inteira sem nenhum eco pra cancelar");
    }

    [Fact]
    public void A_fila_de_referencia_nao_cresce_sem_fim()
    {
        using var p = new MicPreprocessor();
        Assert.True(p.Active);

        // Reproducao correndo na frente do microfone (relogios de placa diferentes).
        // Sem teto, isto viraria atraso acumulado que nunca se paga.
        var play = new byte[FrameBytes];
        for (int f = 0; f < 500; f++) p.PushPlayback(play, 0, FrameBytes);

        Assert.InRange(p.QueuedFrames, 0, 20);
    }

    [Fact]
    public void Quadro_de_tamanho_errado_e_ignorado_sem_estourar()
    {
        using var p = new MicPreprocessor();
        var curto = new byte[FrameBytes / 2];
        new Random(5).NextBytes(curto);
        var copia = curto.ToArray();

        p.Process(curto, 0, curto.Length);

        Assert.Equal(copia, curto);   // saiu intacto, sem excecao
    }
}
