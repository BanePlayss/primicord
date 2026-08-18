using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// A bolinha social de um participante. Foto/camera ficam no centro, o halo mostra
/// presenca e voz, e nome/microfone acompanham o jogador pela arena.
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
    private int _avatarDiameter = SocialPosition.DefaultScale;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int AvatarDiameter
    {
        get => _avatarDiameter;
        set
        {
            int next = Math.Clamp(value, SocialPosition.MinScale, SocialPosition.MaxScale);
            if (next == _avatarDiameter && Width > 0) return;
            _avatarDiameter = next;
            Size = new Size(Math.Max(116, next + 30), next + 56);
            Invalidate();
        }
    }

    /// <summary>Centro geometrico usado pela arena para posicionar a bolinha.</summary>
    public PointF AvatarCenter => new(Width / 2f, 10 + AvatarDiameter / 2f);

    public bool Speaking => Level > SpeakThreshold && !Muted && Connected;

    public PeerTile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = true;
        AvatarDiameter = SocialPosition.DefaultScale;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int haloSize = AvatarDiameter;
        var halo = new Rectangle((Width - haloSize) / 2, 8, haloSize, haloSize);
        Color accent = AccentFor(Nick);
        if (!Connected) accent = Pv.BoneDim;
        else if (Muted) accent = Color.FromArgb(145, 112, 160);

        int alpha = Speaking ? 58 : 27;
        using (var glow = new SolidBrush(Color.FromArgb(alpha, accent))) g.FillEllipse(glow, halo);
        using (var glow2 = new Pen(Color.FromArgb(Speaking ? 155 : 95, accent), Speaking ? 3f : 1.6f))
            g.DrawEllipse(glow2, Rectangle.Inflate(halo, -2, -2));
        if (Speaking)
            using (var ring = new Pen(Color.FromArgb(75, accent), 6f))
                g.DrawEllipse(ring, Rectangle.Inflate(halo, 2, 2));

        int photoSize = Math.Max(30, (int)Math.Round(haloSize * 0.58));
        var photoBox = new Rectangle(
            halo.X + (halo.Width - photoSize) / 2,
            halo.Y + Math.Max(8, (halo.Height - photoSize) / 2 - 8),
            photoSize, photoSize);

        var cam = Cam;
        bool drewCam = cam != null && TryDrawImageCircle(g, cam, photoBox);
        if (!drewCam)
        {
            var photo = Primitivao.AvatarFor(Nick);
            if (photo != null) DrawImageCircle(g, photo, photoBox);
            else DrawInitial(g, photoBox, accent, Nick);
        }

        if (Muted || !Connected)
            using (var veil = new SolidBrush(Color.FromArgb(125, Pv.Charcoal)))
                g.FillEllipse(veil, photoBox);
        using (var border = new Pen(Pv.Charcoal, 2)) g.DrawEllipse(border, photoBox);

        DrawCaption(g, halo, accent);
    }

    private void DrawCaption(Graphics g, Rectangle halo, Color accent)
    {
        string original = (IsMe ? Nick + " (VOCE)" : Nick).ToUpperInvariant();
        string name = original;
        while (name.Length > 4 && Pv.TrackedWidth(g, name, Pv.DisplaySm, 0.8f) > Width - 8)
            name = name[..^1];
        if (name != original) name = name[..Math.Max(1, name.Length - 1)] + "…";

        using (var b = new SolidBrush(Pv.Bone))
        {
            float w = Pv.TrackedWidth(g, name, Pv.DisplaySm, 0.8f);
            Pv.DrawTracked(g, name, Pv.DisplaySm, b, (Width - w) / 2f, halo.Bottom + 1, 0.8f);
        }

        const int GiveUpSeconds = 25;
        string status = !Connected
            ? (Punching ? (SilentSeconds >= GiveUpSeconds ? "SEM ROTA" : "CONECTANDO") : "SEM SINAL")
            : Muted ? "MIC MUTADO"
            : Speaking ? "MIC · FALANDO"
            : Sharing ? "NA TELA" : "MIC ATIVO";
        Color statusColor = !Connected
            ? (Punching && SilentSeconds < GiveUpSeconds ? Pv.OrangeDim : Pv.Red)
            : Muted ? Color.FromArgb(176, 150, 190)
            : Speaking ? Pv.Green : accent;
        using (var b = new SolidBrush(statusColor))
        {
            float w = Pv.TrackedWidth(g, status, Pv.Label, 1.0f);
            Pv.DrawTracked(g, status, Pv.Label, b, (Width - w) / 2f, halo.Bottom + 25, 1.0f);
        }

        if (Connected && !Muted && Level > 0.01f)
        {
            int bw = (int)(Math.Min(1f, Level * 3f) * Math.Max(22, halo.Width - 28));
            using var b = new SolidBrush(Pv.Green);
            g.FillRectangle(b, (Width - bw) / 2, Height - 5, bw, 2);
        }
    }

    private static bool TryDrawImageCircle(Graphics g, Image image, Rectangle box)
    {
        try { DrawImageCircle(g, image, box); return true; }
        catch { return false; }
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
