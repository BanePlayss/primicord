using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Primicord;

/// <summary>Pastas, log e utilidades de rede compartilhadas.</summary>
public static class AppEnv
{
    /// <summary>%APPDATA%\Primicord — sobrevive a updates do instalador.</summary>
    public static string DataDir
    {
        get
        {
            string d = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Primicord");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>%APPDATA%\Primicord\Clipes — destino dos clipes gravados (Fase 3).</summary>
    public static string ClipsDir
    {
        get
        {
            string d = Path.Combine(DataDir, "Clipes");
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>Todos os IPv4 desta maquina (pra publicar o endpoint local e
    /// permitir conexao direta quando dois primitivos estao na mesma rede).</summary>
    public static List<IPAddress> LocalIPv4()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = nic.GetIPProperties(); } catch { continue; }
                foreach (var u in props.UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(u.Address);
            }
        }
        catch { }
        return list;
    }

    public static bool IsOwnAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        foreach (var a in LocalIPv4()) if (a.Equals(ip)) return true;
        return false;
    }

    /// <summary>Impede WSAECONNRESET(10054) de matar o socket UDP quando o destino
    /// ainda nao existe — inevitavel durante o hole punching.</summary>
    public static void DisableUdpConnReset(Socket socket)
    {
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C
            socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
        }
        catch { /* so existe no Windows; ignora em qualquer outro caso */ }
    }
}

/// <summary>Log em arquivo (%TEMP%\Primicord\primicord.log) — o app roda sem console.</summary>
public static class Log
{
    private static readonly object Lock = new();
    private static readonly string Path_ = InitPath();

    private static string InitPath()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Primicord");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "primicord.log");
        }
        catch { return Path.Combine(Path.GetTempPath(), "primicord.log"); }
    }

    public static string FilePath => Path_;

    public static void Write(string msg)
    {
        string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
        System.Diagnostics.Debug.WriteLine("[primicord] " + line);
        try
        {
            lock (Lock)
            {
                // Trunca se passar de 2MB pra nao crescer sem fim.
                try
                {
                    var fi = new FileInfo(Path_);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(Path_);
                }
                catch { }
                File.AppendAllText(Path_, line + Environment.NewLine);
            }
        }
        catch { }
    }
}
