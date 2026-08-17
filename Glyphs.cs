using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Iconografia desenhada em GDI+ (lineart, mesma pegada do &lt;Icon&gt; do site).
/// Sem emoji e sem arquivo de imagem — escala em qualquer DPI.
/// Todo metodo desenha dentro do retangulo dado.
/// </summary>
public static class Glyphs
{
    private static Pen P(Color c, float w) => new(c, w)
    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    /// <summary>"#" — canal de texto.</summary>
    public static void Hash(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float x = r.X, y = r.Y, s = Math.Min(r.Width, r.Height);
        g.DrawLine(p, x + s * 0.36f, y + s * 0.12f, x + s * 0.26f, y + s * 0.88f);
        g.DrawLine(p, x + s * 0.68f, y + s * 0.12f, x + s * 0.58f, y + s * 0.88f);
        g.DrawLine(p, x + s * 0.14f, y + s * 0.36f, x + s * 0.80f, y + s * 0.36f);
        g.DrawLine(p, x + s * 0.12f, y + s * 0.64f, x + s * 0.78f, y + s * 0.64f);
    }

    /// <summary>Alto-falante — sala de voz.</summary>
    public static void Speaker(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        var pts = new[]
        {
            new PointF(x + s * 0.14f, y + s * 0.38f), new PointF(x + s * 0.30f, y + s * 0.38f),
            new PointF(x + s * 0.48f, y + s * 0.18f), new PointF(x + s * 0.48f, y + s * 0.82f),
            new PointF(x + s * 0.30f, y + s * 0.62f), new PointF(x + s * 0.14f, y + s * 0.62f),
        };
        g.DrawPolygon(p, pts);
        g.DrawArc(p, x + s * 0.46f, y + s * 0.30f, s * 0.30f, s * 0.40f, -60, 120);
        g.DrawArc(p, x + s * 0.46f, y + s * 0.18f, s * 0.48f, s * 0.64f, -55, 110);
    }

    /// <summary>Engrenagem — configuracoes.</summary>
    public static void Gear(Graphics g, RectangleF r, Color c, float w = 1.6f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height);
        float cx = r.X + s / 2f, cy = r.Y + s / 2f;
        float rOut = s * 0.42f, rIn = s * 0.27f, tooth = s * 0.10f;

        // 8 dentes radiais + dois circulos.
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            g.DrawLine(p, cx + dx * (rOut - tooth), cy + dy * (rOut - tooth),
                          cx + dx * (rOut + tooth * 0.2f), cy + dy * (rOut + tooth * 0.2f));
        }
        g.DrawEllipse(p, cx - rOut + tooth * 0.6f, cy - rOut + tooth * 0.6f,
                         (rOut - tooth * 0.6f) * 2, (rOut - tooth * 0.6f) * 2);
        g.DrawEllipse(p, cx - rIn * 0.62f, cy - rIn * 0.62f, rIn * 1.24f, rIn * 1.24f);
    }

    public static void Plus(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height);
        g.DrawLine(p, r.X + s * 0.5f, r.Y + s * 0.18f, r.X + s * 0.5f, r.Y + s * 0.82f);
        g.DrawLine(p, r.X + s * 0.18f, r.Y + s * 0.5f, r.X + s * 0.82f, r.Y + s * 0.5f);
    }

    public static void Mic(Graphics g, RectangleF r, Color c, bool off, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        var caps = new RectangleF(x + s * 0.36f, y + s * 0.14f, s * 0.28f, s * 0.44f);
        using (var path = new GraphicsPath())
        {
            float d = caps.Width;
            path.AddArc(caps.X, caps.Y, d, d, 180, 180);
            path.AddArc(caps.X, caps.Bottom - d, d, d, 0, 180);
            path.CloseFigure();
            g.DrawPath(p, path);
        }
        g.DrawArc(p, x + s * 0.22f, y + s * 0.36f, s * 0.56f, s * 0.46f, 20, 140);
        g.DrawLine(p, x + s * 0.5f, y + s * 0.78f, x + s * 0.5f, y + s * 0.90f);
        if (off) g.DrawLine(p, x + s * 0.14f, y + s * 0.10f, x + s * 0.86f, y + s * 0.90f);
    }

    /// <summary>Seta saindo da porta — sair da call.</summary>
    public static void Exit(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        g.DrawLines(p, new[]
        {
            new PointF(x + s * 0.42f, y + s * 0.16f), new PointF(x + s * 0.16f, y + s * 0.16f),
            new PointF(x + s * 0.16f, y + s * 0.84f), new PointF(x + s * 0.42f, y + s * 0.84f),
        });
        g.DrawLine(p, x + s * 0.38f, y + s * 0.5f, x + s * 0.84f, y + s * 0.5f);
        g.DrawLines(p, new[]
        {
            new PointF(x + s * 0.66f, y + s * 0.32f), new PointF(x + s * 0.84f, y + s * 0.5f),
            new PointF(x + s * 0.66f, y + s * 0.68f),
        });
    }

    /// <summary>Monitor — compartilhar tela. Com risco quando desligado.</summary>
    public static void Screen(Graphics g, RectangleF r, Color c, bool on, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        g.DrawRectangle(p, x + s * 0.10f, y + s * 0.18f, s * 0.80f, s * 0.54f);
        g.DrawLine(p, x + s * 0.34f, y + s * 0.86f, x + s * 0.66f, y + s * 0.86f);
        g.DrawLine(p, x + s * 0.5f, y + s * 0.72f, x + s * 0.5f, y + s * 0.86f);
        if (!on) g.DrawLine(p, x + s * 0.14f, y + s * 0.12f, x + s * 0.86f, y + s * 0.88f);
    }

    /// <summary>Filmadora — camera. Com risco quando desligada, igual ao monitor.</summary>
    public static void Camera(Graphics g, RectangleF r, Color c, bool on, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        // Corpo.
        g.DrawRectangle(p, x + s * 0.10f, y + s * 0.28f, s * 0.54f, s * 0.44f);
        // Bico apontando pra direita.
        g.DrawLines(p, new[]
        {
            new PointF(x + s * 0.66f, y + s * 0.44f), new PointF(x + s * 0.88f, y + s * 0.30f),
            new PointF(x + s * 0.88f, y + s * 0.70f), new PointF(x + s * 0.66f, y + s * 0.56f),
        });
        if (!on) g.DrawLine(p, x + s * 0.14f, y + s * 0.12f, x + s * 0.86f, y + s * 0.88f);
    }

    /// <summary>Circulo com ponto — buffer de gravacao.</summary>
    public static void Record(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height);
        g.DrawEllipse(p, r.X + s * 0.10f, r.Y + s * 0.10f, s * 0.80f, s * 0.80f);
        using var b = new SolidBrush(c);
        g.FillEllipse(b, r.X + s * 0.34f, r.Y + s * 0.34f, s * 0.32f, s * 0.32f);
    }

    /// <summary>Tesoura — clipe.</summary>
    public static void Scissors(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        g.DrawEllipse(p, x + s * 0.08f, y + s * 0.62f, s * 0.28f, s * 0.28f);
        g.DrawEllipse(p, x + s * 0.64f, y + s * 0.62f, s * 0.28f, s * 0.28f);
        g.DrawLine(p, x + s * 0.22f, y + s * 0.66f, x + s * 0.82f, y + s * 0.10f);
        g.DrawLine(p, x + s * 0.78f, y + s * 0.66f, x + s * 0.18f, y + s * 0.10f);
    }

    /// <summary>Nota musical — modo DJ.</summary>
    public static void Music(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        g.DrawLine(p, x + s * 0.38f, y + s * 0.74f, x + s * 0.38f, y + s * 0.14f);
        g.DrawLine(p, x + s * 0.82f, y + s * 0.62f, x + s * 0.82f, y + s * 0.08f);
        g.DrawLine(p, x + s * 0.38f, y + s * 0.14f, x + s * 0.82f, y + s * 0.08f);
        g.DrawEllipse(p, x + s * 0.16f, y + s * 0.66f, s * 0.24f, s * 0.20f);
        g.DrawEllipse(p, x + s * 0.60f, y + s * 0.54f, s * 0.24f, s * 0.20f);
    }

    /// <summary>Claquete — sessao cinema.</summary>
    public static void Film(Graphics g, RectangleF r, Color c, float w = 1.8f)
    {
        using var p = P(c, w);
        float s = Math.Min(r.Width, r.Height), x = r.X, y = r.Y;
        g.DrawRectangle(p, x + s * 0.08f, y + s * 0.34f, s * 0.84f, s * 0.52f);
        g.DrawLine(p, x + s * 0.08f, y + s * 0.34f, x + s * 0.30f, y + s * 0.14f);
        g.DrawLine(p, x + s * 0.40f, y + s * 0.34f, x + s * 0.62f, y + s * 0.14f);
        g.DrawLine(p, x + s * 0.72f, y + s * 0.34f, x + s * 0.92f, y + s * 0.16f);
        g.DrawLine(p, x + s * 0.08f, y + s * 0.20f, x + s * 0.92f, y + s * 0.14f);
    }

    /// <summary>Bolinha de status (online/offline/em call).</summary>
    public static void StatusDot(Graphics g, RectangleF r, Color fill, Color ring)
    {
        using (var b = new SolidBrush(ring)) g.FillEllipse(b, r);
        var inner = RectangleF.Inflate(r, -2.5f, -2.5f);
        using (var b = new SolidBrush(fill)) g.FillEllipse(b, inner);
    }

    /// <summary>Foto do jogador recortada em circulo (ou a inicial do nick).</summary>
    public static void Avatar(Graphics g, Rectangle box, string nick, Color fallbackBg, Color fallbackFg)
    {
        var photo = Primitivao.AvatarFor(nick);
        if (photo != null)
        {
            using var clip = new GraphicsPath();
            clip.AddEllipse(box);
            var saved = g.Save();
            g.SetClip(clip);
            int side = Math.Min(photo.Width, photo.Height);
            g.DrawImage(photo, box,
                new Rectangle((photo.Width - side) / 2, (photo.Height - side) / 2, side, side),
                GraphicsUnit.Pixel);
            g.Restore(saved);
            return;
        }
        using (var b = new SolidBrush(fallbackBg)) g.FillEllipse(b, box);
        string ini = string.IsNullOrEmpty(nick) ? "?" : nick[..1].ToUpperInvariant();
        using var f = new Font("Bahnschrift", box.Height * 0.42f, FontStyle.Bold);
        using var tb = new SolidBrush(fallbackFg);
        var sz = g.MeasureString(ini, f);
        g.DrawString(ini, f, tb, box.X + (box.Width - sz.Width) / 2, box.Y + (box.Height - sz.Height) / 2);
    }
}
