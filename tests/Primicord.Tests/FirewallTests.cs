using Primicord.SetupShared;
using Xunit;

namespace Primicord.Tests;

public sealed class FirewallTests
{
    private const string InstalledApp =
        @"C:\Users\bane\AppData\Local\Primicord\current\Primicord.exe";

    [Fact]
    public void Plano_libera_servidor_e_voz_somente_no_tailscale()
    {
        string script = NetworkFirewall.BuildPowerShellScript(
            InstalledApp, replicaExists: false, voiceExists: false);

        Assert.Contains(NetworkFirewall.ReplicaRuleName, script);
        Assert.Contains("-Protocol TCP -LocalPort 8765", script);
        Assert.Contains(NetworkFirewall.VoiceRuleName, script);
        Assert.Contains("-Direction Inbound", script);
        Assert.Contains("-EdgeTraversalPolicy Allow", script);
        Assert.Contains(NetworkFirewall.VoiceOutboundRuleName, script);
        Assert.Contains("-Direction Outbound", script);
        Assert.Equal(2, Count(script, "-Protocol UDP"));
        Assert.Contains("-Program '" + InstalledApp + "'", script);
        Assert.Equal(3, Count(script, "-RemoteAddress '" + NetworkFirewall.TailscaleRange + "'"));
        Assert.DoesNotContain("-RemoteAddress 'Any'", script);
    }

    [Fact]
    public void Plano_nao_recria_regra_que_ja_existe()
    {
        string script = NetworkFirewall.BuildPowerShellScript(
            InstalledApp, replicaExists: true, voiceExists: false);

        Assert.DoesNotContain(NetworkFirewall.ReplicaRuleName, script);
        Assert.Contains(NetworkFirewall.VoiceRuleName, script);
    }

    [Fact]
    public void Reparo_remove_regras_antigas_antes_de_recriar_o_conjunto_completo()
    {
        string script = NetworkFirewall.BuildRepairPowerShellScript(InstalledApp);

        Assert.Equal(3, Count(script, "Remove-NetFirewallRule"));
        Assert.Contains(NetworkFirewall.ReplicaRuleName, script);
        Assert.Contains(NetworkFirewall.VoiceRuleName, script);
        Assert.Contains(NetworkFirewall.VoiceOutboundRuleName, script);
        Assert.Equal(3, Count(script, "New-NetFirewallRule"));
    }

    [Fact]
    public void Configuracoes_expoem_reparo_manual_do_firewall()
    {
        using var dialog = new SettingsDialog(new Config());

        var button = Assert.IsType<PrimButton>(
            Assert.Single(dialog.Controls.Find("repairFirewallButton", true)));
        Assert.Equal("REPARAR FIREWALL", button.Text);
        Assert.Contains("Firewall", button.AccessibleName);
    }

    private static int Count(string value, string fragment)
    {
        int total = 0;
        int offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            total++;
            offset += fragment.Length;
        }
        return total;
    }
}
