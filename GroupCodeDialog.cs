using Primicord.SetupShared;

namespace Primicord;

/// <summary>
/// Entrada explicita no grupo para quem recebeu o app por atualizacao, copiou o
/// portatil ou ja tinha o Primicord instalado antes do Setup universal.
/// </summary>
public sealed class GroupCodeDialog : Form
{
    private readonly Config _cfg;
    private readonly TextBox _code = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly PrimButton _enter;
    private readonly PrimButton _cancel;
    private bool _busy;

    public GroupCodeDialog(Config cfg)
    {
        _cfg = cfg;
        Text = "ENTRAR NO GRUPO PRIMICORD";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 340);
        BackColor = Pv.Char2;
        ForeColor = Pv.Bone;
        Font = Pv.Body;

        var title = new Label
        {
            Text = "CÓDIGO DO GRUPO", Font = Pv.DisplaySm, ForeColor = Pv.Orange,
            Location = new Point(28, 24), AutoSize = true,
        };
        var body = new Label
        {
            Text = "Digite o código curto enviado pelo administrador. O Primicord\n"
                 + "entra na rede privada e encontra as salas automaticamente.",
            Font = Pv.Body, ForeColor = Pv.BoneDim, Location = new Point(28, 62),
            Size = new Size(464, 48),
        };
        var label = new Label
        {
            Text = "CÓDIGO", Font = Pv.Label, ForeColor = Pv.Orange,
            Location = new Point(28, 118), AutoSize = true,
        };

        _code.Location = new Point(28, 140);
        _code.Size = new Size(464, 34);
        _code.BackColor = Pv.Charcoal;
        _code.ForeColor = Pv.Bone;
        _code.BorderStyle = BorderStyle.FixedSingle;
        _code.Font = new Font("Consolas", 12f, FontStyle.Bold);
        _code.CharacterCasing = CharacterCasing.Upper;
        _code.MaxLength = 96;
        _code.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await ActivateAsync();
        };

        _status.Location = new Point(28, 186);
        _status.Size = new Size(464, 48);
        _status.ForeColor = Pv.BoneDim;
        _status.Text = "O código não fica salvo neste PC.";

        _progress.Location = new Point(28, 240);
        _progress.Size = new Size(464, 8);
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.Visible = false;

        _enter = new PrimButton("ENTRAR NO GRUPO")
        { Location = new Point(28, 272), Size = new Size(286, 42) };
        _enter.Click += async (_, _) => await ActivateAsync();
        _cancel = new PrimButton("CANCELAR", PrimButton.Style.Ghost)
        { Location = new Point(326, 272), Size = new Size(166, 42) };
        _cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
            { title, body, label, _code, _status, _progress, _enter, _cancel });
        Shown += (_, _) => _code.Focus();
    }

    private async Task ActivateAsync()
    {
        if (_busy) return;
        string code = GroupActivationClient.NormalizeCode(_code.Text);
        if (code.Length < 4)
        {
            _status.ForeColor = Pv.Red;
            _status.Text = "Digite o código completo do grupo.";
            _code.Focus();
            return;
        }

        SetBusy(true, "Validando o código...");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            GroupActivation activation = await GroupActivationClient.ActivateAsync(
                http, GroupActivationDefaults.Endpoint, code, Guid.NewGuid().ToString());

            if (!TailscaleIntegration.IsInstalled)
            {
                SetStatus("Instalando o Tailscale...");
                var progress = new Progress<int>(p => SetStatus($"Baixando o Tailscale... {p}%"));
                await TailscaleIntegration.InstallLatestAsync(progress, openClient: false);
            }

            SetStatus("Conectando este PC à rede do grupo...");
            await TailscaleIntegration.ConnectWithAuthKeyAsync(activation.AuthKey);

            _cfg.CoordServerUrl = Config.NormalizeCoordServerUrl(activation.ServerUrl);
            _cfg.HostMiniServer = true;
            _cfg.Save();
            MiniServerProcess.Configure(enabled: true);

            SetStatus("Liberando servidor e voz somente na rede privada...");
            try { await TailscaleIntegration.EnsureNetworkFirewallAsync(); }
            catch (Exception ex)
            {
                // A adesao ao grupo continua valida, mas servidor e voz podem ficar
                // sem rota ate a permissao ser aceita pelo instalador completo.
                Log.Write("firewall apos codigo: " + ex.Message);
                MessageBox.Show(this,
                    "Este PC entrou no grupo, mas o Firewall do Windows nao foi configurado.\n\n"
                  + ex.Message
                  + "\n\nExecute o instalador completo e aceite a permissao de administrador.",
                    "PRIMICORD — FIREWALL",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }

            _status.ForeColor = Pv.Green;
            _status.Text = "Pronto: este PC entrou no grupo e já pode encontrar as salas.";
            await Task.Delay(700);
            DialogResult = DialogResult.OK;
        }
        catch (GroupActivationException ex) when (ex.ErrorCode == "invalid_code")
        {
            _status.ForeColor = Pv.Red;
            _status.Text = "Código incorreto. Confira e tente novamente.";
            _code.SelectAll();
            _code.Focus();
        }
        catch (Exception ex)
        {
            Log.Write("entrada por codigo falhou: " + ex);
            _status.ForeColor = Pv.Red;
            _status.Text = ex.Message;
        }
        finally
        {
            code = "";
            if (!IsDisposed) SetBusy(false, null);
        }
    }

    private void SetStatus(string text)
    {
        _status.ForeColor = Pv.Bone;
        _status.Text = text;
    }

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        _enter.Enabled = !busy;
        _cancel.Enabled = !busy;
        _code.Enabled = !busy;
        _progress.Visible = busy;
        if (status != null) SetStatus(status);
    }
}
