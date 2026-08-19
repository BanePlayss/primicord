using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>
/// Centro da chamada no estilo Discord: participantes em grade e, durante um
/// compartilhamento, tela grande com miniaturas alinhadas na parte inferior.
/// </summary>
public sealed class CallGrid : Panel
{
    public const uint OwnId = 0;

    private readonly Dictionary<uint, PeerTile> _participants = new();
    private StageView? _stage;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ParticipantCount => _participants.Count;

    public CallGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(17, 18, 20);
    }

    public void AttachStage(StageView stage)
    {
        _stage = stage;
        stage.Visible = false;
        Controls.Add(stage);
        stage.SendToBack();
        LayoutCall();
    }

    public void SetStageVisible(bool visible)
    {
        if (_stage == null || _stage.Visible == visible) return;
        _stage.Visible = visible;
        LayoutCall();
        Invalidate();
    }

    public void SetOwn(PeerTile tile) => SetParticipant(OwnId, tile);

    public void SetParticipant(uint id, PeerTile tile)
    {
        if (_participants.TryGetValue(id, out var existing))
        {
            if (ReferenceEquals(existing, tile)) return;
            Controls.Remove(existing);
            existing.Dispose();
        }

        tile.Margin = Padding.Empty;
        _participants[id] = tile;
        Controls.Add(tile);
        tile.BringToFront();
        _stage?.SendToBack();
        LayoutCall();
    }

    public void RemoveParticipant(uint id)
    {
        if (!_participants.Remove(id, out var tile)) return;
        Controls.Remove(tile);
        tile.Dispose();
        LayoutCall();
    }

    public void ClearParticipants()
    {
        foreach (var tile in _participants.Values)
        {
            Controls.Remove(tile);
            tile.Dispose();
        }
        _participants.Clear();
        LayoutCall();
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutCall();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_stage?.Visible == true) return;

        using var brush = new LinearGradientBrush(ClientRectangle,
            Color.FromArgb(21, 22, 25), Color.FromArgb(11, 12, 14),
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    private void LayoutCall()
    {
        if (ClientSize.Width < 2 || ClientSize.Height < 2) return;

        var tiles = _participants.OrderBy(p => p.Key == OwnId ? 0 : 1)
                                 .ThenBy(p => p.Value.Nick, StringComparer.OrdinalIgnoreCase)
                                 .Select(p => p.Value).ToArray();

        if (_stage?.Visible == true)
        {
            _stage.Bounds = ClientRectangle;
            _stage.SendToBack();
            LayoutThumbnails(tiles);
        }
        else
        {
            LayoutGrid(tiles);
        }

        foreach (var tile in tiles) tile.BringToFront();
    }

    private void LayoutGrid(PeerTile[] tiles)
    {
        if (tiles.Length == 0) return;

        const int outer = 18, gap = 12;
        int areaW = Math.Max(1, Width - outer * 2);
        int areaH = Math.Max(1, Height - outer * 2);
        int bestCols = 1;
        double bestArea = -1;

        for (int cols = 1; cols <= Math.Min(4, tiles.Length); cols++)
        {
            int rows = (tiles.Length + cols - 1) / cols;
            double cellW = (areaW - gap * (cols - 1)) / (double)cols;
            double cellH = (areaH - gap * (rows - 1)) / (double)rows;
            double cardW = Math.Min(cellW, cellH * 16d / 9d);
            double cardH = Math.Min(cellH, cardW * 9d / 16d);
            double used = cardW * cardH * tiles.Length;
            if (used > bestArea) { bestArea = used; bestCols = cols; }
        }

        int bestRows = (tiles.Length + bestCols - 1) / bestCols;
        int cellWidth = (areaW - gap * (bestCols - 1)) / bestCols;
        int cellHeight = (areaH - gap * (bestRows - 1)) / bestRows;
        int cardWidth = Math.Max(120, Math.Min(cellWidth, (int)(cellHeight * 16d / 9d)));
        int cardHeight = Math.Max(92, Math.Min(cellHeight, (int)(cardWidth * 9d / 16d)));
        int gridW = bestCols * cardWidth + (bestCols - 1) * gap;
        int gridH = bestRows * cardHeight + (bestRows - 1) * gap;
        int startX = (Width - gridW) / 2;
        int startY = (Height - gridH) / 2;

        for (int i = 0; i < tiles.Length; i++)
        {
            int row = i / bestCols;
            int itemsInRow = Math.Min(bestCols, tiles.Length - row * bestCols);
            int rowW = itemsInRow * cardWidth + (itemsInRow - 1) * gap;
            int rowX = (Width - rowW) / 2;
            int col = i % bestCols;
            tiles[i].Bounds = new Rectangle(rowX + col * (cardWidth + gap),
                                             startY + row * (cardHeight + gap),
                                             cardWidth, cardHeight);
        }
    }

    private void LayoutThumbnails(PeerTile[] tiles)
    {
        if (tiles.Length == 0) return;

        const int gap = 8, bottom = 12;
        int cardHeight = Math.Clamp(Height / 7, 82, 116);
        int cardWidth = cardHeight * 16 / 9;
        int available = Math.Max(1, Width - 24 - gap * (tiles.Length - 1));
        cardWidth = Math.Min(cardWidth, available / tiles.Length);
        cardWidth = Math.Max(104, cardWidth);
        int total = cardWidth * tiles.Length + gap * (tiles.Length - 1);
        int x = Math.Max(12, (Width - total) / 2);
        int y = Height - cardHeight - bottom;

        foreach (var tile in tiles)
        {
            tile.Bounds = new Rectangle(x, y, cardWidth, cardHeight);
            x += cardWidth + gap;
        }
    }
}
