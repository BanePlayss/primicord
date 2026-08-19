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
    public const string VoiceOutboundRuleName = "Primicord Voice Out (Tailscale)";
    public const string TailscaleRange = "100.64.0.0/10";

    public static async Task EnsureAsync(string appPath, CancellationToken ct = default)
    {
        bool replicaExists = await RuleExistsAsync(ReplicaRuleName, ct).ConfigureAwait(false);
        bool voiceExists = await RuleExistsAsync(VoiceRuleName, ct).ConfigureAwait(false);
        bool voiceOutboundExists = await RuleExistsAsync(VoiceOutboundRuleName, ct)
            .ConfigureAwait(false);
        if (replicaExists && voiceExists && voiceOutboundExists) return;

        appPath = ValidateAppPath(appPath);
        await RunElevatedAsync(BuildPowerShellScript(
            appPath, replicaExists, voiceExists, voiceOutboundExists), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Remove regras antigas com os nomes do Primicord e as recria por completo.
    /// Usado pelo botao manual quando uma instalacao anterior deixou regra parcial.
    /// </summary>
    public static Task RepairAsync(string appPath, CancellationToken ct = default)
        => RunElevatedAsync(BuildRepairPowerShellScript(ValidateAppPath(appPath)), ct);

    private static string ValidateAppPath(string appPath)
    {
        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(appPath))
            throw new InvalidOperationException(
                "O executavel instalado nao foi encontrado em " + appPath);
        return appPath;
    }

    private static async Task RunElevatedAsync(string script, CancellationToken ct)
    {
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
                                                bool voiceExists,
                                                bool voiceOutboundExists = false)
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
                       + " -Protocol UDP -RemoteAddress " + Ps(TailscaleRange)
                       + " -EdgeTraversalPolicy Allow | Out-Null");
        if (!voiceOutboundExists)
            commands.Add("New-NetFirewallRule -DisplayName " + Ps(VoiceOutboundRuleName)
                       + " -Direction Outbound -Action Allow -Enabled True -Profile Any"
                       + " -Program " + Ps(Path.GetFullPath(appPath))
                       + " -Protocol UDP -RemoteAddress " + Ps(TailscaleRange) + " | Out-Null");
        return string.Join(";", commands);
    }

    public static string BuildRepairPowerShellScript(string appPath)
    {
        static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
        string remove = string.Join(";", new[]
        {
            ReplicaRuleName,
            VoiceRuleName,
            VoiceOutboundRuleName,
        }.Select(name => "Remove-NetFirewallRule -DisplayName " + Ps(name)
                       + " -ErrorAction SilentlyContinue"));
        return "$ErrorActionPreference='Stop';" + remove + ";"
             + BuildPowerShellScript(appPath, false, false, false);
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
