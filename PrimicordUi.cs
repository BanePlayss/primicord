using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Paleta e fontes do Primicord. A hierarquia de superfícies mantém a densidade
/// de apps de comunidade modernos, enquanto o osso/fuligem/laranja traz a marca
/// Primitivão para toda a interface.
/// </summary>
public static class Pv
{
    /// <summary>
    /// Cor de destaque. Nao e constante porque o app adota o TEMA que o jogador
    /// escolheu no site do Primitivao — quem usa "Hortelã" la ve o Primicord verde.
    /// </summary>
    public static Color Orange { get; private set; } = Color.FromArgb(0xE6, 0x72, 0x16);
    public static Color OrangeDim { get; private set; } = Color.FromArgb(0xA8, 0x4A, 0x10);

    /// <summary>Troca a cor de destaque (null volta pro laranja padrao).</summary>
    public static void SetAccent(Color? accent)
    {
        var c = accent ?? Color.FromArgb(0xE6, 0x72, 0x16);
        Orange = c;
        // Versao apagada pra estados secundarios (borda de "conectando", etc).
        OrangeDim = Color.FromArgb((int)(c.R * 0.72), (int)(c.G * 0.72), (int)(c.B * 0.72));
    }
    // Paleta inspirada no escudo enviado: fuligem, madeira queimada, osso e
    // laranja de pigmento. O destaque continua legível em telas escuras.
    public static readonly Color Charcoal = Color.FromArgb(0x16, 0x16, 0x14);
    public static readonly Color Char2 = Color.FromArgb(0x22, 0x22, 0x1F);
    public static readonly Color Char3 = Color.FromArgb(0x3C, 0x3B, 0x35);
    public static readonly Color SurfaceLow = Color.FromArgb(0x29, 0x28, 0x23);
    public static readonly Color SurfaceLowest = Color.FromArgb(0x11, 0x0E, 0x0B);
    public static readonly Color SurfaceHover = Color.FromArgb(0x4A, 0x2D, 0x1B);
    public static readonly Color Input = Color.FromArgb(0x32, 0x21, 0x17);
    public static readonly Color Border = Color.FromArgb(0x53, 0x35, 0x20);
    public static readonly Color Bone = Color.FromArgb(0xF4, 0xE5, 0xCC);
    public static readonly Color BoneDim = Color.FromArgb(0xC9, 0xB6, 0x98);
    public static readonly Color Muted = Color.FromArgb(0x99, 0x82, 0x68);
    public static readonly Color Green = Color.FromArgb(0x8F, 0xBA, 0x62);
    public static readonly Color Red = Color.FromArgb(0xD8, 0x5A, 0x45);
    public static readonly Color Yellow = Color.FromArgb(0xE3, 0xB5, 0x4B);
    public static readonly Color NitroPurple = Color.FromArgb(0xB0, 0x5A, 0x20);
    public static readonly Color NitroPink = Color.FromArgb(0xF0, 0x8A, 0x25);

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
            if (c != ' ') g.DrawString(c.ToString(), font, brush, x, y);
            x += CharWidth(g, c, font) + tracking;
        }
    }

    public static float TrackedWidth(Graphics g, string text, Font font, float tracking)
    {
        float w = 0;
        foreach (char c in text) w += CharWidth(g, c, font) + tracking;
        return w;
    }

    /// <summary>
    /// Largura de um caractere. O espaco precisa de tratamento proprio: medido com
    /// GenericTypographic ele volta ~0 e as palavras grudam ("BANE·940TPC").
    /// </summary>
    private static float CharWidth(Graphics g, char c, Font font)
        => c == ' '
            ? font.Size * 0.42f
            : g.MeasureString(c.ToString(), font, PointF.Empty, StringFormat.GenericTypographic).Width;
}

/// <summary>Botao compacto, arredondado e de alto contraste.</summary>
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
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = text;
    }

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

        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        if (_down && Enabled) r.Offset(0, 1);

        if (bg != Color.Transparent)
            using (var b = new SolidBrush(bg))
            using (var path = Pv.RoundRect(r, 4)) g.FillPath(b, path);
        if (true)
            using (var p = new Pen(border, 1))
            using (var path = Pv.RoundRect(r, 4)) g.DrawPath(p, path);

        string t = Text.ToUpperInvariant();
        using var brush = new SolidBrush(fg);
        // Labels such as “TRANSMITIR MINHA MUSICA” precisam continuar legíveis
        // em uma janela estreita. O desenho centralizado com ellipsis evita que
        // o tracking antigo corte letras fora do botão.
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        g.DrawString(t, Font, brush, r, sf);
    }
}

/// <summary>
/// Slider no tema do app. O TrackBar do WinForms nao aceita cor — apareceria uma
/// barra branca com puxador azul do Windows no meio do painel escuro.
/// </summary>
public sealed class PrimSlider : Control
{
    private int _value = 30;
    private bool _dragging;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Minimum { get; set; } = 5;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; } = 60;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int v = Math.Clamp(value, Minimum, Maximum);
            if (v == _value) return;
            _value = v;
            ValueChanged?.Invoke(this, EventArgs.Empty);
            Invalidate();
        }
    }

    public event EventHandler? ValueChanged;

    public PrimSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 30;
        Cursor = Cursors.Hand;
        BackColor = Pv.Char2;
    }

    private int TrackLeft => 8;
    private int TrackRight => Width - 8;

    private void SetFromX(int x)
    {
        double t = (x - TrackLeft) / (double)Math.Max(1, TrackRight - TrackLeft);
        Value = (int)Math.Round(Minimum + t * (Maximum - Minimum));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    { _dragging = true; SetFromX(e.X); base.OnMouseDown(e); }

    protected override void OnMouseMove(MouseEventArgs e)
    { if (_dragging) SetFromX(e.X); base.OnMouseMove(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    { _dragging = false; base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int cy = Height / 2;
        double t = (_value - Minimum) / (double)Math.Max(1, Maximum - Minimum);
        int x = TrackLeft + (int)(t * (TrackRight - TrackLeft));

        using (var b = new SolidBrush(Pv.Char3))
        using (var path = Pv.RoundRect(new Rectangle(TrackLeft, cy - 3, TrackRight - TrackLeft, 6), 3))
            g.FillPath(b, path);

        if (x > TrackLeft)
            using (var b = new SolidBrush(Pv.Orange))
            using (var path = Pv.RoundRect(new Rectangle(TrackLeft, cy - 3, x - TrackLeft, 6), 3))
                g.FillPath(b, path);

        using (var b = new SolidBrush(Pv.Bone)) g.FillEllipse(b, x - 8, cy - 8, 16, 16);
        using (var p = new Pen(Pv.Charcoal, 2)) g.DrawEllipse(p, x - 8, cy - 8, 16, 16);
    }
}

/// <summary>Caixa de marcar no tema do app (a padrao do WinForms fica branca).</summary>
public sealed class PrimCheck : Control
{
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked { get; set; }

    public PrimCheck(string text)
    {
        Text = text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 26;
        Cursor = Cursors.Hand;
        BackColor = Pv.Char2;
        Font = Pv.Body;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Checked = !Checked; Invalidate(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var box = new Rectangle(0, (Height - 18) / 2, 18, 18);
        using (var b = new SolidBrush(Checked ? Pv.Orange : Pv.Charcoal))
        using (var path = Pv.RoundRect(box, 4))
            g.FillPath(b, path);
        using (var p = new Pen(Checked || _hover ? Pv.Orange : Pv.Char3, 2))
        using (var path = Pv.RoundRect(box, 4))
            g.DrawPath(p, path);

        if (Checked)
            using (var p = new Pen(Pv.Charcoal, 2.4f)
            { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(p, new[]
                {
                    new PointF(box.X + 4.5f, box.Y + 9f),
                    new PointF(box.X + 7.5f, box.Y + 12.5f),
                    new PointF(box.X + 13.5f, box.Y + 5.5f),
                });

        using var tb = new SolidBrush(Pv.Bone);
        g.DrawString(Text, Font, tb, box.Right + 10, (Height - Font.Height) / 2f);
    }
}

/// <summary>Caixa de texto no tema escuro (a TextBox padrao do WinForms nao tema).</summary>
public sealed class PrimInput : Panel
{
    public readonly TextBox Box;

    public PrimInput(string placeholder = "")
    {
        BackColor = Pv.Input;
        Padding = new Padding(10, 8, 10, 8);
        Height = 38;
        Box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Pv.Input,
            ForeColor = Pv.Bone,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            Dock = DockStyle.Fill,
            PlaceholderText = placeholder,
        };
        Controls.Add(Box);
        Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var p = new Pen(Box.Focused ? Pv.Orange : Pv.Input, 1);
            using var path = Pv.RoundRect(r, 7);
            e.Graphics.DrawPath(p, path);
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
