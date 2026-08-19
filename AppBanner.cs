namespace Primicord;

/// <summary>
/// Faixa de aviso do aplicativo. Toda mensagem pode ser fechada manualmente e
/// avisos rapidos tambem desaparecem sozinhos.
/// </summary>
public sealed class AppBanner : Panel
{
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Pv.Bone,
        TextAlign = ContentAlignment.MiddleCenter,
        Font = Pv.Body,
        Padding = new Padding(48, 0, 8, 0),
    };

    private readonly Button _close = new()
    {
        Dock = DockStyle.Right,
        Width = 48,
        Text = "×",
        AccessibleName = "Fechar notificacao",
        FlatStyle = FlatStyle.Flat,
        BackColor = Pv.Red,
        ForeColor = Pv.Bone,
        Font = Pv.DisplaySm,
        Cursor = Cursors.Hand,
        TabStop = true,
        UseVisualStyleBackColor = false,
    };

    private readonly System.Windows.Forms.Timer _autoDismiss = new();

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Message => _message.Text;

    public AppBanner()
    {
        Dock = DockStyle.Top;
        Height = 0;
        BackColor = Pv.Red;
        Visible = false;

        _close.FlatAppearance.BorderSize = 0;
        _close.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xD6, 0x45, 0x45);
        _close.FlatAppearance.MouseDownBackColor = Color.FromArgb(0x98, 0x28, 0x28);
        _close.Click += (_, _) => Dismiss();
        _autoDismiss.Tick += (_, _) => Dismiss();

        Controls.Add(_message);
        Controls.Add(_close);
        _close.BringToFront();
    }

    /// <param name="autoDismissMs">
    /// Zero mantem a faixa ate o usuario fechar ou o chamador limpar o aviso.
    /// </param>
    public void ShowMessage(string message, int autoDismissMs = 0)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowMessage(message, autoDismissMs));
            return;
        }

        _autoDismiss.Stop();
        _message.Text = message;
        Height = 42;
        Visible = true;

        if (autoDismissMs <= 0) return;
        _autoDismiss.Interval = Math.Max(1, autoDismissMs);
        _autoDismiss.Start();
    }

    public void Dismiss()
    {
        if (InvokeRequired)
        {
            BeginInvoke(Dismiss);
            return;
        }

        _autoDismiss.Stop();
        Visible = false;
        Height = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _autoDismiss.Dispose();
        base.Dispose(disposing);
    }
}
