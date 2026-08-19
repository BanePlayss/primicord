using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Primicord.SetupShared;

/// <summary>
/// Regras minimas para a malha privada: TCP do mini servidor e UDP da voz,
/// ambas aceitando somente origens da faixa IPv4 do Tailscale.
/// </summary>
public static class NetworkFirewall
{
    public const string ReplicaRuleName = "Primicord Mini Server (Tailscale)";
    public const string VoiceRuleName = "Primicord Voice (Tailscale)";
    public const string TailscaleRange = "100.64.0.0/10";

    public static async Task EnsureAsync(string appPath, CancellationToken ct = default)
    {
        bool replicaExists = await RuleExistsAsync(ReplicaRuleName, ct).ConfigureAwait(false);
        bool voiceExists = await RuleExistsAsync(VoiceRuleName, ct).ConfigureAwait(false);
        if (replicaExists && voiceExists) return;

        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(appPath))
            throw new InvalidOperationException("O executavel instalado nao foi encontrado em " + appPath);

        string script = BuildPowerShellScript(appPath, replicaExists, voiceExists);
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        Process? firewall;
        try
        {
            firewall = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                          + encoded,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("A permissao das rotas de voz foi cancelada.", ex);
        }
        using (firewall)
        {
            if (firewall == null)
                throw new InvalidOperationException("O Windows nao abriu o firewall.");
            await firewall.WaitForExitAsync(ct).ConfigureAwait(false);
            if (firewall.ExitCode != 0)
                throw new InvalidOperationException(
                    "O firewall terminou com codigo " + firewall.ExitCode + ".");
        }
    }

    /// <summary>Separado da elevacao para poder validar a regra em testes.</summary>
    public static string BuildPowerShellScript(string appPath,
                                                bool replicaExists,
                                                bool voiceExists)
    {
        static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
        var commands = new List<string> { "$ErrorActionPreference='Stop'" };
        if (!replicaExists)
            commands.Add("New-NetFirewallRule -DisplayName " + Ps(ReplicaRuleName)
                       + " -Direction Inbound -Action Allow -Enabled True -Profile Any"
                       + " -Protocol TCP -LocalPort 8765 -RemoteAddress "
                       + Ps(TailscaleRange) + " | Out-Null");
        if (!voiceExists)
            commands.Add("New-NetFirewallRule -DisplayName " + Ps(VoiceRuleName)
                       + " -Direction Inbound -Action Allow -Enabled True -Profile Any"
                       + " -Program " + Ps(Path.GetFullPath(appPath))
                       + " -Protocol UDP -RemoteAddress " + Ps(TailscaleRange) + " | Out-Null");
        return string.Join(";", commands);
    }

    private static async Task<bool> RuleExistsAsync(string name, CancellationToken ct)
    {
        using var check = Process.Start(new ProcessStartInfo("netsh.exe")
        {
            Arguments = $"advfirewall firewall show rule name=\"{name}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (check == null) return false;
        string output = await check.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await check.WaitForExitAsync(ct).ConfigureAwait(false);
        return check.ExitCode == 0
            && output.Contains(name, StringComparison.OrdinalIgnoreCase);
    }
}
