using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// O palco: mostra a tela compartilhada em foco, em cima de fundo preto, com um
/// selo de quem esta transmitindo e o estado do buffer de clipe.
/// </summary>
public sealed class StageView : Control
{
    private Bitmap? _frame;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SharerNick { get; set; } = "";

    /// <summary>Texto do canto direito: buffer do clipe / gravando / fps.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string StatusRight { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Recording { get; set; }

    public StageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
    }

    /// <summary>Troca o quadro exibido. O bitmap pertence ao ScreenReceiver — nao dispomos.</summary>
    public void SetFrame(Bitmap? frame)
    {
        _frame = frame;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        var f = _frame;
        if (f == null)
        {
            using var b = new SolidBrush(Pv.BoneDim);
            const string msg = "Ninguem compartilhando tela";
            var sz = g.MeasureString(msg, Pv.Body);
            g.DrawString(msg, Pv.Body, b, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
            return;
        }

        // Encaixa mantendo proporcao (letterbox).
        double scale = Math.Min(Width / (double)f.Width, Height / (double)f.Height);
        int w = (int)(f.Width * scale), h = (int)(f.Height * scale);
        var dest = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);

        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try { g.DrawImage(f, dest); } catch { /* bitmap trocado no meio do paint */ }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (SharerNick.Length > 0) DrawTag(g, "TELA DE " + SharerNick.ToUpperInvariant(), true);
        if (StatusRight.Length > 0) DrawTag(g, StatusRight, false);
    }

    private void DrawTag(Graphics g, string text, bool left)
    {
        float tw = Pv.TrackedWidth(g, text, Pv.Label, 1.6f);
        var box = new Rectangle(left ? 12 : (int)(Width - tw - 34), 12, (int)tw + 22, 24);
        if (!left && Recording) box.X -= 14;

        using (var b = new SolidBrush(Color.FromArgb(200, Pv.Charcoal)))
        using (var path = Pv.RoundRect(box, 4))
            g.FillPath(b, path);
        using (var p = new Pen(left ? Pv.Orange : Pv.Char3, 1))
        using (var path = Pv.RoundRect(box, 4))
            g.DrawPath(p, path);

        float tx = box.X + 11;
        if (!left && Recording)
        {
            using var rb = new SolidBrush(Pv.Red);
            g.FillEllipse(rb, box.X + 8, box.Y + 9, 7, 7);
            tx += 12;
        }
        using var tb = new SolidBrush(left ? Pv.Orange : Pv.Bone);
        Pv.DrawTracked(g, text, Pv.Label, tb, tx, box.Y + 6, 1.6f);
    }
}
