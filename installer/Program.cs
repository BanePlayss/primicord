using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Primicord.Installer;

internal static class Program
{
    private const string TailscaleMsi =
        "https://pkgs.tailscale.com/stable/tailscale-setup-latest-amd64.msi";
    private const uint Ok = 0x00000000;
    private const uint YesNo = 0x00000004;
    private const uint IconInfo = 0x00000040;
    private const uint IconWarning = 0x00000030;
    private const int Yes = 6;

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--verify", StringComparer.OrdinalIgnoreCase))
        {
            using Stream? embedded = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Primicord.Velopack.Setup.exe");
            return embedded is { Length: > 10_000_000 } ? 0 : 3;
        }

        MessageBoxW(IntPtr.Zero,
            "O Primicord 0.6.12 usa o Tailscale para criar a rede privada do grupo.\n\n"
          + "Se ele ainda nao estiver instalado, este assistente baixa o MSI oficial, "
          + "pede permissao do Windows e depois instala o Primicord. Nenhuma chave "
          + "da tailnet fica dentro do instalador.",
            "PRIMICORD 0.6.12", Ok | IconInfo);

        string tempDir = Path.Combine(Path.GetTempPath(), "PrimicordSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            if (!TailscaleInstalled())
            {
                try { await InstallTailscaleAsync(tempDir); }
                catch (Exception ex)
                {
                    int answer = MessageBoxW(IntPtr.Zero,
                        "O Tailscale nao foi instalado:\n\n" + ex.Message
                      + "\n\nContinuar instalando o Primicord com o modo de compatibilidade?",
                        "PRIMICORD — TAILSCALE", YesNo | IconWarning);
                    if (answer != Yes) return 2;
                }
            }

            string setup = Path.Combine(tempDir, "Primicord-win-Setup.exe");
            await ExtractResourceAsync("Primicord.Velopack.Setup.exe", setup);
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                WorkingDirectory = tempDir,
            }) ?? throw new InvalidOperationException("O Windows nao abriu o instalador do Primicord.");
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, ex.Message, "PRIMICORD — INSTALACAO", Ok | IconWarning);
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static bool TailscaleInstalled()
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
            if (File.Exists(Path.Combine(root, "Tailscale", "tailscale.exe"))) return true;
        return false;
    }

    private static async Task InstallTailscaleAsync(string tempDir)
    {
        string msi = Path.Combine(tempDir, "tailscale-latest-amd64.msi");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        using (var response = await http.GetAsync(TailscaleMsi, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long size = response.Content.Headers.ContentLength ?? 0;
            if (size is > 0 and < 1_000_000)
                throw new InvalidDataException("O download oficial veio incompleto.");
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = new FileStream(msi, FileMode.Create, FileAccess.Write, FileShare.None,
                                                    128 * 1024, useAsync: true);
            await input.CopyToAsync(output);
        }

        Process? installer;
        try
        {
            installer = Process.Start(new ProcessStartInfo("msiexec.exe")
            {
                Arguments = $"/i \"{msi}\" /passive /norestart TS_INSTALLUPDATES=always",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("A permissao de administrador foi cancelada.", ex);
        }

        using (installer)
        {
            if (installer == null) throw new InvalidOperationException("O Windows nao abriu o MSI do Tailscale.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode is not (0 or 1641 or 3010))
                throw new InvalidOperationException("O MSI do Tailscale terminou com codigo " + installer.ExitCode + ".");
        }
    }

    private static async Task ExtractResourceAsync(string resourceName, string destination)
    {
        await using Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("O instalador interno do Primicord nao foi empacotado.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                                                FileShare.None, 128 * 1024, useAsync: true);
        await input.CopyToAsync(output);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
