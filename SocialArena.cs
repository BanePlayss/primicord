using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Espaco social da call. Mantem os avatares em coordenadas normalizadas, permite
/// que o jogador mova somente o proprio e aplica uma repulsao visual leve.
/// </summary>
public sealed class SocialArena : Panel
{
    private sealed class Participant
    {
        public required uint Id;
        public required PeerTile Tile;
        public SocialPosition Position;
        public PointF Center;
    }

    public const uint OwnId = 0;

    private readonly Dictionary<uint, Participant> _participants = new();
    private Participant? _dragging;
    private PointF _dragOffset;
    private Control? _stage;
    private Rectangle _minusBox, _plusBox;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RoomName { get; set; } = "SALA";

    /// <summary>false durante o arraste; true ao soltar ou redimensionar.</summary>
    public event Action<SocialPosition, bool>? OwnPositionChanged;

    public SocialArena()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Pv.Charcoal;
        TabStop = true;
    }

    /// <summary>Coloca o palco atras das bolinhas; ele so fica visivel durante tela.</summary>
    public void AttachStage(Control stage)
    {
        _stage = stage;
        stage.Dock = DockStyle.Fill;
        Controls.Add(stage);
        stage.SendToBack();
    }

    public void SetOwn(PeerTile tile, SocialPosition position)
        => SetParticipant(OwnId, tile, position, own: true);

    public void SetParticipant(uint id, PeerTile tile, SocialPosition position)
        => SetParticipant(id, tile, position, own: false);

    private void SetParticipant(uint id, PeerTile tile, SocialPosition position, bool own)
    {
        position = position.Normalized();
        if (_participants.TryGetValue(id, out var existing))
        {
            existing.Position = position;
            existing.Tile.AvatarDiameter = position.Scale;
            LayoutParticipants();
            return;
        }

        tile.IsMe = own;
        tile.AvatarDiameter = position.Scale;
        var p = new Participant { Id = id, Tile = tile, Position = position };
        _participants[id] = p;
        Controls.Add(tile);
        tile.BringToFront();

        if (own)
        {
            // O Windows promove toque simples a estes eventos de mouse no WinForms.
            tile.MouseDown += (_, e) => BeginOwnDrag(p, e);
            tile.MouseMove += (_, e) => ContinueOwnDrag(p, e);
            tile.MouseUp += (_, e) => EndOwnDrag(p, e);
            tile.MouseWheel += (_, e) => ResizeOwn(Math.Sign(e.Delta) * 8);
            tile.MouseEnter += (_, _) => tile.Focus();
        }

        _stage?.SendToBack();
        LayoutParticipants();
    }

    public void SetPosition(uint id, SocialPosition position)
    {
        if (!_participants.TryGetValue(id, out var p)) return;
        p.Position = position.Normalized();
        p.Tile.AvatarDiameter = p.Position.Scale;
        LayoutParticipants();
    }

    public void RemoveParticipant(uint id)
    {
        if (!_participants.Remove(id, out var p)) return;
        Controls.Remove(p.Tile);
        p.Tile.Dispose();
        LayoutParticipants();
    }

    public void ClearParticipants()
    {
        foreach (var p in _participants.Values)
        {
            Controls.Remove(p.Tile);
            p.Tile.Dispose();
        }
        _participants.Clear();
        _dragging = null;
        Invalidate();
    }

    public double? DistanceBetween(uint first, uint second)
        => _participants.TryGetValue(first, out var a) && _participants.TryGetValue(second, out var b)
            ? a.Position.DistanceTo(b.Position)
            : null;

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutParticipants();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_minusBox.Contains(e.Location)) ResizeOwn(-8);
            else if (_plusBox.Contains(e.Location)) ResizeOwn(8);
        }
        base.OnMouseDown(e);
    }

    private void BeginOwnDrag(Participant p, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || p.Id != OwnId) return;
        _dragging = p;
        var at = PointToClient(p.Tile.PointToScreen(e.Location));
        _dragOffset = new PointF(at.X - p.Center.X, at.Y - p.Center.Y);
        p.Tile.Capture = true;
        p.Tile.Cursor = Cursors.SizeAll;
    }

    private void ContinueOwnDrag(Participant p, MouseEventArgs e)
    {
        if (_dragging != p || !p.Tile.Capture) return;
        var at = PointToClient(p.Tile.PointToScreen(e.Location));
        SetOwnCenter(p, new PointF(at.X - _dragOffset.X, at.Y - _dragOffset.Y), final: false);
    }

    private void EndOwnDrag(Participant p, MouseEventArgs e)
    {
        if (_dragging != p) return;
        p.Tile.Capture = false;
        p.Tile.Cursor = Cursors.Hand;
        _dragging = null;
        // Persiste a posicao final depois da repulsao, nao cada pixel percorrido.
        SetOwnCenter(p, p.Center, final: true);
    }

    private void SetOwnCenter(Participant p, PointF center, bool final)
    {
        float x = Math.Clamp(center.X / Math.Max(1f, ClientSize.Width), 0f, 1f);
        float y = Math.Clamp(center.Y / Math.Max(1f, ClientSize.Height), 0f, 1f);
        p.Position = new SocialPosition(x, y, p.Position.Scale).Normalized();
        LayoutParticipants();
        OwnPositionChanged?.Invoke(p.Position, final);
    }

    private void ResizeOwn(int delta)
    {
        if (!_participants.TryGetValue(OwnId, out var own)) return;
        int next = Math.Clamp(own.Position.Scale + delta,
                              SocialPosition.MinScale, SocialPosition.MaxScale);
        if (next == own.Position.Scale) return;
        own.Position = own.Position with { Scale = next };
        own.Tile.AvatarDiameter = next;
        LayoutParticipants();
        OwnPositionChanged?.Invoke(own.Position, true);
    }

    private void LayoutParticipants()
    {
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

        foreach (var p in _participants.Values)
            p.Center = new PointF((float)(p.Position.X * ClientSize.Width),
                                  (float)(p.Position.Y * ClientSize.Height));

        // Quatro passadas curtas bastam para desfazer sobreposicao sem dar sensacao
        // de jogo de fisica. A coordenada persistida nao e alterada por esta animacao.
        var all = _participants.Values.ToArray();
        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 0; i < all.Length; i++)
            for (int j = i + 1; j < all.Length; j++)
            {
                var a = all[i]; var b = all[j];
                float dx = b.Center.X - a.Center.X, dy = b.Center.Y - a.Center.Y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                float min = (a.Position.Scale + b.Position.Scale) / 2f + 12f;
                if (dist >= min) continue;
                if (dist < 0.5f) { dx = 1f; dy = 0f; dist = 1f; }
                float push = (min - dist) * 0.28f;
                float ux = dx / dist, uy = dy / dist;
                a.Center = new PointF(a.Center.X - ux * push, a.Center.Y - uy * push);
                b.Center = new PointF(b.Center.X + ux * push, b.Center.Y + uy * push);
            }
        }

        foreach (var p in all)
        {
            int radius = p.Position.Scale / 2;
            float cx = Math.Clamp(p.Center.X, radius + 8, Math.Max(radius + 8, Width - radius - 8));
            float cy = Math.Clamp(p.Center.Y, radius + 8, Math.Max(radius + 8, Height - radius - 42));
            p.Center = new PointF(cx, cy);
            p.Tile.Location = new Point(
                (int)Math.Round(cx - p.Tile.AvatarCenter.X),
                (int)Math.Round(cy - p.Tile.AvatarCenter.Y));
        }

        _stage?.SendToBack();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_stage?.Visible == true) { DrawSizeControls(e.Graphics); return; }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Pv.Charcoal);

        // Fundo preto/marrom com poeira vermelha, como a referencia, sem copiar
        // a composicao: a identidade continua vindo da paleta Pv e do tema do site.
        var field = Rectangle.Inflate(ClientRectangle, -8, -8);
        using (var fill = new LinearGradientBrush(field, Pv.Char2, Color.FromArgb(12, 8, 6),
                                                   LinearGradientMode.ForwardDiagonal))
        using (var path = Pv.RoundRect(field, 12)) g.FillPath(fill, path);
        using (var border = new Pen(Pv.Char3, 1))
        using (var path = Pv.RoundRect(field, 12)) g.DrawPath(border, path);

        using (var dust = new SolidBrush(Color.FromArgb(55, Pv.Red)))
        {
            for (int i = 0; i < 95; i++)
            {
                int x = field.Left + 12 + (i * 97 + i * i * 17) % Math.Max(1, field.Width - 24);
                int y = field.Top + 28 + (i * 53 + i * i * 11) % Math.Max(1, field.Height - 64);
                int d = i % 9 == 0 ? 2 : 1;
                g.FillEllipse(dust, x, y, d, d);
            }
        }

        using (var b = new SolidBrush(Color.FromArgb(150, Pv.BoneDim)))
            Pv.DrawTracked(g, "ESPACO SOCIAL · " + RoomName.ToUpperInvariant(), Pv.Label, b,
                           field.Left + 16, field.Top + 12, 1.6f);

        const string help = "ARRASTE PARA MOVER   ·   ROLINHO PARA AUMENTAR / DIMINUIR";
        float helpW = Pv.TrackedWidth(g, help, Pv.Label, 1.0f);
        var helpBox = new Rectangle((int)((Width - helpW) / 2 - 14), field.Bottom - 35,
                                    (int)helpW + 28, 26);
        using (var fillHelp = new SolidBrush(Color.FromArgb(210, Pv.Char2)))
        using (var path = Pv.RoundRect(helpBox, 8)) g.FillPath(fillHelp, path);
        using (var borderHelp = new Pen(Pv.Char3, 1))
        using (var path = Pv.RoundRect(helpBox, 8)) g.DrawPath(borderHelp, path);
        using (var b = new SolidBrush(Pv.BoneDim))
            Pv.DrawTracked(g, help, Pv.Label, b, helpBox.Left + 14, helpBox.Top + 7, 1.0f);

        DrawSizeControls(g);
    }

    private void DrawSizeControls(Graphics g)
    {
        const int d = 26, gap = 6;
        _plusBox = new Rectangle(Math.Max(4, Width - 22 - d), Math.Max(4, Height - 38), d, d);
        _minusBox = new Rectangle(_plusBox.Left - gap - d, _plusBox.Top, d, d);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawButton(g, _minusBox, "−");
        DrawButton(g, _plusBox, "+");
    }

    private static void DrawButton(Graphics g, Rectangle box, string text)
    {
        using (var fill = new SolidBrush(Pv.Char3)) g.FillEllipse(fill, box);
        using (var pen = new Pen(Pv.Orange, 1.5f)) g.DrawEllipse(pen, box);
        using var b = new SolidBrush(Pv.Bone);
        using var f = new Font("Segoe UI", 12f, FontStyle.Bold);
        var size = g.MeasureString(text, f);
        g.DrawString(text, f, b, box.X + (box.Width - size.Width) / 2,
                     box.Y + (box.Height - size.Height) / 2 - 1);
    }
}
