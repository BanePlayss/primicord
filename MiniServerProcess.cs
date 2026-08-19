using Microsoft.Win32;
using System.Diagnostics;

namespace Primicord;

/// <summary>Inicia o coordenador local no PC escolhido e o mantem no login do Windows.</summary>
public static class MiniServerProcess
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "Primicord Mini Server";
    private const string FirewallName = "Primicord Mini Server (Tailscale)";

    public static string? ExecutablePath
    {
        get
        {
            string installed = Path.Combine(AppContext.BaseDirectory, "Primicord.Server.exe");
            if (File.Exists(installed)) return installed;

            // Caminho de desenvolvimento, quando o app roda pelo dotnet run.
            string dev = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                                                       "server", "bin", "Release", "net10.0", "win-x64",
                                                       "publish", "Primicord.Server.exe"));
            return File.Exists(dev) ? dev : null;
        }
    }

    public static void Configure(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (!enabled)
            {
                key.DeleteValue(RunName, throwOnMissingValue: false);
                Stop();
                return;
            }

            string? exe = ExecutablePath;
            if (exe == null)
            {
                Log.Write("mini servidor: Primicord.Server.exe nao encontrado");
                return;
            }

            key.SetValue(RunName, $"\"{exe}\"", RegistryValueKind.String);
            EnsureFirewallRule();
            EnsureRunning(exe);
        }
        catch (Exception ex) { Log.Write("mini servidor: nao configurei inicializacao: " + ex.Message); }
    }

    /// <summary>
    /// Libera o executavel antes de uma atualizacao e encerra o servidor quando
    /// este PC deixa de ser o host. So toca no processo que veio desta instalacao.
    /// </summary>
    public static void Stop()
    {
        string? expected = ExecutablePath;
        if (expected == null) return;

        foreach (var process in Process.GetProcessesByName("Primicord.Server"))
        {
            using (process)
            {
                try
                {
                    string? running = process.MainModule?.FileName;
                    if (running == null || !Path.GetFullPath(running).Equals(
                            Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                        continue;

                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                    Log.Write("mini servidor: encerrado");
                }
                catch (Exception ex) { Log.Write("mini servidor: nao encerrou: " + ex.Message); }
            }
        }
    }

    private static void EnsureRunning(string exe)
    {
        try
        {
            if (Process.GetProcessesByName("Primicord.Server").Length > 0) return;
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            Log.Write("mini servidor: iniciado neste PC");
        }
        catch (Exception ex) { Log.Write("mini servidor: nao iniciou: " + ex.Message); }
    }

    private static void EnsureFirewallRule()
    {
        try
        {
            const string rulesPath = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            using var rules = Registry.LocalMachine.OpenSubKey(rulesPath);
            if (rules != null)
                foreach (string valueName in rules.GetValueNames())
                    if (rules.GetValue(valueName) is string value
                        && value.Contains("Name=" + FirewallName, StringComparison.OrdinalIgnoreCase))
                        return;

            string args = "advfirewall firewall add rule "
                        + $"name=\"{FirewallName}\" dir=in action=allow enable=yes "
                        + "protocol=TCP localport=8765 "
                        + "remoteip=100.64.0.0/10";
            using var netsh = Process.Start(new ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            netsh?.WaitForExit(15_000);
            if (netsh is { HasExited: true, ExitCode: 0 })
                Log.Write("mini servidor: regra de firewall limitada a tailnet criada");
            else
                Log.Write("mini servidor: regra de firewall nao foi criada");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log.Write("mini servidor: permissao da regra de firewall foi cancelada");
        }
        catch (Exception ex) { Log.Write("mini servidor: firewall: " + ex.Message); }
    }
}
