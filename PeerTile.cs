using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Card de um participante na chamada. Camera ocupa o fundo; sem camera, avatar e
/// estado de voz seguem o padrao visual de uma chamada em grade.
/// </summary>
public sealed class PeerTile : Control
{
    public string Nick = "";
    public bool IsMe;
    public bool Muted;
    public bool Sharing;
    public bool Connected = true;
    public bool Punching;
    public int SilentSeconds;
    public float Level;

    /// <summary>Ultimo quadro da camera; o tile nao e dono da imagem.</summary>
    public Image? Cam;

    private const float SpeakThreshold = 0.045f;

    public bool Speaking => Level > SpeakThreshold && !Muted && Connected;

    public PeerTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        DrawGridCard(g);
    }

    private void DrawGridCard(Graphics g)
    {
        var card = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        Color accent = AccentFor(Nick);
        if (!Connected) accent = Pv.BoneDim;

        using (var path = Pv.RoundRect(card, 9))
        using (var fill = new SolidBrush(Color.FromArgb(35, 36, 40)))
            g.FillPath(fill, path);

        bool drewCam = Cam != null && TryDrawImageCover(g, Cam, card);
        if (!drewCam)
        {
            int size = Math.Clamp((int)(Math.Min(Width, Height) * .44f), 52, 126);
            var avatar = new Rectangle((Width - size) / 2, Math.Max(12, (Height - size) / 2 - 7),
                                       size, size);
            var photo = Primitivao.AvatarFor(Nick);
            if (photo != null) DrawImageCircle(g, photo, avatar);
            else DrawInitial(g, avatar, accent, Nick);
            using var outline = new Pen(Color.FromArgb(120, accent), 2f);
            g.DrawEllipse(outline, avatar);
        }

        int footerH = Math.Clamp(Height / 4, 30, 42);
        var footer = new Rectangle(card.Left, card.Bottom - footerH, card.Width, footerH);
        using (var path = Pv.RoundRect(footer, 8))
        using (var shade = new SolidBrush(Color.FromArgb(218, 18, 19, 22)))
            g.FillPath(shade, path);

        string name = Nick.ToLowerInvariant() + (IsMe ? " (voce)" : "");
        using (var nameBrush = new SolidBrush(Pv.Bone))
            g.DrawString(name, Pv.BodyBold, nameBrush, footer.Left + 11,
                         footer.Top + (footer.Height - Pv.BodyBold.Height) / 2f);

        string status = !Connected ? "SEM ROTA" : Muted ? "MUTADO" : Speaking ? "FALANDO" : "MIC";
        Color statusColor = !Connected ? Pv.Red : Muted ? Pv.BoneDim : Speaking ? Pv.Green : Pv.BoneDim;
        float statusWidth = Pv.TrackedWidth(g, status, Pv.Label, .7f);
        using (var statusBrush = new SolidBrush(statusColor))
            Pv.DrawTracked(g, status, Pv.Label, statusBrush,
                           footer.Right - statusWidth - 10,
                           footer.Top + (footer.Height - Pv.Label.Height) / 2f + 1, .7f);

        if (Sharing)
        {
            const string live = "AO VIVO";
            float liveWidth = Pv.TrackedWidth(g, live, Pv.Label, .8f);
            var badge = new Rectangle(card.Right - (int)liveWidth - 23, card.Top + 10,
                                      (int)liveWidth + 14, 22);
            using (var path = Pv.RoundRect(badge, 4))
            using (var fill = new SolidBrush(Pv.Red)) g.FillPath(fill, path);
            using var text = new SolidBrush(Color.White);
            Pv.DrawTracked(g, live, Pv.Label, text, badge.Left + 7, badge.Top + 5, .8f);
        }

        Color borderColor = Speaking ? Pv.Green : Color.FromArgb(80, Pv.Char3);
        float borderWidth = Speaking ? 3f : 1f;
        using (var path = Pv.RoundRect(card, 9))
        using (var border = new Pen(borderColor, borderWidth))
            g.DrawPath(border, path);
    }

    private static bool TryDrawImageCover(Graphics g, Image image, Rectangle box)
    {
        var saved = g.Save();
        try
        {
            using var clip = Pv.RoundRect(box, 9);
            g.SetClip(clip);
            double scale = Math.Max(box.Width / (double)image.Width, box.Height / (double)image.Height);
            int sourceW = Math.Max(1, (int)(box.Width / scale));
            int sourceH = Math.Max(1, (int)(box.Height / scale));
            var source = new Rectangle((image.Width - sourceW) / 2, (image.Height - sourceH) / 2,
                                       sourceW, sourceH);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(image, box, source, GraphicsUnit.Pixel);
            return true;
        }
        catch { return false; }
        finally { g.Restore(saved); }
    }

    private static void DrawImageCircle(Graphics g, Image image, Rectangle box)
    {
        using var clip = new GraphicsPath();
        clip.AddEllipse(box);
        var saved = g.Save();
        g.SetClip(clip);
        int side = Math.Min(image.Width, image.Height);
        var src = new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(image, box, src, GraphicsUnit.Pixel);
        g.Restore(saved);
    }

    private static void DrawInitial(Graphics g, Rectangle box, Color accent, string nick)
    {
        using (var b = new SolidBrush(accent)) g.FillEllipse(b, box);
        string initial = string.IsNullOrWhiteSpace(nick) ? "?" : nick[..1].ToUpperInvariant();
        using var f = new Font("Bahnschrift", Math.Max(12, box.Height * 0.40f), FontStyle.Bold);
        using var tb = new SolidBrush(Pv.Charcoal);
        var size = g.MeasureString(initial, f);
        g.DrawString(initial, f, tb, box.X + (box.Width - size.Width) / 2,
                     box.Y + (box.Height - size.Height) / 2);
    }

    /// <summary>Cor social estavel por nick, sem persistir tema paralelo.</summary>
    private static Color AccentFor(string nick)
    {
        Color[] palette =
        {
            Color.FromArgb(65, 164, 232),
            Color.FromArgb(211, 61, 62),
            Color.FromArgb(210, 165, 33),
            Color.FromArgb(151, 82, 188),
            Color.FromArgb(100, 162, 70),
            Pv.Orange,
        };
        return palette[(int)(RoomSession.HashId(nick) % (uint)palette.Length)];
    }
}
