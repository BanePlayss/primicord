using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Primicord;

/// <summary>Tailscale is a routed overlay, never treated as an unlimited local LAN.</summary>
public static class ConnectionPolicy
{
    public static bool IsTailnetAddress(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    public static List<IPAddress> TailnetAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    !(nic.Name.Contains("tailscale", StringComparison.OrdinalIgnoreCase) ||
                      nic.Description.Contains("tailscale", StringComparison.OrdinalIgnoreCase))) continue;
                addresses.AddRange(nic.GetIPProperties().UnicastAddresses
                    .Select(x => x.Address).Where(IsTailnetAddress));
            }
        }
        catch (NetworkInformationException) { }
        return addresses;
    }

    public static string Summary => TailnetAddresses().Count > 0 ? "Tailscale disponível" : "Tailscale desconectado";
}
