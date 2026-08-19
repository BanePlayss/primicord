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
        Assert.Contains("-Protocol UDP", script);
        Assert.Contains("-Program '" + InstalledApp + "'", script);
        Assert.Equal(2, Count(script, "-RemoteAddress '" + NetworkFirewall.TailscaleRange + "'"));
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
