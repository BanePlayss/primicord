using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// O palco: mostra a tela compartilhada em foco, em cima de fundo preto, com um
/// selo de quem esta transmitindo e o estado do buffer de clipe.
/// </summary>
public sealed class StageView : Control
{
    private ScreenReceiver? _frames;
    private uint _senderId;
    private bool _paintErrorLogged;
    private readonly object _selfFrameLock = new();
    private Bitmap? _selfFrame;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SharerNick { get; set; } = "";

    /// <summary>Texto do canto direito: buffer do clipe / gravando / fps.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string StatusRight { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Recording { get; set; }

    /// <summary>
    /// Ligado quando EU sou quem compartilha: mostra a previa local recebida do
    /// capturador. Se o Primicord estiver visivel na area escolhida, ele aparece
    /// normalmente no quadro, como qualquer outra janela.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool SelfPreview { get; set; }

    /// <summary>Linha de status mostrada no cartao (resolucao, fps, banda).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string SelfInfo { get; set; } = "";

    public StageView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Black;
    }

    /// <summary>Aponta o palco para a tela remontada de uma pessoa.</summary>
    public void SetFrame(ScreenReceiver? frames, uint senderId = 0)
    {
        _frames = frames;
        _senderId = senderId;
        Invalidate();
    }

    /// <summary>Troca a previa local de forma segura entre a thread de captura e a UI.</summary>
    public void SetSelfFrame(byte[] jpeg)
    {
        Bitmap next;
        try
        {
            using var ms = new MemoryStream(jpeg, writable: false);
            using var decoded = Image.FromStream(ms);
            next = new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Write("previa local nao decodificou: " + ex.Message);
            return;
        }

        lock (_selfFrameLock)
        {
            var old = _selfFrame;
            _selfFrame = next;
            try { old?.Dispose(); } catch { }
        }
        RequestInvalidate();
    }

    public void ClearSelfFrame()
    {
        lock (_selfFrameLock)
        {
            try { _selfFrame?.Dispose(); } catch { }
            _selfFrame = null;
        }
        RequestInvalidate();
    }

    private void RequestInvalidate()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(Invalidate);
            else Invalidate();
        }
        catch { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (SelfPreview)
        {
            bool drewSelf = false;
            lock (_selfFrameLock)
                if (_selfFrame != null)
                {
                    DrawFrame(g, _selfFrame);
                    drewSelf = true;
                }
            if (!drewSelf) DrawSelfCard(g);
            else
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                DrawTag(g, "SUA TELA · AO VIVO", true);
                if (StatusRight.Length > 0) DrawTag(g, StatusRight, false);
            }
            return;
        }

        bool drewFrame = false;
        try
        {
            drewFrame = _frames?.UseFrame(_senderId, f => DrawFrame(g, f)) == true;
            _paintErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (!_paintErrorLogged)
            {
                Log.Write("palco: quadro nao desenhou: " + ex.Message);
                _paintErrorLogged = true;
            }
        }

        if (!drewFrame)
        {
            using var b = new SolidBrush(Pv.BoneDim);
            const string msg = "Ninguem compartilhando tela";
            var sz = g.MeasureString(msg, Pv.Body);
            g.DrawString(msg, Pv.Body, b, (Width - sz.Width) / 2, (Height - sz.Height) / 2);
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (SharerNick.Length > 0) DrawTag(g, "TELA DE " + SharerNick.ToUpperInvariant(), true);
        if (StatusRight.Length > 0) DrawTag(g, StatusRight, false);
    }

    private void DrawFrame(Graphics g, Bitmap frame)
    {
        // Encaixa mantendo proporcao (letterbox). O ScreenReceiver segura o mesmo
        // lock usado para escrever os blocos durante toda esta chamada.
        var dest = FitFrame(new Size(frame.Width, frame.Height), ClientRectangle);
        if (dest.Width <= 0 || dest.Height <= 0) return;

        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(frame, dest);
    }

    /// <summary>
    /// Retangulo de exibicao sem crop nem ampliacao desproporcional. Publico para
    /// o contrato poder ser testado sem depender de uma tela fisica.
    /// </summary>
    public static Rectangle FitFrame(Size frame, Rectangle viewport)
    {
        if (frame.Width <= 0 || frame.Height <= 0 ||
            viewport.Width <= 0 || viewport.Height <= 0) return Rectangle.Empty;

        double scale = Math.Min(
            viewport.Width / (double)frame.Width,
            viewport.Height / (double)frame.Height);
        int width = Math.Max(1, (int)Math.Round(frame.Width * scale));
        int height = Math.Max(1, (int)Math.Round(frame.Height * scale));
        return new Rectangle(
            viewport.X + (viewport.Width - width) / 2,
            viewport.Y + (viewport.Height - height) / 2,
            width,
            height);
    }

    /// <summary>Cartao temporario enquanto o primeiro quadro local ainda nao chegou.</summary>
    private void DrawSelfCard(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int cw = Math.Min(520, Width - 60), ch = 190;
        var card = new Rectangle((Width - cw) / 2, (Height - ch) / 2, cw, ch);
        using (var b = new SolidBrush(Pv.Char2))
        using (var p = Pv.RoundRect(card, 8))
            g.FillPath(b, p);
        using (var pen = new Pen(Pv.Orange, 2))
        using (var p = Pv.RoundRect(card, 8))
            g.DrawPath(pen, p);

        // Monitor estilizado, so pra dar cara de "transmitindo".
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
            const string t = "preparando sua prévia local...";
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_selfFrameLock)
            {
                try { _selfFrame?.Dispose(); } catch { }
                _selfFrame = null;
            }
        }
        base.Dispose(disposing);
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
