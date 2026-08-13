using System.Net;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// A malha UDP, exercitada de verdade em loopback: dois sockets reais, o protocolo
/// real, sem Firestore.
/// </summary>
/// <remarks>
/// O RoomSession ja tinha as portas de entrada pra isto (StartNetworkOnlyAsync e
/// AddPeerDirect) e os comentarios citavam um "harness de teste" que nao estava no
/// repositorio. Aqui ele existe.
/// </remarks>
[Collection("rede")]
public sealed class ProtocolTests
{
    /// <summary>Sobe dois lados em loopback e espera o furo fechar dos dois lados.</summary>
    private static async Task<(RoomSession A, RoomSession B)> PairAsync()
    {
        var fs = new Firestore();   // nunca usado: StartNetworkOnlyAsync desliga a presenca
        var a = new RoomSession(fs, "sala-teste", "peer-aaa", "A");
        var b = new RoomSession(fs, "sala-teste", "peer-bbb", "B");

        await a.StartNetworkOnlyAsync(useStun: false);
        await b.StartNetworkOnlyAsync(useStun: false);

        a.AddPeerDirect("peer-bbb", "B", new[] { new IPEndPoint(IPAddress.Loopback, b.LocalPort) });
        b.AddPeerDirect("peer-aaa", "A", new[] { new IPEndPoint(IPAddress.Loopback, a.LocalPort) });

        bool up = await Wait.UntilAsync(
            () => a.Peers.Any(p => p.Connected) && b.Peers.Any(p => p.Connected), 10_000);
        Assert.True(up, "os dois lados deveriam ter fechado o furo em loopback");
        return (a, b);
    }

    [Fact]
    public async Task Punch_fecha_e_marca_o_endereco_que_respondeu()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            var peer = Assert.Single(a.Peers);
            Assert.NotNull(peer.Locked);
            Assert.Equal(b.LocalPort, peer.Locked!.Port);
            // Loopback e endereco privado: a sessao tem que se reconhecer como LAN,
            // que e o que libera o orcamento alto de tela.
            Assert.True(peer.OnLan);
        }
    }

    [Fact]
    public async Task Voz_chega_intacta_do_outro_lado()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            byte[]? got = null;
            b.VoiceReceived += (_, buf, off, len) =>
            {
                if (got == null) got = buf.Skip(off).Take(len).ToArray();
            };

            var frame = new byte[VoiceEngine.FrameBytes];
            for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i * 7 % 251);
            a.SendVoice(frame, 0, frame.Length);

            Assert.True(await Wait.UntilAsync(() => got != null, 5_000), "a voz nao chegou");
            Assert.Equal(frame, got);
        }
    }

    [Fact]
    public async Task Mudo_nao_manda_voz()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            int count = 0;
            b.VoiceReceived += (_, _, _, _) => Interlocked.Increment(ref count);

            a.Muted = true;
            a.SendVoice(new byte[VoiceEngine.FrameBytes], 0, VoiceEngine.FrameBytes);

            await Task.Delay(500);
            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task Quadro_de_tela_fragmentado_remonta_byte_a_byte()
    {
        var (a, b) = await PairAsync();
        using (a) using (b)
        {
            // Bem maior que o payload de um datagrama (1100B): tem que picar em
            // varios pedacos e remontar na ordem certa.
            var jpeg = new byte[9_500];
            new Random(1234).NextBytes(jpeg);

            byte[]? got = null;
            int gotW = 0, gotH = 0;
            b.ScreenFrameReceived += (_, payload, w, h) =>
            {
                if (got == null) { got = payload; gotW = w; gotH = h; }
            };

            a.SendScreenFrame(jpeg, jpeg.Length, 1920, 1080);

            Assert.True(await Wait.UntilAsync(() => got != null, 5_000), "o quadro nao remontou");
            Assert.Equal(1920, gotW);
            Assert.Equal(1080, gotH);
            Assert.Equal(jpeg, got);
        }
    }

    [Fact]
    public async Task Bye_tira_o_peer_da_lista_do_outro_lado()
    {
        var (a, b) = await PairAsync();
        using (b)
        {
            Assert.Single(b.Peers);
            a.Dispose();   // manda TypeBye na saida
            Assert.True(await Wait.UntilAsync(() => b.Peers.Count == 0, 5_000),
                        "o peer deveria ter sumido depois do bye");
        }
    }

    [Fact]
    public void HashId_e_estavel_e_separa_ids_parecidos()
    {
        Assert.Equal(RoomSession.HashId("bane-1a2f3"), RoomSession.HashId("bane-1a2f3"));
        Assert.NotEqual(RoomSession.HashId("bane-1a2f3"), RoomSession.HashId("bane-1a2f4"));
    }
}

/// <summary>Testes de rede nao rodam em paralelo: brigariam por porta e por CPU.</summary>
[CollectionDefinition("rede", DisableParallelization = true)]
public sealed class RedeCollection { }
