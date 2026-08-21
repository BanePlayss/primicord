using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Primicord.SetupShared;

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

        string ownPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("O Windows nao informou o caminho do instalador.");
        GroupInvite? invite = null;
        if (GroupInvitePackage.HasInvite(ownPath))
        {
            invite = AskAndUnlockInvite(ownPath);
            if (invite == null) return 2;
            MessageBoxW(IntPtr.Zero,
                "Convite aceito. Agora o Primicord vai:\n\n"
              + "1. instalar o Tailscale, se necessario;\n"
              + "2. conectar este PC a rede privada do grupo;\n"
              + "3. configurar as salas e a replica automaticamente.\n\n"
              + "O Windows pode pedir permissao de administrador.",
                "PRIMICORD 0.7.1 — GRUPO", Ok | IconInfo);
        }
        else
        {
            string? configured = ReadConfiguredServerUrl();
            if (configured != null && await ServerReachableAsync(configured))
            {
                invite = new GroupInvite(configured, "");
            }
            else
            {
                invite = await AskAndActivateAsync();
                if (invite == null) return 2;
                MessageBoxW(IntPtr.Zero,
                    "Codigo aceito. Agora o Primicord vai:\n\n"
                  + "1. instalar o Tailscale, se necessario;\n"
                  + "2. conectar este PC a rede privada do grupo;\n"
                  + "3. configurar as salas e a replica automaticamente.\n\n"
                  + "O Windows pode pedir permissao de administrador.",
                    "PRIMICORD 0.7.1 — GRUPO", Ok | IconInfo);
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "PrimicordSetup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            if (!TailscaleInstalled())
            {
                try { await InstallTailscaleAsync(tempDir); }
                catch (Exception ex)
                {
                    if (invite != null)
                        throw new InvalidOperationException("O convite nao conseguiu instalar o Tailscale. "
                                                          + ex.Message, ex);

                    int answer = MessageBoxW(IntPtr.Zero,
                        "O Tailscale nao foi instalado:\n\n" + ex.Message
                      + "\n\nContinuar instalando o Primicord com o modo de compatibilidade?",
                        "PRIMICORD — TAILSCALE", YesNo | IconWarning);
                    if (answer != Yes) return 2;
                }
            }

            if (invite != null)
            {
                if (!string.IsNullOrEmpty(invite.AuthKey))
                    await ConnectTailscaleAsync(invite, tempDir);
                WritePrimicordConfig(invite.ServerUrl);
            }

            string setup = Path.Combine(tempDir, "Primicord-win-Setup.exe");
            await ExtractResourceAsync("Primicord.Velopack.Setup.exe", setup);
            using var process = Process.Start(new ProcessStartInfo(setup)
            {
                UseShellExecute = true,
                WorkingDirectory = tempDir,
            }) ?? throw new InvalidOperationException("O Windows nao abriu o instalador do Primicord.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) return process.ExitCode;

            // O executavel ja existe no caminho definitivo. Uma unica elevacao
            // cria tanto a regra do mini servidor quanto a rota UDP dinamica da
            // voz, que antes ficava esquecida e produzia o estado "SEM ROTA".
            string appPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Primicord", "current", "Primicord.exe");
            try { await NetworkFirewall.RepairAsync(appPath); }
            catch (Exception ex)
            {
                MessageBoxW(IntPtr.Zero,
                    "O Primicord foi instalado, mas as regras de rede nao foram criadas:\n\n"
                  + ex.Message
                  + "\n\nExecute o instalador novamente e aceite a permissao de administrador.",
                    "PRIMICORD — FIREWALL", Ok | IconWarning);
            }
            return 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(IntPtr.Zero, ex.Message, "PRIMICORD — INSTALACAO", Ok | IconWarning);
            return 1;
        }
        finally
        {
            invite = null;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<GroupInvite?> AskAndActivateAsync()
    {
        string installId = Guid.NewGuid().ToString();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!PromptGroupCode(attempt > 0, out string code)) return null;
            try
            {
                var activation = await GroupActivationClient.ActivateAsync(
                    http, GroupActivationDefaults.Endpoint, code, installId);
                return new GroupInvite(activation.ServerUrl, activation.AuthKey);
            }
            catch (GroupActivationException ex) when (ex.ErrorCode == "invalid_code")
            {
                // Repete o prompt sem mostrar detalhes internos do ativador.
            }
            catch (GroupActivationException ex)
            {
                MessageBoxW(IntPtr.Zero, ex.Message,
                    "PRIMICORD — ATIVACAO", Ok | IconWarning);
                return null;
            }
            finally { code = ""; }
        }
        MessageBoxW(IntPtr.Zero,
            "O codigo foi recusado cinco vezes. Peca o codigo novamente ao administrador do grupo.",
            "PRIMICORD — CODIGO", Ok | IconWarning);
        return null;
    }

    private static GroupInvite? AskAndUnlockInvite(string ownPath)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (!PromptPassword(attempt > 0, out string password)) return null;
            try { return GroupInvitePackage.ReadInstaller(ownPath, password); }
            catch (CryptographicException) { }
            catch (InvalidDataException) { }
            finally { password = ""; }
        }
        MessageBoxW(IntPtr.Zero,
            "A senha foi recusada cinco vezes. Peca a senha novamente ao administrador do grupo.",
            "PRIMICORD — SENHA", Ok | IconWarning);
        return null;
    }

    private static async Task ConnectTailscaleAsync(GroupInvite invite, string tempDir)
    {
        // Se este PC ja esta na tailnet certa, nao consome nem expoe a auth key.
        if (await ServerReachableAsync(invite.ServerUrl)) return;

        string? cli = null;
        for (int i = 0; i < 20 && cli == null; i++)
        {
            cli = FindTailscaleCli();
            if (cli == null) await Task.Delay(500);
        }
        if (cli == null)
            throw new InvalidOperationException("O Tailscale foi instalado, mas o comando tailscale.exe nao apareceu.");

        string keyFile = Path.Combine(tempDir, ".primicord-auth-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(keyFile, invite.AuthKey);
        try
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var start = new ProcessStartInfo(cli)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };
                    // login adiciona/ativa o perfil da tailnet sem apagar perfis que o
                    // usuario ja possua. file: evita colocar o segredo na command line.
                    start.ArgumentList.Add("login");
                    start.ArgumentList.Add("--auth-key=file:" + keyFile);
                    using var process = Process.Start(start)
                        ?? throw new InvalidOperationException("O Windows nao abriu o comando do Tailscale.");
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); }
                    catch (TimeoutException)
                    {
                        try { process.Kill(entireProcessTree: true); } catch { }
                        throw new InvalidOperationException("O Tailscale demorou demais para autenticar.");
                    }
                    string stdout = await stdoutTask;
                    string stderr = await stderrTask;
                    if (process.ExitCode != 0)
                        throw new InvalidOperationException((stderr + " " + stdout).Trim());

                    await Task.Delay(1_000);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    await Task.Delay(1_500);
                }
            }
            throw new InvalidOperationException("Nao foi possivel entrar na tailnet do grupo. "
                                              + last?.Message, last);
        }
        finally
        {
            try
            {
                if (File.Exists(keyFile))
                {
                    byte[] zeros = new byte[new FileInfo(keyFile).Length];
                    await File.WriteAllBytesAsync(keyFile, zeros);
                    File.Delete(keyFile);
                }
            }
            catch { }
        }
    }

    private static async Task<bool> ServerReachableAsync(string serverUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string health = serverUrl.TrimEnd('/') + "/health";
            using var response = await http.GetAsync(health);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static void WritePrimicordConfig(string serverUrl)
    {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Primicord");
        Directory.CreateDirectory(dataDir);
        string path = Path.Combine(dataDir, "config.txt");
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        lines.RemoveAll(line => line.StartsWith("coordserver=", StringComparison.OrdinalIgnoreCase)
                             || line.StartsWith("hostserver=", StringComparison.OrdinalIgnoreCase));
        lines.Add("coordserver=" + serverUrl.TrimEnd('/'));
        lines.Add("hostserver=1"); // 0.6.14+: todo participante mantem uma replica

        string temp = path + ".new";
        File.WriteAllLines(temp, lines);
        File.Move(temp, path, overwrite: true);
    }

    private static string? ReadConfiguredServerUrl()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Primicord", "config.txt");
            if (!File.Exists(path)) return null;
            string? line = File.ReadLines(path).LastOrDefault(value =>
                value.StartsWith("coordserver=", StringComparison.OrdinalIgnoreCase));
            string value = line?[(line.IndexOf('=') + 1)..].Trim() ?? "";
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                   && uri.Scheme == Uri.UriSchemeHttp && uri.Port == 8765
                ? value.TrimEnd('/') : null;
        }
        catch { return null; }
    }

    private static bool TailscaleInstalled() => FindTailscaleCli() != null
        || Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale"));

    private static string? FindTailscaleCli()
    {
        foreach (string root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            string path = Path.Combine(root, "Tailscale", "tailscale.exe");
            if (File.Exists(path)) return path;
        }
        return null;
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

    private static bool PromptPassword(bool wrongPassword, out string password)
    {
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            Caption = "PRIMICORD — INSTALADOR DO GRUPO",
            Message = wrongPassword
                ? "Senha incorreta. Digite novamente a senha compartilhada."
                : "Digite a senha compartilhada do instalador. O campo Usuario identifica apenas o grupo.",
        };
        var user = new StringBuilder("Grupo Primicord", 128);
        var secret = new StringBuilder(256);
        bool save = false;
        uint result = CredUIPromptForCredentialsW(ref info, "Primicord", IntPtr.Zero, 0,
            user, user.Capacity, secret, secret.Capacity, ref save,
            0x00040000 /* GENERIC_CREDENTIALS */ |
            0x00000080 /* ALWAYS_SHOW_UI */ |
            0x00000002 /* DO_NOT_PERSIST */ |
            0x00100000 /* KEEP_USERNAME */);
        password = result == 0 ? secret.ToString() : "";
        secret.Clear();
        return result == 0;
    }

    private static bool PromptGroupCode(bool wrongCode, out string code)
    {
        var info = new CredUiInfo
        {
            Size = Marshal.SizeOf<CredUiInfo>(),
            Caption = "PRIMICORD — ENTRAR NO GRUPO",
            Message = wrongCode
                ? "Codigo incorreto. Digite novamente o codigo enviado pelo administrador."
                : "Digite o codigo do grupo. O mesmo Setup funciona para todos os participantes.",
        };
        var user = new StringBuilder("Grupo Primicord", 128);
        var secret = new StringBuilder(128);
        bool save = false;
        uint result = CredUIPromptForCredentialsW(ref info, "PrimicordActivation", IntPtr.Zero, 0,
            user, user.Capacity, secret, secret.Capacity, ref save,
            0x00040000 /* GENERIC_CREDENTIALS */ |
            0x00000080 /* ALWAYS_SHOW_UI */ |
            0x00000002 /* DO_NOT_PERSIST */ |
            0x00100000 /* KEEP_USERNAME */);
        code = result == 0 ? secret.ToString() : "";
        secret.Clear();
        return result == 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredUiInfo
    {
        public int Size;
        public IntPtr Parent;
        [MarshalAs(UnmanagedType.LPWStr)] public string Message;
        [MarshalAs(UnmanagedType.LPWStr)] public string Caption;
        public IntPtr Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern uint CredUIPromptForCredentialsW(
        ref CredUiInfo info, string targetName, IntPtr reserved, uint authError,
        StringBuilder userName, int userNameMaxChars,
        StringBuilder password, int passwordMaxChars,
        ref bool save, uint flags);
}
