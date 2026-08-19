using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

namespace Primicord;

public sealed record TailscaleStatus(bool Installed, bool Connected, string Address,
                                     string DnsName, string BackendState)
{
    public string Description => !Installed ? "nao instalado"
        : Connected ? $"conectado — {(DnsName.Length > 0 ? DnsName : Address)}"
        : "instalado — falta entrar na tailnet";
}

public static class TailscaleIntegration
{
    public const string LatestMsiUrl =
        "https://pkgs.tailscale.com/stable/tailscale-setup-latest-amd64.msi";

    public static string? CliPath => Find("tailscale.exe");
    public static string? GuiPath => Find("tailscale-ipn.exe");
    public static bool IsInstalled => CliPath != null
        || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                         "Tailscale"));

    public static bool IsTailnetAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    public static async Task<TailscaleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        string? cli = CliPath;
        if (cli == null) return new TailscaleStatus(false, false, "", "", "NoState");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(cli, "status --json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return new TailscaleStatus(true, false, "", "", "Unknown");
            string json = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            string state = root?["BackendState"]?.GetValue<string>() ?? "Unknown";
            string address = root?["TailscaleIPs"]?[0]?.GetValue<string>() ?? "";
            string dns = root?["Self"]?["DNSName"]?.GetValue<string>()?.TrimEnd('.') ?? "";
            return new TailscaleStatus(true, state.Equals("Running", StringComparison.OrdinalIgnoreCase),
                                       address, dns, state);
        }
        catch (Exception ex)
        {
            Log.Write("tailscale: status falhou: " + ex.Message);
            return new TailscaleStatus(true, false, "", "", "Unknown");
        }
    }

    public static async Task InstallLatestAsync(IProgress<int>? progress = null,
                                                 CancellationToken ct = default)
    {
        if (IsInstalled) return;
        string dir = Path.Combine(Path.GetTempPath(), "Primicord", "prerequisites");
        Directory.CreateDirectory(dir);
        string msi = Path.Combine(dir, "tailscale-latest-amd64.msi");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(LatestMsiUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            if (total is > 0 and < 1_000_000)
                throw new InvalidDataException("O download oficial do Tailscale veio incompleto.");

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(msi, FileMode.Create, FileAccess.Write, FileShare.None,
                                                     128 * 1024, useAsync: true))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    if (total > 0) progress?.Report((int)Math.Clamp(copied * 100 / total, 0, 100));
                }
            }

            var start = new ProcessStartInfo("msiexec.exe")
            {
                Arguments = $"/i \"{msi}\" /passive /norestart TS_INSTALLUPDATES=always",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
            };
            using var installer = Process.Start(start)
                ?? throw new InvalidOperationException("O Windows nao abriu o instalador do Tailscale.");
            await installer.WaitForExitAsync(ct).ConfigureAwait(false);
            if (installer.ExitCode is not (0 or 1641 or 3010))
                throw new InvalidOperationException($"O instalador do Tailscale terminou com codigo {installer.ExitCode}.");

            progress?.Report(100);
            OpenClient();
        }
        finally
        {
            try { if (File.Exists(msi)) File.Delete(msi); } catch { }
        }
    }

    public static void OpenClient()
    {
        string? gui = GuiPath;
        if (gui != null)
        {
            Process.Start(new ProcessStartInfo(gui) { UseShellExecute = true });
            return;
        }

        // Plano B: a pagina explica o login pelo icone da bandeja.
        Process.Start(new ProcessStartInfo("https://tailscale.com/docs/install/windows")
        { UseShellExecute = true });
    }

    private static string? Find(string file)
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            string path = Path.Combine(root, "Tailscale", file);
            if (File.Exists(path)) return path;
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        foreach (string dir in (pathEnv ?? "").Split(Path.PathSeparator,
                 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return path;
            }
            catch { }
        }
        return null;
    }
}

/// <summary>Onboarding usado por quem chega pela atualizacao diferencial.</summary>
public sealed class TailscaleSetupDialog : Form
{
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly PrimButton _install;
    private readonly PrimButton _later;
    private bool _busy;

    public TailscaleSetupDialog()
    {
        Text = "REDE PRIVADA DO PRIMICORD";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 285);
        BackColor = Pv.Char2;
        ForeColor = Pv.Bone;

        var title = new Label
        {
            Text = "TAILSCALE", Font = Pv.Display, ForeColor = Pv.Orange,
            Location = new Point(28, 24), AutoSize = true,
        };
        var body = new Label
        {
            Text = "O Primicord usa uma rede privada para ligar os PCs diretamente e\n"
                 + "tirar salas, presenca, chat e sinalizacao do Firestore.\n\n"
                 + "A instalacao pede permissao do Windows. Depois, entre na tailnet\n"
                 + "pelo icone do Tailscale ao lado do relogio.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(28, 68),
            Size = new Size(444, 92),
        };

        _status.Location = new Point(28, 164);
        _status.Size = new Size(444, 22);
        _status.ForeColor = Pv.Bone;
        _status.Text = TailscaleIntegration.IsInstalled ? "Tailscale ja esta instalado." : "Pronto para instalar.";

        _progress.Location = new Point(28, 192);
        _progress.Size = new Size(444, 9);
        _progress.Style = ProgressBarStyle.Continuous;

        _install = new PrimButton(TailscaleIntegration.IsInstalled ? "ABRIR TAILSCALE" : "INSTALAR TAILSCALE")
        { Location = new Point(28, 222), Size = new Size(258, 40) };
        _install.Click += async (_, _) => await InstallOrOpenAsync();
        _later = new PrimButton("AGORA NAO", PrimButton.Style.Ghost)
        { Location = new Point(298, 222), Size = new Size(174, 40) };
        _later.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { title, body, _status, _progress, _install, _later });
    }

    private async Task InstallOrOpenAsync()
    {
        if (_busy) return;
        if (TailscaleIntegration.IsInstalled)
        {
            TailscaleIntegration.OpenClient();
            Close();
            return;
        }

        _busy = true;
        _install.Enabled = false;
        _later.Enabled = false;
        try
        {
            var progress = new Progress<int>(p =>
            {
                _progress.Value = Math.Clamp(p, 0, 100);
                _status.Text = $"Baixando do site oficial... {p}%";
            });
            await TailscaleIntegration.InstallLatestAsync(progress);
            _status.Text = "Instalado. Entre na tailnet pelo icone ao lado do relogio.";
            _install.Text = "ABRIR TAILSCALE";
            _install.Enabled = true;
            _later.Text = "FECHAR";
            _later.Enabled = true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _status.Text = "A permissao de administrador foi cancelada.";
            _install.Enabled = true;
            _later.Enabled = true;
        }
        catch (Exception ex)
        {
            Log.Write("tailscale: instalacao falhou: " + ex);
            _status.Text = "Nao instalou: " + ex.Message;
            _install.Enabled = true;
            _later.Enabled = true;
        }
        finally { _busy = false; }
    }
}
