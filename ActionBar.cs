using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Botao de acao em icone, com rotulo curto embaixo e estado ligado/desligado.
/// </summary>
/// <remarks>
/// Substitui os botoes de texto largos: cinco rotulos escritos por extenso somavam
/// ~800px e o ultimo ficava fora da tela. Em icone cabem todos, sobra espaco pra
/// tela e fica parecido com a barra do Discord.
/// </remarks>
public sealed class ActionIcon : Control
{
    public delegate void Painter(Graphics g, RectangleF box, Color color, float weight);

    private Painter _paint;
    private bool _hover, _down;
    private readonly ToolTip _tip = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    /// <summary>Estado de perigo (mutado, gravando): pinta em vermelho.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Alert { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Caption { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ToolTipText
    {
        get => _tip.GetToolTip(this) ?? "";
        set => _tip.SetToolTip(this, value);
    }

    /// <summary>Marcador no canto (ex.: a tecla do clipe).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Badge { get; set; } = "";

    public ActionIcon(Painter painter, string caption)
    {
        _paint = painter;
        Caption = caption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(66, 58);
        Cursor = Cursors.Hand;
        BackColor = Pv.Charcoal;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = caption;
    }

    public void SetPainter(Painter p) { _paint = p; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.SuppressKeyPress = true;
            if (Enabled) OnClick(EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int d = 38;
        var circle = new Rectangle((Width - d) / 2, 2, d, d);

        Color bg = Alert ? Pv.Red : Active ? Pv.Orange : Pv.Char2;
        Color fg = Alert || Active ? Pv.Charcoal : Enabled ? Pv.Bone : Pv.BoneDim;
        if (_hover && !Active && !Alert) bg = Pv.Char3;
        if (!Enabled) { bg = Pv.Char2; fg = Pv.BoneDim; }

        var r = circle;
        if (_down && Enabled) r.Offset(0, 1);
        using (var b = new SolidBrush(bg)) g.FillEllipse(b, r);
        if (_hover && Enabled)
            using (var p = new Pen(Alert ? Pv.Red : Pv.Orange, 2))
                g.DrawEllipse(p, r);

        float pad = d * 0.28f;
        _paint(g, new RectangleF(r.X + pad, r.Y + pad, d - pad * 2, d - pad * 2), fg, 1.8f);

        if (Badge.Length > 0)
        {
            using var b = new SolidBrush(Pv.BoneDim);
            float w = g.MeasureString(Badge, Pv.Label).Width;
            g.DrawString(Badge, Pv.Label, b, r.Right - w + 4, r.Y - 2);
        }

        if (Caption.Length > 0)
        {
            using var b = new SolidBrush(Enabled ? (Active || Alert ? Pv.Bone : Pv.BoneDim) : Pv.Char3);
            float w = Pv.TrackedWidth(g, Caption, Pv.Label, 0.8f);
            Pv.DrawTracked(g, Caption, Pv.Label, b, (Width - w) / 2f, circle.Bottom + 3, 0.8f);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Painel flutuante com os ajustes mais usados, aberto pela engrenagem da barra.
/// </summary>
/// <remarks>
/// Existe porque abrir a janela cheia de configuracoes so pra trocar de microfone
/// no meio da call e demais. Aqui ficam so os quatro que se mexe na pratica.
/// </remarks>
public sealed class QuickSettings : Form
{
    private readonly Config _cfg;
    public event Action? Applied;

    public QuickSettings(Config cfg, Point screenPos)
    {
        _cfg = cfg;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Pv.Char2;
        ClientSize = new Size(340, 268);

        var head = new Label
        {
            Text = "AJUSTES RAPIDOS", Font = Pv.Label, ForeColor = Pv.BoneDim,
            Location = new Point(16, 14), AutoSize = true,
        };

        var lblMic = Section("MICROFONE", new Point(16, 40));
        var mic = Combo(new Point(16, 58));
        foreach (var d in VoiceEngine.ListInputs()) mic.Items.Add(d);
        for (int i = 0; i < mic.Items.Count; i++)
            if (mic.Items[i] is InputDeviceItem it && it.DeviceNumber == cfg.MicDevice) mic.SelectedIndex = i;
        if (mic.SelectedIndex < 0 && mic.Items.Count > 0) mic.SelectedIndex = 0;

        var lblOut = Section("SAIDA", new Point(16, 96));
        var outc = Combo(new Point(16, 114));
        outc.Items.Add(new OutputDeviceItem("", "Padrao do Windows"));
        foreach (var d in VoiceEngine.ListOutputs()) outc.Items.Add(d);
        outc.SelectedIndex = 0;
        for (int i = 0; i < outc.Items.Count; i++)
            if (outc.Items[i] is OutputDeviceItem o && o.Id == cfg.OutputDeviceId) outc.SelectedIndex = i;

        var lblQ = Section("QUALIDADE DA TELA", new Point(16, 152));
        var q = Combo(new Point(16, 170));
        var degraus = new[] { (450, "Economico"), (900, "Equilibrado"), (2000, "Alta"), (4000, "Maxima") };
        foreach (var (_, nome) in degraus) q.Items.Add(nome);
        q.SelectedIndex = Array.FindIndex(degraus, d => d.Item1 == cfg.ScreenBudgetKb);
        if (q.SelectedIndex < 0) q.SelectedIndex = 2;

        var audio = new PrimCheck("Mandar o som do sistema junto com a tela")
        { Location = new Point(16, 206), Size = new Size(308, 26), Checked = cfg.ShareAudioWithScreen };

        var ok = new PrimButton("APLICAR") { Location = new Point(16, 238), Size = new Size(150, 26) };
        ok.Click += (_, _) =>
        {
            if (mic.SelectedItem is InputDeviceItem mi) _cfg.MicDevice = mi.DeviceNumber;
            if (outc.SelectedItem is OutputDeviceItem oi) _cfg.OutputDeviceId = oi.Id;
            if (q.SelectedIndex >= 0) _cfg.ScreenBudgetKb = degraus[q.SelectedIndex].Item1;
            _cfg.ShareAudioWithScreen = audio.Checked;
            _cfg.Save();
            Applied?.Invoke();
            Close();
        };
        var cancel = new PrimButton("FECHAR", PrimButton.Style.Ghost)
        { Location = new Point(174, 238), Size = new Size(150, 26) };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[] { head, lblMic, mic, lblOut, outc, lblQ, q, audio, ok, cancel });

        Paint += (_, e) =>
        {
            using var p = new Pen(Pv.Orange, 2);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        };
        Deactivate += (_, _) => Close();

        // Encaixa na tela: aberto perto da barra, sem sair pra fora do monitor.
        var wa = Screen.FromPoint(screenPos).WorkingArea;
        Location = new Point(
            Math.Min(Math.Max(wa.Left + 8, screenPos.X - Width / 2), wa.Right - Width - 8),
            Math.Max(wa.Top + 8, screenPos.Y - Height - 12));
    }

    private static Label Section(string t, Point at) => new()
    { Text = t, Font = Pv.Label, ForeColor = Pv.Orange, Location = at, AutoSize = true };

    private static ComboBox Combo(Point at) => new()
    {
        Location = at, Width = 308, DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat, BackColor = Pv.Charcoal, ForeColor = Pv.Bone, Font = Pv.Body,
    };
}
