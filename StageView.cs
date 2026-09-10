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
    private Bitmap? _selfFrame;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SharerNick { get; set; } = "";

    /// <summary>Texto do canto direito: buffer do clipe / gravando / fps.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string StatusRight { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Recording { get; set; }

    /// <summary>
    /// Ligado quando EU sou quem compartilha: em vez do video ao vivo, mostra um
    /// cartao. Ver a propria tela aqui criava espelho infinito (o Primicord aparecia
    /// dentro da propria captura), e como o espelho muda a cada quadro, TODOS os
    /// blocos ficavam sujos sempre — a tela gastava a banda inteira se filmando.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SelfPreview { get; set; }

    /// <summary>Linha de status mostrada no cartao (resolucao, fps, banda).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelfInfo { get; set; } = "";

    /// <summary>Quadro local da fonte, usado apenas na prévia de quem transmite.</summary>
    public void SetSelfFrame(Bitmap? frame)
    {
        try { _selfFrame?.Dispose(); } catch { }
        _selfFrame = frame;
        Invalidate();
    }

    public StageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
    }

    /// <summary>Troca o quadro exibido. O palco assume e libera o clone recebido.</summary>
    public void SetFrame(Bitmap? frame)
    {
        if (ReferenceEquals(_frame, frame)) return;
        try { _frame?.Dispose(); } catch { }
        _frame = frame;
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _frame?.Dispose(); } catch { }
            try { _selfFrame?.Dispose(); } catch { }
            _frame = null;
            _selfFrame = null;
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (SelfPreview) { DrawSelfCard(g); return; }

        var f = _frame;
        if (f == null)
        {
            int cw = Math.Min(420, Math.Max(240, Width - 48));
            var card = new Rectangle((Width - cw) / 2, Math.Max(24, (Height - 156) / 2), cw, 156);
            using (var b = new SolidBrush(Pv.Char2))
            using (var path = Pv.RoundRect(card, 12)) g.FillPath(b, path);
            using (var p = new Pen(Pv.OrangeDim, 1))
            using (var path = Pv.RoundRect(card, 12)) g.DrawPath(p, path);
            BrandAssets.Draw(g, new Rectangle(card.X + 18, card.Y + 26, 70, 64));
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString("Palco livre", Pv.DisplaySm, b, card.X + 106, card.Y + 34);
            using (var b = new SolidBrush(Pv.BoneDim))
                g.DrawString("Clique em TELA para começar\nou escolha uma transmissão abaixo.",
                    Pv.Body, b, new RectangleF(card.X + 106, card.Y + 68, card.Width - 124, 48));
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

    /// <summary>Cartao de "voce esta transmitindo" — sem video, sem espelho.</summary>
    private void DrawSelfCard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var preview = _selfFrame;
        int cw = preview != null ? Math.Min(760, Width - 60) : Math.Min(520, Width - 60);
        int ch = preview != null ? Math.Min(430, Height - 80) : 190;
        var card = new Rectangle((Width - cw) / 2, (Height - ch) / 2, cw, ch);
        using (var b = new SolidBrush(Pv.Char2))
        using (var p = Pv.RoundRect(card, 8))
            g.FillPath(b, p);
        using (var pen = new Pen(Pv.Orange, 2))
        using (var p = Pv.RoundRect(card, 8))
            g.DrawPath(pen, p);

        if (preview != null)
        {
            DrawLocalPreview(g, card, preview);
            return;
        }

        // Monitor estilizado, so pra dar cara de "transmitindo" quando o primeiro
        // quadro ainda está sendo capturado.
        var icon = new RectangleF(card.X + (cw - 56) / 2f, card.Y + 26, 56, 56);
        Glyphs.Speaker(g, icon, Pv.Orange, 2f);

        using (var b = new SolidBrush(Pv.Bone))
        {
            const string t = "VOCE ESTA TRANSMITINDO";
            float w = Pv.TrackedWidth(g, t, Pv.DisplaySm, 1.6f);
            Pv.DrawTracked(g, t, Pv.DisplaySm, b, card.X + (cw - w) / 2f, card.Y + 96, 1.6f);
        }
        using (var b = new SolidBrush(Pv.BoneDim))
        {
            const string t = "a galera esta vendo — sua propria tela nao aparece aqui";
            var sz = g.MeasureString(t, Pv.Body);
            g.DrawString(t, Pv.Body, b, card.X + (cw - sz.Width) / 2, card.Y + 128);
        }
        if (SelfInfo.Length > 0)
            using (var b = new SolidBrush(Pv.Green))
            {
                float w = Pv.TrackedWidth(g, SelfInfo, Pv.Label, 1.4f);
                Pv.DrawTracked(g, SelfInfo, Pv.Label, b, card.X + (cw - w) / 2f, card.Y + 154, 1.4f);
            }

        if (StatusRight.Length > 0) DrawTag(g, StatusRight, false);
    }

    private void DrawLocalPreview(Graphics g, Rectangle card, Bitmap preview)
    {
        int margin = 18;
        var area = new Rectangle(card.X + margin, card.Y + margin,
            Math.Max(1, card.Width - margin * 2), Math.Max(1, card.Height - margin * 2));
        double scale = Math.Min(area.Width / (double)preview.Width, area.Height / (double)preview.Height);
        int w = Math.Max(1, (int)(preview.Width * scale));
        int h = Math.Max(1, (int)(preview.Height * scale));
        var dest = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        try { g.DrawImage(preview, dest); } catch { }
        using var shade = new SolidBrush(Color.FromArgb(170, Pv.Charcoal));
        g.FillRectangle(shade, dest.X, dest.Y, Math.Min(dest.Width, 250), 27);
        using var p = new Pen(Pv.Orange, 2);
        g.DrawRectangle(p, dest);
        using var b = new SolidBrush(Pv.Bone);
        g.DrawString("PRÉVIA LOCAL · VOCÊ TRANSMITE", Pv.Label, b, dest.X + 10, dest.Y + 7);
        if (SelfInfo.Length > 0) DrawTag(g, SelfInfo, false);
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
