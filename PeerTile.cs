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
    public bool DjJoined;
    public bool DjHost;
    public bool Connected = true;
    public bool Punching;      // ainda furando o NAT
    public float Level;        // 0..1 nivel de voz agora

    private const float SpeakThreshold = 0.045f;

    public PeerTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(186, 124);
        BackColor = Pv.Char2;
    }

    public bool Speaking => Level > SpeakThreshold && !Muted && Connected;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var b = new SolidBrush(Pv.Char2)) g.FillRectangle(b, r);

        Color border = Speaking ? Pv.Green : (Punching ? Pv.OrangeDim : Pv.Char3);
        using (var p = new Pen(border, 2)) g.DrawRectangle(p, r);

        if (DjJoined)
        {
            var badge = new Rectangle(Width - 48, 8, 38, 20);
            using (var b = new SolidBrush(DjHost ? Pv.NitroPink : Pv.Char3))
            using (var path = Pv.RoundRect(badge, 6)) g.FillPath(b, path);
            Glyphs.Music(g, new RectangleF(badge.X + 5, badge.Y + 4, 12, 12), Pv.Bone, 1.5f);
            using var text = new SolidBrush(Pv.Bone);
            g.DrawString(DjHost ? "DJ" : "♫", Pv.Label, text, badge.X + 20, badge.Y + 5);
        }

        // Avatar: a MESMA foto que o jogador usa no site; sem foto, cai na inicial.
        int av = 52;
        var ac = new Rectangle((Width - av) / 2, 16, av, av);
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
            using (var p = new Pen(Pv.Green, 3))
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
            Pv.DrawTracked(g, name, Pv.Label, b, (Width - w) / 2f, 76, 1.2f);
        }

        // Estado embaixo.
        string status = !Connected ? (Punching ? "CONECTANDO" : "SEM SINAL")
                      : Muted ? "MUDO"
                      : Sharing ? "NA TELA" : "";
        if (status.Length > 0)
        {
            Color c = !Connected ? (Punching ? Pv.OrangeDim : Pv.Red)
                    : Muted ? Pv.Red : Pv.Orange;
            using var b = new SolidBrush(c);
            float w = Pv.TrackedWidth(g, status, Pv.Label, 1.4f);
            Pv.DrawTracked(g, status, Pv.Label, b, (Width - w) / 2f, 95, 1.4f);
        }

        // Barrinha de nivel (so quando conectado e sem mute).
        if (Connected && !Muted && Level > 0.01f)
        {
            int bw = (int)(Math.Min(1f, Level * 3f) * (Width - 40));
            using var b = new SolidBrush(Pv.Green);
            g.FillRectangle(b, 20, Height - 12, bw, 3);
        }
    }
}
