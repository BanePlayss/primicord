using System.Net;
using System.Net.Sockets;

namespace Primicord.Server;

public static class TailnetAddress
{
    public static bool IsAllowed(IPAddress? address)
    {
        if (address == null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (Environment.GetEnvironmentVariable("PRIMICORD_SERVER_ALLOW_LAN") == "1") return true;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] == 100 && bytes[1] is >= 64 and <= 127; // 100.64.0.0/10

        // Prefixo IPv6 fixo usado pelo Tailscale: fd7a:115c:a1e0::/48.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && bytes[0] == 0xfd && bytes[1] == 0x7a
            && bytes[2] == 0x11 && bytes[3] == 0x5c
            && bytes[4] == 0xa1 && bytes[5] == 0xe0;
    }
}
