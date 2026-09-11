using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>A real, keyboard-operable category; its add action is a separate button.</summary>
public sealed class RailSection : Panel
{
    public RailSection(string title, bool collapsed, Action toggle, Func<Task>? add = null)
    {
        Dock = DockStyle.Top;
        Height = 34;
        BackColor = Pv.Char2;
        var disclosure = new Disclosure(title, collapsed) { Dock = DockStyle.Fill };
        disclosure.Click += (_, _) => toggle();
        Controls.Add(disclosure);
        if (add != null)
        {
            var plus = new GlyphButton(Glyphs.Plus) { Dock = DockStyle.Right, Width = 32,
                ToolTipText = "Criar sala", AccessibleName = "Criar sala" };
            plus.Click += async (_, _) => await add();
            Controls.Add(plus);
        }
    }

    private sealed class Disclosure : Control
    {
        private readonly bool _collapsed;
        private bool _hover;
        public Disclosure(string title, bool collapsed)
        {
            Text = title;
            _collapsed = collapsed;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
            AccessibleName = (collapsed ? "Expandir " : "Recolher ") + title;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Space or Keys.Enter || e.KeyCode == Keys.Left && !_collapsed || e.KeyCode == Keys.Right && _collapsed)
            { e.SuppressKeyPress = true; OnClick(EventArgs.Empty); return; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(_hover ? Pv.Bone : Pv.BoneDim, 1.5f);
            g.DrawLines(pen, _collapsed ? new[] { new Point(13,13), new Point(17,17), new Point(13,21) }
                : new[] { new Point(11,15), new Point(15,19), new Point(19,15) });
            TextRenderer.DrawText(g, Text, Pv.Label, new Rectangle(26,0,Math.Max(1,Width-30),Height),
                _hover ? Pv.Bone : Pv.BoneDim, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle,-3,-3), Pv.NitroPurple, BackColor);
        }
    }
}
