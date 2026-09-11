using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Uma linha clicavel do rail esquerdo: canal de texto, sala de voz ou conversa.
/// Fundo arredondado que acende no hover e fica solido quando e o item ativo —
/// e o que tira o ar de "tudo quadrado".
/// </summary>
public sealed class RailItem : Control
{
    public enum Kind { TextChannel, Voice, Music, Dm, Action, Event, Members, Boost }

    private bool _hover;
    private readonly MotionValue _hoverMotion;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Kind ItemKind { get; set; } = Kind.TextChannel;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get; set; }

    /// <summary>Nick — quando presente, desenha a foto no lugar do icone (DMs).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? AvatarNick { get; set; }

    /// <summary>Texto pequeno a direita (ex.: "3" numa sala com gente).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Suffix { get; set; } = "";

    /// <summary>Bolinha verde de online (DMs).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Online { get; set; }

    /// <summary>Recuo para participantes exibidos abaixo de uma sala de voz.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Indent { get; set; }

    public RailItem(string text, Kind kind)
    {
        _hoverMotion = new MotionValue(this);
        Text = text;
        ItemKind = kind;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 32;
        Cursor = Cursors.Hand;
        BackColor = Pv.Char2;   // MESMA cor do rail: senao aparece emenda vertical
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        AccessibleName = text;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; _hoverMotion.To(1); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _hoverMotion.To(0); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            e.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(6 + Indent, 2, Width - 12 - Indent, Height - 4);
        if (Active || _hoverMotion.Value > 0)
        {
            using var path = Pv.RoundRect(r, 6);
            using var b = new SolidBrush(Active ? Color.FromArgb(48, 46, 66) : UiMotion.Blend(BackColor, Pv.SurfaceHover, _hoverMotion.Value));
            g.FillPath(b, path);
        }
        if (Active)
        {
            using var accent = new SolidBrush(Pv.NitroPurple);
            using var indicator = Pv.RoundRect(new Rectangle(r.X, r.Y + 6, 3, Math.Max(3, r.Height - 12)), 2);
            g.FillPath(accent, indicator);
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Pv.NitroPurple, 1.5f);
            using var outline = Pv.RoundRect(r, 6);
            g.DrawPath(focus, outline);
        }
        g.TranslateTransform((float)(_hoverMotion.Value * 2), 0);

        Color fg = Active ? Pv.Bone : _hover ? Pv.Bone : Pv.BoneDim;
        int iconBox = 18;
        var ic = new RectangleF(r.X + 8, r.Y + (r.Height - iconBox) / 2f, iconBox, iconBox);

        if (ItemKind == Kind.Dm && !string.IsNullOrEmpty(AvatarNick))
        {
            var box = new Rectangle((int)ic.X, (int)ic.Y - 2, 22, 22);
            Glyphs.Avatar(g, box, AvatarNick, Pv.Char3, Pv.Bone);
            if (Online)
                Glyphs.StatusDot(g, new RectangleF(box.Right - 8, box.Bottom - 8, 9, 9),
                                 Pv.Green, Pv.Char2);
            ic = new RectangleF(ic.X + 6, ic.Y, iconBox, iconBox);
        }
        else
        {
            switch (ItemKind)
            {
                case Kind.TextChannel: Glyphs.Hash(g, ic, fg); break;
                case Kind.Voice: Glyphs.Speaker(g, ic, fg); break;
                case Kind.Music: Glyphs.Music(g, ic, fg); break;
                case Kind.Action: Glyphs.Plus(g, ic, fg); break;
                case Kind.Event: Glyphs.Bell(g, ic, fg); break;
                case Kind.Members: Glyphs.Users(g, ic, fg); break;
                case Kind.Boost: Glyphs.Compass(g, ic, fg); break;
            }
        }

        float textX = ic.X + iconBox + 10;
        using (var b = new SolidBrush(fg))
        {
            var font = Active ? Pv.BodyBold : Pv.Body;
            string t = Text;
            float avail = r.Right - textX - (Suffix.Length > 0 ? 34 : 10);
            while (t.Length > 3 && g.MeasureString(t, font).Width > avail) t = t[..^1];
            if (t != Text) t = t[..Math.Max(1, t.Length - 1)] + "…";
            g.DrawString(t, font, b, textX, r.Y + (r.Height - font.Height) / 2f);
        }

        if (Suffix.Length > 0)
        {
            using var b = new SolidBrush(Pv.BoneDim);
            float w = g.MeasureString(Suffix, Pv.Label).Width;
            g.DrawString(Suffix, Pv.Label, b, r.Right - w - 10, r.Y + (r.Height - Pv.Label.Height) / 2f);
        }
    }
}

/// <summary>Linha da lista de membros (direita): foto, nick e status.</summary>
public sealed class MemberRow : Control
{
    private bool _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Nick { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Online { get; set; }

    /// <summary>Sala de voz em que a pessoa esta (mostrada abaixo do nick).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Room { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsMe { get; set; }

    public MemberRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 54;
        Cursor = Cursors.Hand;
        BackColor = Pv.Char2;   // MESMA cor do painel de membros
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); return; }
        base.OnKeyDown(e);
    }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(6, 2, Width - 12, Height - 4);
        if (_hover || Focused)
        {
            using var path = Pv.RoundRect(r, 6);
            using var b = new SolidBrush(Pv.SurfaceHover);
            g.FillPath(b, path);
        }

        var box = new Rectangle(r.X + 8, r.Y + (r.Height - 30) / 2, 30, 30);
        var saved = g.Save();
        if (!Online) g.CompositingQuality = CompositingQuality.HighQuality;
        Glyphs.Avatar(g, box, Nick, Online ? Pv.AvatarColor(Nick) : Pv.Char3, Pv.Bone);
        if (!Online)
        {
            // Offline: veu por cima da foto, como no Discord.
            using var veil = new SolidBrush(Color.FromArgb(150, Pv.Char2));
            g.FillEllipse(veil, box);
        }
        g.Restore(saved);

        Glyphs.StatusDot(g, new RectangleF(box.Right - 9, box.Bottom - 9, 11, 11),
                         Online ? Pv.Green : Pv.BoneDim, Pv.Char2);

        float tx = box.Right + 10;
        bool twoLines = Room.Length > 0;
        AccessibleName = Nick + (IsMe ? " (você)" : "") + ", " + Room;
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using (var b = new SolidBrush(Online ? Pv.Bone : Pv.BoneDim))
        {
            string name = Nick + (IsMe ? " (você)" : "");
            g.DrawString(name, Online ? Pv.BodyBold : Pv.Body, b,
                new RectangleF(tx, twoLines ? r.Y + 5 : r.Y + (r.Height - Pv.Body.Height) / 2f, Math.Max(1,r.Right-tx-8),22), format);
        }
        if (twoLines)
        {
            using var b = new SolidBrush(Room == "falando" ? Pv.Green : Pv.BoneDim);
            g.DrawString(Room, Pv.Label, b, new RectangleF(tx, r.Y + 27, Math.Max(1,r.Right-tx-8),19), format);
        }
    }
}
