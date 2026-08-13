using System.Net;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>Regras de sinalizacao — puras, e sao elas que impedem o "glare".</summary>
public sealed class SignalingRuleTests
{
    [Theory]
    [InlineData("bane-1a2f3", "caco-9b8c1")]
    [InlineData("caco-9b8c1", "bane-1a2f3")]
    [InlineData("aaa", "zzz")]
    public void Os_dois_lados_calculam_a_mesma_chave(string a, string b)
    {
        Assert.Equal(WebRtcSignaling.PairKey(a, b), WebRtcSignaling.PairKey(b, a));
    }

    [Fact]
    public void Exatamente_um_dos_lados_oferta()
    {
        var fs = new Firestore();
        var lucas = new WebRtcSignaling(fs, "sala", "lucas-111");
        var bane = new WebRtcSignaling(fs, "sala", "bane-222");

        // Se os dois ofertassem, as duas ofertas se sobrescreveriam no mesmo doc
        // e a conexao nunca fecharia. Este e o invariante que garante que nao.
        Assert.NotEqual(lucas.IsOfferer("bane-222"), bane.IsOfferer("lucas-111"));
    }

    [Fact]
    public void Candidatos_sobrevivem_a_ida_e_volta()
    {
        var cands = new[] { "{\"candidate\":\"um\"}", "{\"candidate\":\"dois\"}" };
        var blob = string.Join("\n", cands);
        Assert.Equal(cands, WebRtcSignaling.SplitCandidates(blob));
        Assert.Empty(WebRtcSignaling.SplitCandidates(""));
    }
}

/// <summary>
/// A malha WebRTC de verdade: duas <see cref="WebRtcVoiceMesh"/> negociando entre
/// si com sinalizacao em memoria, e audio real atravessando.
/// </summary>
/// <remarks>
/// E o teste que importa — a classe mais nova e menos rodada do projeto. Cobre a
/// maquina de estados inteira: papel de ofertante, oferta, resposta, candidatos,
/// DTLS e o caminho Opus de ponta a ponta.
///
/// Sem STUN de proposito (StunServers vazio): em loopback os candidatos locais
/// bastam, a coleta termina na hora e o teste nao depende de internet.
/// </remarks>
[Collection("rede")]
public sealed class WebRtcVoiceMeshTests
{
    private static (RoomSession A, RoomSession B) Presence()
    {
        var fs = new Firestore();
        var a = new RoomSession(fs, "sala", "peer-aaa", "A");
        var b = new RoomSession(fs, "sala", "peer-bbb", "B");
        // A malha WebRTC so LE a lista de presenca; nao precisa de socket de pe.
        a.AddPeerDirect("peer-bbb", "B", Array.Empty<IPEndPoint>());
        b.AddPeerDirect("peer-aaa", "A", Array.Empty<IPEndPoint>());
        return (a, b);
    }

    private static WebRtcVoiceMesh Mesh(IWebRtcSignaling sig, RoomSession session)
        => new(sig, session)
        {
            TickMs = 250,
            GatherTimeoutMs = 1500,
            StunServers = Array.Empty<string>(),
        };

    [Fact]
    public async Task Duas_malhas_negociam_e_conectam_sozinhas()
    {
        var (sa, sb) = Presence();
        var bus = new MemorySignalingBus();
        using var a = Mesh(bus.For("peer-aaa"), sa);
        using var b = Mesh(bus.For("peer-bbb"), sb);

        a.Start();
        b.Start();

        bool up = await Wait.UntilAsync(() => a.ConnectedCount == 1 && b.ConnectedCount == 1, 45_000);
        Assert.True(up, $"nao conectou (A={a.ConnectedCount}/{a.LinkCount}, B={b.ConnectedCount}/{b.LinkCount})");
    }

    [Fact]
    public async Task Voz_atravessa_codificada_em_opus()
    {
        var (sa, sb) = Presence();
        var bus = new MemorySignalingBus();
        using var a = Mesh(bus.For("peer-aaa"), sa);
        using var b = Mesh(bus.For("peer-bbb"), sb);

        double peak = 0;
        int frames = 0;
        uint sawSender = 0;
        b.VoiceReceived += (sender, buf, off, len) =>
        {
            // Sem Assert aqui: isto roda na thread de rede, dentro de um try/catch
            // do OnAudioPacket — uma falha seria engolida e o teste passaria errado.
            sawSender = sender;
            for (int i = off; i + 1 < off + len; i += 2)
                peak = Math.Max(peak, Math.Abs((short)(buf[i] | (buf[i + 1] << 8))) / 32768.0);
            Interlocked.Increment(ref frames);
        };

        a.Start();
        b.Start();
        Assert.True(await Wait.UntilAsync(() => a.ConnectedCount == 1, 45_000), "nao conectou");

        // 1s de tom de 440Hz, entregue como o VoiceEngine entrega: frames de 10ms.
        var frame = new byte[VoiceEngine.FrameBytes];
        int phase = 0;
        for (int f = 0; f < 100; f++)
        {
            for (int i = 0; i < VoiceEngine.FrameBytes / 2; i++, phase++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * phase / 48000.0) * 12000);
                frame[i * 2] = (byte)s;
                frame[i * 2 + 1] = (byte)(s >> 8);
            }
            a.SendVoice(frame, 0, frame.Length);
            await Task.Delay(10);
        }

        Assert.True(await Wait.UntilAsync(() => frames > 20, 5_000),
                    $"chegaram so {frames} quadros de audio");
        // O senderId TEM que ser o mesmo hash que a UI usa pra indexar tile e volume.
        Assert.Equal(RoomSession.HashId("peer-aaa"), sawSender);
        // O tom entra em ~0,366 de pico; o Opus e com perda, entao a margem e larga.
        Assert.InRange(peak, 0.15, 0.95);
    }

    [Fact]
    public async Task Mudo_nao_manda_nada()
    {
        var (sa, sb) = Presence();
        var bus = new MemorySignalingBus();
        using var a = Mesh(bus.For("peer-aaa"), sa);
        using var b = Mesh(bus.For("peer-bbb"), sb);

        int frames = 0;
        b.VoiceReceived += (_, _, _, _) => Interlocked.Increment(ref frames);

        a.Start();
        b.Start();
        Assert.True(await Wait.UntilAsync(() => a.ConnectedCount == 1, 45_000), "nao conectou");

        a.Muted = true;
        var frame = new byte[VoiceEngine.FrameBytes];
        new Random(3).NextBytes(frame);
        for (int f = 0; f < 30; f++) { a.SendVoice(frame, 0, frame.Length); await Task.Delay(10); }

        await Task.Delay(500);
        Assert.Equal(0, frames);
    }

    [Fact]
    public async Task Peer_que_sai_da_presenca_perde_a_conexao()
    {
        var fs = new Firestore();
        var sa = new RoomSession(fs, "sala", "peer-aaa", "A");
        var sb = new RoomSession(fs, "sala", "peer-bbb", "B");
        // Esta precisa subir de verdade: Dispose() e no-op numa sessao que nunca
        // rodou, e o teste depende do Dispose esvaziar a lista de presenca.
        await sa.StartNetworkOnlyAsync(useStun: false);
        sa.AddPeerDirect("peer-bbb", "B", Array.Empty<IPEndPoint>());
        sb.AddPeerDirect("peer-aaa", "A", Array.Empty<IPEndPoint>());

        var bus = new MemorySignalingBus();
        using var a = Mesh(bus.For("peer-aaa"), sa);
        using var b = Mesh(bus.For("peer-bbb"), sb);

        a.Start();
        b.Start();
        Assert.True(await Wait.UntilAsync(() => a.ConnectedCount == 1, 45_000), "nao conectou");

        // Some da lista de presenca (é o que o poll do Firestore faria).
        sa.Dispose();
        Assert.True(await Wait.UntilAsync(() => a.LinkCount == 0, 10_000),
                    "a malha deveria ter derrubado o par que saiu");
    }
}
