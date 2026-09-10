using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Botao redondo so com icone (engrenagem, mic, sair).</summary>
public sealed class GlyphButton : Control
{
    public delegate void Painter(Graphics g, RectangleF box, Color color, float weight);

    private readonly Painter _paint;
    private bool _hover, _down;
    private readonly ToolTip _tip = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; } = Pv.Bone;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string ToolTipText
    {
        get => _tip.GetToolTip(this) ?? "";
        set => _tip.SetToolTip(this, value);
    }

    public GlyphButton(Painter painter)
    {
        _paint = painter;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(34, 34);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_hover)
        {
            using var b = new SolidBrush(Color.FromArgb(_down ? 90 : 60, Pv.Bone));
            g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        }

        float pad = Width * 0.24f;
        var box = new RectangleF(pad, pad, Width - pad * 2, Height - pad * 2);
        _paint(g, box, _hover ? Pv.Orange : Accent, 1.7f);
        if (Focused) { using var pen = new Pen(Pv.Orange, 2); g.DrawEllipse(pen, 2, 2, Width - 5, Height - 5); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tip.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Caixinha de texto modal — usada pra pedir o nome da sala nova.</summary>
public static class PromptDialog
{
    public static string? Ask(IWin32Window owner, string title, string label, string placeholder)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(400, 190),
            BackColor = Pv.Char2,
            ForeColor = Pv.Bone,
            Font = Pv.Body,
        };
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) dlg.Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        var head = new Label
        {
            Text = title, Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(22, 20), AutoSize = true,
        };
        var lbl = new Label
        {
            Text = label, Font = Pv.Label, ForeColor = Pv.Orange,
            Location = new Point(22, 58), AutoSize = true,
        };
        var input = new PrimInput(placeholder) { Location = new Point(22, 78), Width = 356 };
        var ok = new PrimButton("CRIAR") { Location = new Point(22, 132), Size = new Size(172, 40) };
        var cancel = new PrimButton("CANCELAR", PrimButton.Style.Ghost)
        { Location = new Point(206, 132), Size = new Size(172, 40) };

        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };
        input.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            dlg.DialogResult = DialogResult.OK;
            dlg.Close();
        };

        dlg.Controls.AddRange(new Control[] { head, lbl, input, ok, cancel });
        dlg.Shown += (_, _) => input.Box.Focus();
        return dlg.ShowDialog(owner) == DialogResult.OK ? input.Value : null;
    }
}

/// <summary>Escolha entre opcoes — usada pra pedir qual monitor compartilhar.</summary>
public static class PickDialog
{
    /// <summary>Indice escolhido, ou -1 se cancelou.</summary>
    public static int Choose(IWin32Window owner, string title, string label, List<string> options)
    {
        using var dlg = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 190),
            BackColor = Pv.Char2,
            ForeColor = Pv.Bone,
            Font = Pv.Body,
        };
        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null) dlg.Icon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        var head = new Label
        {
            Text = title, Font = Pv.DisplaySm, ForeColor = Pv.Bone,
            Location = new Point(22, 20), AutoSize = true,
        };
        var lbl = new Label
        {
            Text = label, Font = Pv.Label, ForeColor = Pv.Orange,
            Location = new Point(22, 58), AutoSize = true,
        };
        var combo = new ComboBox
        {
            Location = new Point(22, 78), Width = 376,
            DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
            BackColor = Pv.Charcoal, ForeColor = Pv.Bone, Font = Pv.Body,
        };
        foreach (string o in options) combo.Items.Add(o);
        combo.SelectedIndex = 0;

        var ok = new PrimButton("COMPARTILHAR") { Location = new Point(22, 132), Size = new Size(182, 40) };
        var cancel = new PrimButton("CANCELAR", PrimButton.Style.Ghost)
        { Location = new Point(216, 132), Size = new Size(182, 40) };
        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; dlg.Close(); };
        cancel.Click += (_, _) => { dlg.DialogResult = DialogResult.Cancel; dlg.Close(); };

        dlg.Controls.AddRange(new Control[] { head, lbl, combo, ok, cancel });
        return dlg.ShowDialog(owner) == DialogResult.OK ? combo.SelectedIndex : -1;
    }
}
