using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// O cartao de um participante: inicial, nick, se esta mudo e se ja conectou.
/// A borda acende em verde quando a pessoa esta falando.
/// </summary>
public sealed class PeerTile : Control
{
    public string Nick = "";
    public bool IsMe;
    public bool Muted;
    public bool Sharing;
    public bool JamJoined;
    public bool Connected = true;
    public bool Punching;      // ainda furando o NAT
    public float Level;        // 0..1 nivel de voz agora
    public bool Large;
    public string Game = "";

    private const float SpeakThreshold = 0.045f;
    private readonly MotionValue _arrival, _speech;
    private bool _wasSpeaking;

    public PeerTile()
    {
        _arrival = new MotionValue(this, 1);
        _speech = new MotionValue(this);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(186, 124);
        BackColor = Pv.Charcoal;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
    }

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent != null) { _arrival.Snap(0); _arrival.To(1, 320, Math.Min(Parent.Controls.Count * 35, 175)); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
        base.OnKeyDown(e);
    }

    public bool Speaking => Level > SpeakThreshold && !Muted && Connected;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.TranslateTransform(0, (float)((1-_arrival.Value)*12));
        if (_wasSpeaking != Speaking) { _wasSpeaking = Speaking; _speech.To(Speaking ? 1 : 0, 180); }
        if (Large || Height < 120) { DrawCallCard(g); return; }

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(Pv.Char2)) g.FillRectangle(b, r);

        Color border = Speaking ? Pv.Green : (Punching ? Pv.OrangeDim : Pv.Char3);
        using (var p = new Pen(border, 2)) g.DrawRectangle(p, r);

        if (JamJoined)
        {
            var badge = new Rectangle(Width - 48, 8, 38, 20);
            using (var b = new SolidBrush(Pv.Char3))
            using (var path = Pv.RoundRect(badge, 6)) g.FillPath(b, path);
            Glyphs.Music(g, new RectangleF(badge.X + 5, badge.Y + 4, 12, 12), Pv.Bone, 1.5f);
            using var text = new SolidBrush(Pv.Bone);
            g.DrawString("JAM", Pv.Label, text, badge.X + 20, badge.Y + 5);
        }

        // Avatar: a MESMA foto que o jogador usa no site; sem foto, cai na inicial.
        int av = Large ? Math.Min(92, Width / 2) : 52;
        int avatarY = Large ? Math.Max(44, Height / 2 - 72) : 16;
        var ac = new Rectangle((Width - av) / 2, avatarY, av, av);
        var photo = Primitivao.AvatarFor(Nick);

        if (photo != null)
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(ac);
            var saved = g.Save();
            g.SetClip(clip);
            // Recorte quadrado central da foto, pra nao distorcer retrato/paisagem.
            int side = Math.Min(photo.Width, photo.Height);
            var src = new Rectangle((photo.Width - side) / 2, (photo.Height - side) / 2, side, side);
            g.DrawImage(photo, ac, src, GraphicsUnit.Pixel);
            g.Restore(saved);
            // Sem sinal / mutado: escurece a foto pra ficar claro que esta inativo.
            if (Muted || !Connected)
                using (var veil = new SolidBrush(Color.FromArgb(140, Pv.Charcoal)))
                    g.FillEllipse(veil, ac);
            using (var p = new Pen(Pv.Charcoal, 2)) g.DrawEllipse(p, ac);
        }
        else
        {
            using (var b = new SolidBrush(Muted || !Connected ? Pv.Char3 : Pv.Orange)) g.FillEllipse(b, ac);
            string initial = string.IsNullOrEmpty(Nick) ? "?" : Nick[..1].ToUpperInvariant();
            using var f = new Font("Bahnschrift", 22f, FontStyle.Bold);
            using var tb = new SolidBrush(Muted || !Connected ? Pv.BoneDim : Pv.Charcoal);
            var sz = g.MeasureString(initial, f);
            g.DrawString(initial, f, tb, ac.X + (av - sz.Width) / 2, ac.Y + (av - sz.Height) / 2);
        }

        if (Speaking)
            using (var p = new Pen(Pv.Orange, Large ? 5 : 3))
                g.DrawEllipse(p, Rectangle.Inflate(ac, 4, 4));

        // Nick.
        string name = (IsMe ? Nick + " (VOCE)" : Nick).ToUpperInvariant();
        using (var b = new SolidBrush(Pv.Bone))
        {
            float w = Pv.TrackedWidth(g, name, Pv.Label, 1.2f);
            if (w > Width - 12)   // corta nick gigante
            {
                while (name.Length > 3 && Pv.TrackedWidth(g, name + "...", Pv.Label, 1.2f) > Width - 12)
                    name = name[..^1];
                name += "...";
                w = Pv.TrackedWidth(g, name, Pv.Label, 1.2f);
            }
            Pv.DrawTracked(g, name, Pv.Label, b, (Width - w) / 2f, ac.Bottom + 12, 1.2f);
        }

        // Estado embaixo.
        string status = !Connected ? (Punching ? "CONECTANDO" : "SEM SINAL")
                      : Muted ? "MUDO"
                      : Sharing ? "NA TELA" : Speaking ? "FALANDO" : "";
        if (status.Length > 0)
        {
            Color c = !Connected ? (Punching ? Pv.OrangeDim : Pv.Red)
                    : Muted ? Pv.Red : Pv.Orange;
            using var b = new SolidBrush(c);
            float w = Pv.TrackedWidth(g, status, Pv.Label, 1.4f);
            Pv.DrawTracked(g, status, Pv.Label, b, (Width - w) / 2f, ac.Bottom + 32, 1.4f);
        }

        if (Sharing)
        {
            using var live = new SolidBrush(Pv.Green);
            g.DrawString("AO VIVO", Pv.Label, live, 14, 12);
        }
        else if (Large && Game.Length > 0)
            using (var b = new SolidBrush(Pv.BoneDim))
                g.DrawString("jogando " + Game, Pv.Label, b, new RectangleF(12,14,Width-24,28));

        // Barrinha de nivel (so quando conectado e sem mute).
        if (Connected && !Muted && Level > 0.01f)
        {
            int bw = (int)(Math.Min(1f, Level * 3f) * (Width - 40));
            using var b = new SolidBrush(Pv.Green);
            g.FillRectangle(b, 20, Height - 12, bw, 3);
        }
    }

    private void DrawCallCard(Graphics g)
    {
        // Stable, restrained avatar colors give each person a recognizable tile.
        Color[] colors = { Color.FromArgb(60, 45, 42), Color.FromArgb(43, 52, 66),
            Color.FromArgb(46, 57, 51), Color.FromArgb(57, 43, 63), Color.FromArgb(62, 52, 37) };
        uint hash = 0;
        foreach (char ch in Nick.ToLowerInvariant()) hash = unchecked(hash * 31 + ch);
        var rect = new Rectangle(2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5));
        using var path = Pv.RoundRect(rect, 12);
        using (var fill = new SolidBrush(colors[hash % (uint)colors.Length])) g.FillPath(fill, path);
        if (_speech.Value > .01 || Punching)
            using (var pen = new Pen(Punching ? Pv.Orange : UiMotion.Blend(colors[hash % (uint)colors.Length], Pv.Green, _speech.Value), 3)) g.DrawPath(pen, path);

        int diameter = Math.Clamp(Height / 3, Large ? 44 : 32, 86);
        var avatar = new Rectangle((Width - diameter) / 2, (Height - diameter) / 2 - 2, diameter, diameter);
        Glyphs.Avatar(g, avatar, Nick, Pv.Orange, Pv.Bone);
        if (_speech.Value > .01)
            using (var pen = new Pen(Color.FromArgb((int)(255*Math.Clamp(_speech.Value,0,1)), Pv.Green), (float)(2+2*_speech.Value)))
                g.DrawEllipse(pen, Rectangle.Inflate(avatar, 5, 5));
        using var light = new SolidBrush(Pv.Bone);
        using var dim = new SolidBrush(Pv.BoneDim);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(IsMe ? Nick + " (você)" : Nick, Pv.BodyBold, light,
            new RectangleF(14, Height - 30, Math.Max(1, Width - 54), 24), format);
        if (Sharing)
        {
            using var live = new SolidBrush(Pv.Green);
            g.DrawString("AO VIVO", Pv.Label, live, 14, 12);
        }
        else if (Large && Game.Length > 0)
            g.DrawString("Jogando " + Game, Pv.Label, dim, new RectangleF(14, 12, Math.Max(1, Width - 85), 20), format);
        if (JamJoined)
        {
            Glyphs.Music(g, new RectangleF(Width - 61, 12, 14, 14), Pv.Green, 1.5f);
            g.DrawString("JAM", Pv.Label, light, Width - 42, 13);
        }
        if (Muted) Glyphs.Mic(g, new RectangleF(Width - 34, Height - 32, 18, 18), Pv.Red, true);
        if (!Connected)
            g.DrawString(Punching ? "Conectando…" : "Sem sinal", Pv.Label, dim, 14, Height - 50);
    }
}
