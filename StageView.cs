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

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black);

        if (SelfPreview) { DrawSelfCard(g); return; }

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
        double scale = Math.Min(Width / (double)frame.Width, Height / (double)frame.Height);
        int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
        var dest = new Rectangle((Width - w) / 2, (Height - h) / 2, w, h);

        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(frame, dest);
    }

    /// <summary>Cartao de "voce esta transmitindo" — sem video, sem espelho.</summary>
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
