using System.Diagnostics;
using Primicord.SetupShared;

namespace Primicord;

/// <summary>
/// Gera, somente no PC do administrador, uma copia privada do Setup publico.
/// Auth key e senha nunca saem daqui nem entram no repositorio de releases.
/// </summary>
public sealed class GroupInstallerBuilderDialog : Form
{
    private readonly Config _cfg;
    private readonly TextBox _server = Field();
    private readonly TextBox _authKey = Field(secret: true);
    private readonly TextBox _password = Field(secret: true);
    private readonly TextBox _confirm = Field(secret: true);
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly PrimButton _create;
    private bool _busy;

    public GroupInstallerBuilderDialog(Config cfg)
    {
        _cfg = cfg;
        Text = "INSTALADOR PRIVADO DO GRUPO";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(570, 610);
        BackColor = Pv.Char2;
        ForeColor = Pv.Bone;
        Font = Pv.Body;

        var title = new Label
        {
            Text = "CONVIDAR O GRUPO", Font = Pv.Display, ForeColor = Pv.Orange,
            Location = new Point(28, 24), AutoSize = true,
        };
        var intro = new Label
        {
            Text = "Este gerador cria um unico Setup protegido por senha para ate o grupo\n"
                 + "inteiro. Ele instala o Tailscale, entra na tailnet e configura as salas.\n"
                 + "O segredo fica cifrado dentro do arquivo e nao e enviado ao GitHub.",
            ForeColor = Pv.BoneDim, Location = new Point(28, 66), Size = new Size(514, 66),
        };

        var serverLabel = Section("MINI SERVIDOR", 144);
        _server.Location = new Point(28, 166);
        _server.Text = cfg.CoordServerUrl;

        var keyLabel = Section("AUTH KEY REUTILIZAVEL DO TAILSCALE", 212);
        _authKey.Location = new Point(28, 234);
        _authKey.PlaceholderText = "tskey-auth-...";

        var keyHelp = new Label
        {
            Text = "Na tela de chaves: marque Reusable, nao marque Ephemeral e use o\n"
                 + "maior prazo necessario. Depois que todos instalarem, revogue a chave.",
            ForeColor = Pv.BoneDim, Location = new Point(28, 272), Size = new Size(514, 44),
        };
        var openKeys = new PrimButton("ABRIR CHAVES DO TAILSCALE", PrimButton.Style.Ghost)
        { Location = new Point(28, 320), Size = new Size(250, 36) };
        openKeys.Click += (_, _) => Process.Start(new ProcessStartInfo(
            "https://login.tailscale.com/admin/settings/keys") { UseShellExecute = true });

        var passwordLabel = Section("SENHA COMPARTILHADA (MINIMO 10 CARACTERES)", 372);
        _password.Location = new Point(28, 394);
        var confirmLabel = Section("CONFIRMAR SENHA", 440);
        _confirm.Location = new Point(28, 462);

        _progress.Location = new Point(28, 510);
        _progress.Size = new Size(514, 8);
        _progress.Style = ProgressBarStyle.Continuous;
        _status.Location = new Point(28, 524);
        _status.Size = new Size(514, 30);
        _status.ForeColor = Pv.BoneDim;
        _status.Text = "A senha e a auth key nao serao salvas neste PC.";

        _create = new PrimButton("GERAR INSTALADOR")
        { Location = new Point(28, 560), Size = new Size(250, 40) };
        _create.Click += async (_, _) => await CreateAsync();
        var cancel = new PrimButton("FECHAR", PrimButton.Style.Ghost)
        { Location = new Point(292, 560), Size = new Size(250, 40) };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, intro, serverLabel, _server, keyLabel, _authKey, keyHelp, openKeys,
            passwordLabel, _password, confirmLabel, _confirm, _progress, _status,
            _create, cancel,
        });

        Shown += async (_, _) =>
        {
            if (!cfg.HostMiniServer) return;
            var tailscale = await TailscaleIntegration.GetStatusAsync();
            if (!IsDisposed && tailscale.Connected && tailscale.Address.Length > 0)
                _server.Text = Config.NormalizeCoordServerUrl(tailscale.Address);
        };
    }

    private async Task CreateAsync()
    {
        if (_busy) return;
        string password = _password.Text;
        if (password != _confirm.Text)
        {
            _status.Text = "As duas senhas nao conferem.";
            _confirm.Focus();
            return;
        }

        GroupInvite invite;
        try
        {
            invite = new GroupInvite(Config.NormalizeCoordServerUrl(_server.Text), _authKey.Text.Trim());
            // Valida antes de pedir o destino e baixar 100+ MB.
            ValidateFields(invite, password);
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
            return;
        }

        using var save = new SaveFileDialog
        {
            Title = "Salvar instalador privado do grupo",
            Filter = "Aplicativo do Windows (*.exe)|*.exe",
            FileName = "Primicord-Grupo-Setup.exe",
            InitialDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            OverwritePrompt = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        _busy = true;
        _create.Enabled = false;
        _progress.Value = 0;
        string tempDir = Path.Combine(Path.GetTempPath(), "Primicord", "group-installer");
        string genericSetup = Path.Combine(tempDir, "Primicord-public-Setup.exe");
        string privateSetup = Path.Combine(tempDir, "Primicord-private-Setup.exe");
        try
        {
            Directory.CreateDirectory(tempDir);
            _status.Text = "Baixando o instalador publico da versao mais recente...";
            await DownloadSetupAsync(genericSetup, new Progress<int>(p => _progress.Value = p));

            _status.Text = "Cifrando o convite privado...";
            await Task.Run(() => GroupInvitePackage.CreateInstaller(
                genericSetup, privateSetup, invite, password));
            File.Move(privateSetup, save.FileName, overwrite: true);
            _progress.Value = 100;
            _authKey.Clear();
            _password.Clear();
            _confirm.Clear();

            _status.Text = "Pronto. Envie o arquivo e a senha por caminhos diferentes.";
            MessageBox.Show(this,
                "Instalador privado criado.\n\n"
              + "Envie o arquivo para o grupo e passe a senha separadamente. "
              + "Depois que todos instalarem, revogue a auth key no Tailscale.",
                "PRIMICORD", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Write("instalador do grupo: " + ex);
            _status.Text = "Nao gerou: " + ex.Message;
        }
        finally
        {
            try { if (File.Exists(genericSetup)) File.Delete(genericSetup); } catch { }
            try { if (File.Exists(privateSetup)) File.Delete(privateSetup); } catch { }
            password = "";
            _busy = false;
            if (!IsDisposed) _create.Enabled = true;
        }
    }

    private static void ValidateFields(GroupInvite invite, string password)
    {
        if (!Uri.TryCreate(invite.ServerUrl, UriKind.Absolute, out var server)
            || server.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(server.Host))
            throw new ArgumentException("Informe o endereco HTTP do mini servidor.");
        if (!invite.AuthKey.StartsWith("tskey-auth-", StringComparison.Ordinal)
            || invite.AuthKey.Length < 30)
            throw new ArgumentException("Cole uma auth key valida do Tailscale.");
        if (password.Length < 10)
            throw new ArgumentException("Use uma senha com pelo menos 10 caracteres.");
    }

    private async Task DownloadSetupAsync(string destination, IProgress<int> progress)
    {
        string repo = string.IsNullOrWhiteSpace(_cfg.UpdateRepo) ? Updater.DefaultRepo : _cfg.UpdateRepo;
        string url = $"https://github.com/{repo}/releases/latest/download/Primicord-win-Setup.exe";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        if (total is > 0 and < 10_000_000)
            throw new InvalidDataException("O download do Setup veio incompleto.");

        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                                                FileShare.None, 128 * 1024, useAsync: true);
        byte[] buffer = new byte[128 * 1024];
        long copied = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
            copied += read;
            if (total > 0) progress.Report((int)Math.Clamp(copied * 100 / total, 0, 99));
        }
        if (copied < 10_000_000)
            throw new InvalidDataException("O download do Setup veio incompleto.");
    }

    private static TextBox Field(bool secret = false) => new()
    {
        Size = new Size(514, 34), BackColor = Pv.Charcoal, ForeColor = Pv.Bone,
        BorderStyle = BorderStyle.FixedSingle, Font = Pv.Body,
        UseSystemPasswordChar = secret,
    };

    private static Label Section(string text, int y) => new()
    {
        Text = text, Font = Pv.Label, ForeColor = Pv.Orange,
        Location = new Point(28, y), AutoSize = true,
    };
}
