using System.Security.Cryptography;
using System.Diagnostics;
using Primicord.SetupShared;
using Xunit;

namespace Primicord.Tests;

public sealed class GroupInvitePackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "PrimicordInviteTests-" + Guid.NewGuid().ToString("N"));

    public GroupInvitePackageTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void CriaELiberaConviteComSenhaCorreta()
    {
        string source = CreateBaseInstaller();
        string privateSetup = Path.Combine(_dir, "grupo.exe");
        var expected = new GroupInvite("http://100.64.2.73:8765",
            "tskey-auth-abc123456789012345678901234567890");

        GroupInvitePackage.CreateInstaller(source, privateSetup, expected, "senha-forte-do-grupo");

        Assert.True(GroupInvitePackage.HasInvite(privateSetup));
        Assert.Equal(expected,
            GroupInvitePackage.ReadInstaller(privateSetup, "senha-forte-do-grupo"));
        Assert.False(GroupInvitePackage.HasInvite(source));
    }

    [Fact]
    public void SenhaErradaNaoRevelaConvite()
    {
        string source = CreateBaseInstaller();
        string privateSetup = Path.Combine(_dir, "grupo.exe");
        var invite = new GroupInvite("http://primicord-server:8765",
            "tskey-auth-abc123456789012345678901234567890");
        GroupInvitePackage.CreateInstaller(source, privateSetup, invite, "senha-forte-do-grupo");

        Assert.ThrowsAny<CryptographicException>(() =>
            GroupInvitePackage.ReadInstaller(privateSetup, "outra-senha-qualquer"));
    }

    [Fact]
    public void RecusaChaveOuSenhaFraca()
    {
        string source = CreateBaseInstaller();
        string destination = Path.Combine(_dir, "grupo.exe");

        Assert.Throws<ArgumentException>(() => GroupInvitePackage.CreateInstaller(source, destination,
            new GroupInvite("http://100.64.2.73:8765", "nao-e-chave"), "senha-forte-do-grupo"));
        Assert.Throws<ArgumentException>(() => GroupInvitePackage.CreateInstaller(source, destination,
            new GroupInvite("http://100.64.2.73:8765",
                "tskey-auth-abc123456789012345678901234567890"), "curta"));
    }

    [Fact]
    public void SetupRealContinuaExecutavelDepoisDoConviteSerAnexado()
    {
        string source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "Releases", "Primicord-win-Setup.exe"));
        if (!File.Exists(source)) return; // build unitario sem empacotamento previo

        string privateSetup = Path.Combine(_dir, "setup-real-privado.exe");
        GroupInvitePackage.CreateInstaller(source, privateSetup,
            new GroupInvite("http://100.64.2.73:8765",
                "tskey-auth-abc123456789012345678901234567890"),
            "senha-forte-do-grupo");

        using var process = Process.Start(new ProcessStartInfo(privateSetup, "--verify")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }

    private string CreateBaseInstaller()
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".exe");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        file.SetLength(1_100_000);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
