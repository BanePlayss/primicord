using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Clipped channel viewport with a narrow, keyboard-operable scrollbar.</summary>
internal sealed class RailScrollHost : Panel
{
    public RailListPanel Content { get; } = new() { Dock = DockStyle.Fill };
    public RailScrollHost()
    {
        Dock = DockStyle.Fill;
        BackColor = Pv.Char2;
        var thumb = new ScrollThumb(Content) { Dock = DockStyle.Right, Width = 10 };
        Controls.Add(Content);
        Controls.Add(thumb);
        Content.ScrollChanged += () => { thumb.Visible = Content.Maximum > 0; thumb.Invalidate(); };
    }

    private sealed class ScrollThumb : Control
    {
        private readonly RailListPanel _content;
        private int _dragOffset;
        public ScrollThumb(RailListPanel content)
        {
            _content = content;
            BackColor = Pv.Char2;
            TabStop = true;
            AccessibleName = "Rolar lista de canais";
            AccessibleRole = AccessibleRole.ScrollBar;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        private Rectangle Thumb
        {
            get
            {
                int height = Math.Min(Height,Math.Max(28,Height*_content.Height/Math.Max(1,_content.TotalHeight)));
                int top = _content.Maximum == 0 ? 0 : _content.Offset*(Height-height)/_content.Maximum;
                return new Rectangle(2,top,6,height);
            }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            var thumb = Thumb;
            if (thumb.Contains(e.Location)) { _dragOffset=e.Y-thumb.Y; Capture=true; }
            else _content.ScrollTo(_content.Offset + (e.Y < thumb.Top ? -1 : 1)*_content.Height);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture) _content.ScrollTo((int)((long)(e.Y-_dragOffset)*_content.Maximum/Math.Max(1,Height-Thumb.Height)));
        }
        protected override void OnMouseUp(MouseEventArgs e) { Capture=false; base.OnMouseUp(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { _content.ScrollTo(_content.Offset-e.Delta); base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int current=_content.Offset;
            int? next=e.KeyCode switch { Keys.Up=>current-34, Keys.Down=>current+34, Keys.Home=>0, Keys.End=>_content.Maximum,
                Keys.PageUp=>current-Height, Keys.PageDown=>current+Height, _=>null };
            if (next.HasValue) { _content.ScrollTo(next.Value); e.SuppressKeyPress=true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Height < 10) return;
            e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using var path=Pv.RoundRect(Thumb,3);
            using var brush=new SolidBrush(Focused ? Pv.NitroPurple : Pv.Char3);
            e.Graphics.FillPath(brush,path);
        }
    }
}

/// <summary>Top-stacked rail items. Unlike AutoScroll it has no native light chrome.</summary>
internal sealed class RailListPanel : Panel
{
    public int Offset { get; private set; }
    public int TotalHeight => Controls.Cast<Control>().Sum(c => c.Height)+16;
    public int Maximum => Math.Max(0,TotalHeight-ClientSize.Height);
    public event Action? ScrollChanged;
    private bool _arranging;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new Point AutoScrollPosition { get => new(0,-Offset); set => ScrollTo(value.Y); }
    public RailListPanel() { BackColor=Pv.Char2; DoubleBuffered=true; }
    public void ScrollTo(int offset) { Offset=Math.Clamp(offset,0,Maximum); PerformLayout(); }
    protected override void OnControlAdded(ControlEventArgs e)
    {
        if (e.Control is { } child)
        {
            child.Dock=DockStyle.None;
            child.Enter += (_, _) =>
            {
                if (child.Top < 0) ScrollTo(Offset+child.Top-8);
                else if (child.Bottom > Height) ScrollTo(Offset+child.Bottom-Height+8);
            };
        }
        base.OnControlAdded(e);
    }
    protected override void OnLayout(LayoutEventArgs e)
    {
        if (_arranging) return;
        _arranging=true;
        try
        {
            Offset=Math.Clamp(Offset,0,Maximum);
            int y=8-Offset;
            foreach (Control child in Controls.Cast<Control>().Reverse())
            { child.SetBounds(0,y,ClientSize.Width,child.Height); y+=child.Height; }
        }
        finally { _arranging=false; }
        base.OnLayout(e);
        ScrollChanged?.Invoke();
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        int lines=SystemInformation.MouseWheelScrollLines;
        if (lines != 0) ScrollTo(Offset-(e.Delta/120)*(lines < 0 ? Height : lines*24));
        if (e is HandledMouseEventArgs handled) handled.Handled=true;
    }
}
