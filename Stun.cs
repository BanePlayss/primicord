using System.Net;
using System.Net.Sockets;

namespace Primicord;

/// <summary>
/// Descoberta do endereco publico (RFC 5389, so o Binding Request).
/// </summary>
/// <remarks>
/// Pra dois PCs atras de roteadores domesticos se acharem, cada um precisa saber como
/// o MUNDO o enxerga — o IP:porta que o roteador dele criou. O servidor STUN responde
/// exatamente isso: "o pacote que voce mandou chegou aqui vindo de tal IP:porta".
///
/// CRITICO: a consulta tem que sair pelo MESMO socket que vai carregar a voz depois.
/// A porta publica e criada pelo NAT por socket; perguntar de outro socket devolveria
/// um mapeamento que nao serve pra nada.
/// </remarks>
public static class Stun
{
    private const uint MagicCookie = 0x2112A442;

    public static readonly string[] DefaultServers =
    {
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302",
        "stun.cloudflare.com:3478",
    };

    /// <summary>
    /// Pergunta ao STUN qual e o endpoint publico deste socket. Devolve null se
    /// nenhum servidor responder (sem internet, ou UDP bloqueado na saida).
    /// </summary>
    public static async Task<IPEndPoint?> DiscoverAsync(Socket socket, int timeoutMs = 1200,
                                                        CancellationToken ct = default)
    {
        foreach (string server in DefaultServers)
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                var ep = await ResolveAsync(server, ct).ConfigureAwait(false);
                if (ep is null) continue;

                var result = await QueryAsync(socket, ep, timeoutMs, ct).ConfigureAwait(false);
                if (result != null)
                {
                    Log.Write($"STUN {server} -> publico {result}");
                    return result;
                }
            }
            catch (Exception ex) { Log.Write($"STUN {server} falhou: {ex.Message}"); }
        }
        Log.Write("STUN: nenhum servidor respondeu");
        return null;
    }

    private static async Task<IPEndPoint?> ResolveAsync(string hostPort, CancellationToken ct)
    {
        int colon = hostPort.LastIndexOf(':');
        string host = colon > 0 ? hostPort[..colon] : hostPort;
        int port = colon > 0 && int.TryParse(hostPort[(colon + 1)..], out var p) ? p : 3478;
        var addrs = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        foreach (var a in addrs)
            if (a.AddressFamily == AddressFamily.InterNetwork) return new IPEndPoint(a, port);
        return null;
    }

    private static async Task<IPEndPoint?> QueryAsync(Socket socket, IPEndPoint server,
                                                      int timeoutMs, CancellationToken ct)
    {
        // Binding Request: type(2) + length(2) + cookie(4) + transactionId(12) = 20 bytes.
        byte[] req = new byte[20];
        req[0] = 0x00; req[1] = 0x01;   // Binding Request
        req[2] = 0x00; req[3] = 0x00;   // length 0 (sem atributos)
        WriteUInt32(req, 4, MagicCookie);
        byte[] txId = new byte[12];
        Random.Shared.NextBytes(txId);
        Buffer.BlockCopy(txId, 0, req, 8, 12);

        await socket.SendToAsync(req, SocketFlags.None, server, ct).ConfigureAwait(false);

        // Espera a resposta correspondente. Como o socket e o mesmo do trafego de voz,
        // pacotes de outros pares podem chegar no meio — descarta e continua esperando.
        var buf = new byte[512];
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        while (!timeoutCts.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buf, SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (SocketException) { continue; }

            var parsed = ParseResponse(buf, r.ReceivedBytes, txId);
            if (parsed != null) return parsed;
        }
        return null;
    }

    /// <summary>
    /// Le o endpoint de uma resposta STUN. Retorna null se o pacote nao for uma
    /// Binding Response desta transacao (ex.: pacote de voz de outro par).
    /// </summary>
    public static IPEndPoint? ParseResponse(byte[] buf, int len, byte[] expectedTxId)
    {
        if (len < 20) return null;
        if (buf[0] != 0x01 || buf[1] != 0x01) return null;              // Binding Response
        if (ReadUInt32(buf, 4) != MagicCookie) return null;
        for (int i = 0; i < 12; i++) if (buf[8 + i] != expectedTxId[i]) return null;

        int attrLen = (buf[2] << 8) | buf[3];
        int pos = 20;
        int end = Math.Min(len, 20 + attrLen);

        IPEndPoint? mapped = null;
        while (pos + 4 <= end)
        {
            int type = (buf[pos] << 8) | buf[pos + 1];
            int vlen = (buf[pos + 2] << 8) | buf[pos + 3];
            int vpos = pos + 4;
            if (vpos + vlen > end) break;

            // 0x0020 XOR-MAPPED-ADDRESS (preferido) / 0x0001 MAPPED-ADDRESS (legado)
            if ((type == 0x0020 || type == 0x0001) && vlen >= 8 && buf[vpos + 1] == 0x01)
            {
                int port = (buf[vpos + 2] << 8) | buf[vpos + 3];
                uint addr = ReadUInt32(buf, vpos + 4);
                if (type == 0x0020)
                {
                    port ^= (int)(MagicCookie >> 16);
                    addr ^= MagicCookie;
                }
                var ip = new IPAddress(new byte[]
                {
                    (byte)(addr >> 24), (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
                });
                var candidate = new IPEndPoint(ip, port & 0xFFFF);
                if (type == 0x0020) return candidate;   // XOR e o confiavel
                mapped ??= candidate;
            }

            pos = vpos + vlen;
            if (vlen % 4 != 0) pos += 4 - (vlen % 4);   // atributos alinhados em 4 bytes
        }
        return mapped;
    }

    private static void WriteUInt32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    private static uint ReadUInt32(byte[] b, int off)
        => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
}
