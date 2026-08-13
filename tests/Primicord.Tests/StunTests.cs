using System.Net;
using Primicord;
using Xunit;

namespace Primicord.Tests;

/// <summary>
/// O parser de STUN le do MESMO socket que carrega a voz, entao ele precisa
/// recusar com seguranca tudo que nao for a resposta que ele pediu — se ele
/// aceitar um pacote de voz como se fosse endereco publico, a sala inteira passa
/// a furar o NAT no lugar errado.
/// </summary>
public sealed class StunTests
{
    private const uint MagicCookie = 0x2112A442;

    private static byte[] BuildResponse(IPAddress ip, int port, byte[] txId,
                                        bool xor = true, uint cookie = MagicCookie)
    {
        var buf = new byte[32];
        buf[0] = 0x01; buf[1] = 0x01;          // Binding Response
        buf[2] = 0x00; buf[3] = 12;            // um atributo de 12 bytes
        buf[4] = (byte)(cookie >> 24); buf[5] = (byte)(cookie >> 16);
        buf[6] = (byte)(cookie >> 8); buf[7] = (byte)cookie;
        Buffer.BlockCopy(txId, 0, buf, 8, 12);

        ushort type = xor ? (ushort)0x0020 : (ushort)0x0001;
        buf[20] = (byte)(type >> 8); buf[21] = (byte)type;
        buf[22] = 0x00; buf[23] = 8;           // tamanho do valor
        buf[24] = 0x00; buf[25] = 0x01;        // family IPv4

        int p = port;
        uint addr = (uint)IPAddress.NetworkToHostOrder(
            BitConverter.ToInt32(ip.GetAddressBytes(), 0));
        if (xor) { p ^= (int)(MagicCookie >> 16); addr ^= MagicCookie; }

        buf[26] = (byte)(p >> 8); buf[27] = (byte)p;
        buf[28] = (byte)(addr >> 24); buf[29] = (byte)(addr >> 16);
        buf[30] = (byte)(addr >> 8); buf[31] = (byte)addr;
        return buf;
    }

    private static byte[] Tx(byte seed)
    {
        var t = new byte[12];
        for (int i = 0; i < 12; i++) t[i] = (byte)(seed + i);
        return t;
    }

    [Fact]
    public void Le_o_endereco_de_uma_XOR_MAPPED_ADDRESS()
    {
        var tx = Tx(1);
        var pkt = BuildResponse(IPAddress.Parse("203.0.113.7"), 51234, tx);

        var ep = Stun.ParseResponse(pkt, pkt.Length, tx);

        Assert.NotNull(ep);
        Assert.Equal("203.0.113.7", ep!.Address.ToString());
        Assert.Equal(51234, ep.Port);
    }

    [Fact]
    public void Aceita_MAPPED_ADDRESS_legado_sem_xor()
    {
        var tx = Tx(2);
        var pkt = BuildResponse(IPAddress.Parse("198.51.100.42"), 3478, tx, xor: false);

        var ep = Stun.ParseResponse(pkt, pkt.Length, tx);

        Assert.NotNull(ep);
        Assert.Equal("198.51.100.42", ep!.Address.ToString());
        Assert.Equal(3478, ep.Port);
    }

    [Fact]
    public void Recusa_resposta_de_outra_transacao()
    {
        var pkt = BuildResponse(IPAddress.Parse("203.0.113.7"), 51234, Tx(3));
        Assert.Null(Stun.ParseResponse(pkt, pkt.Length, Tx(9)));
    }

    [Fact]
    public void Recusa_cookie_errado()
    {
        var tx = Tx(4);
        var pkt = BuildResponse(IPAddress.Parse("203.0.113.7"), 51234, tx, cookie: 0xDEADBEEF);
        Assert.Null(Stun.ParseResponse(pkt, pkt.Length, tx));
    }

    [Fact]
    public void Recusa_pacote_de_voz_que_chegou_no_meio()
    {
        // Exatamente o caso que o comentario do Stun.cs descreve: o socket e o
        // mesmo da voz, entao lixo chega no meio da espera.
        var tx = Tx(5);
        var voz = new byte[RoomSession.HeaderBytes + VoiceEngine.FrameBytes];
        new Random(7).NextBytes(voz);
        voz[0] = RoomSession.TypeVoice;

        Assert.Null(Stun.ParseResponse(voz, voz.Length, tx));
    }

    [Fact]
    public void Recusa_pacote_curto_demais()
    {
        Assert.Null(Stun.ParseResponse(new byte[8], 8, Tx(6)));
        Assert.Null(Stun.ParseResponse(Array.Empty<byte>(), 0, Tx(6)));
    }
}
