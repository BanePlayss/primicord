using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Paleta e fontes — mesma identidade do app de apostas, em "edicao noturna".</summary>
public static class Pv
{
    public static readonly Color Orange = Color.FromArgb(0xD7, 0x64, 0x14);
    public static readonly Color OrangeDim = Color.FromArgb(0xA8, 0x4A, 0x08);
    public static readonly Color Charcoal = Color.FromArgb(0x1C, 0x16, 0x12);
    public static readonly Color Char2 = Color.FromArgb(0x2A, 0x21, 0x1B);
    public static readonly Color Char3 = Color.FromArgb(0x38, 0x2C, 0x24);
    public static readonly Color Bone = Color.FromArgb(0xF4, 0xEA, 0xD7);
    public static readonly Color BoneDim = Color.FromArgb(0x8A, 0x81, 0x74);
    public static readonly Color Green = Color.FromArgb(0x6D, 0x9A, 0x44);
    public static readonly Color Red = Color.FromArgb(0xC0, 0x33, 0x33);

    public static readonly Font Display = new("Bahnschrift", 20f, FontStyle.Bold);
    public static readonly Font DisplaySm = new("Bahnschrift", 13f, FontStyle.Bold);
    public static readonly Font Body = new("Segoe UI", 9.5f);
    public static readonly Font BodyBold = new("Segoe UI", 9.5f, FontStyle.Bold);
    public static readonly Font Label = new("Segoe UI", 7.5f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);

    public static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Desenha texto com espacamento entre letras (o "tracking" do app).</summary>
    public static void DrawTracked(Graphics g, string text, Font font, Brush brush,
                                   float x, float y, float tracking)
    {
        foreach (char c in text)
        {
            string s = c.ToString();
            g.DrawString(s, font, brush, x, y);
            x += g.MeasureString(s, font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
        }
    }

    public static float TrackedWidth(Graphics g, string text, Font font, float tracking)
    {
        float w = 0;
        foreach (char c in text)
            w += g.MeasureString(c.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width + tracking;
        return w;
    }
}

/// <summary>Botao chapado no estilo do app: retangulo com sombra dura, sem gradiente.</summary>
public sealed class PrimButton : Control
{
    public enum Style { Solid, Ghost, Danger }

    private bool _hover, _down;

    // A UI e montada em codigo (sem designer), entao nada aqui precisa ser serializado.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Style Kind { get; set; } = Style.Solid;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    public PrimButton(string text, Style kind = Style.Solid)
    {
        Text = text;
        Kind = kind;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(120, 36);
        Cursor = Cursors.Hand;
        Font = Pv.Label;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Color bg, fg, border;
        switch (Kind)
        {
            case Style.Ghost:
                bg = _hover ? Pv.Char3 : Color.Transparent;
                fg = _hover ? Pv.Orange : Pv.Bone;
                border = _hover ? Pv.Orange : Pv.Char3;
                break;
            case Style.Danger:
                bg = _hover ? Pv.Red : Color.Transparent;
                fg = _hover ? Pv.Bone : Pv.Red;
                border = Pv.Red;
                break;
            default:
                bg = Active || _hover ? Pv.Bone : Pv.Orange;
                fg = Pv.Charcoal;
                border = bg;
                break;
        }
        if (!Enabled) { bg = Pv.Char2; fg = Pv.BoneDim; border = Pv.Char3; }

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_down && Enabled) r.Offset(0, 1);

        if (bg != Color.Transparent)
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
        using (var p = new Pen(border, 2)) g.DrawRectangle(p, r);

        string t = Text.ToUpperInvariant();
        using var brush = new SolidBrush(fg);
        float tw = Pv.TrackedWidth(g, t, Font, 1.6f);
        Pv.DrawTracked(g, t, Font, brush, r.X + (r.Width - tw) / 2f,
                       r.Y + (r.Height - Font.Height) / 2f, 1.6f);
    }
}

/// <summary>Caixa de texto no tema escuro (a TextBox padrao do WinForms nao tema).</summary>
public sealed class PrimInput : Panel
{
    public readonly TextBox Box;

    public PrimInput(string placeholder = "")
    {
        BackColor = Pv.Charcoal;
        Padding = new Padding(10, 8, 10, 8);
        Height = 38;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Pv.Charcoal,
            ForeColor = Pv.Bone,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            PlaceholderText = placeholder,
        };
        Controls.Add(Box);
        Paint += (_, e) =>
        {
            using var p = new Pen(Box.Focused ? Pv.Orange : Pv.Char3, 2);
            e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        };
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => Box.Text;
        set => Box.Text = value;
    }
}
