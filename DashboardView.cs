using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Home do acampamento: deixa a transmissão visível antes da conversa.</summary>
public sealed class DashboardView : Panel
{
    private readonly FlowLayoutPanel _rooms = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
        BackColor = Pv.Charcoal, Padding = new Padding(18, 8, 18, 18),
    };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 30 };
    private string _tailnet = "";
    private IReadOnlyList<RoomInfo> _roomData = Array.Empty<RoomInfo>();
    private string _signature = "";

    public event Action<string, string>? RoomRequested;
    public event Action? DjRequested;
    public event Action? ServersRequested;
    public event Action? ShareRequested;
    public event Action? ClipsRequested;

    public DashboardView()
    {
        BackColor = Pv.Charcoal;
        var hero = new DashboardHero { Dock = DockStyle.Top, Height = 238 };
        hero.ShareRequested += () => ShareRequested?.Invoke();
        hero.DjRequested += () => DjRequested?.Invoke();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 60, WrapContents = false,
            Padding = new Padding(20, 10, 20, 8), BackColor = Pv.Charcoal,
        };
        var servers = new PrimButton("SERVIDORES ABERTOS", PrimButton.Style.Ghost) { Size = new Size(178, 38) };
        servers.Click += (_, _) => ServersRequested?.Invoke();
        var clips = new PrimButton("MEUS CLIPES", PrimButton.Style.Ghost) { Size = new Size(142, 38) };
        clips.Click += (_, _) => ClipsRequested?.Invoke();
        actions.Controls.AddRange(new Control[] { servers, clips });

        _summary.Padding = new Padding(22, 8, 0, 0);
        _summary.Font = Pv.Label;
        _summary.ForeColor = Pv.BoneDim;

        Controls.Add(_rooms);
        Controls.Add(_summary);
        Controls.Add(actions);
        Controls.Add(hero);
    }

    public void Configure(IReadOnlyList<RoomInfo> rooms, string tailnet, int onlineMembers, bool preview)
    {
        _roomData = rooms;
        _tailnet = tailnet;
        _summary.Text = $"SALAS ABERTAS  ·  {rooms.Count} salas  ·  {onlineMembers} tribais online  ·  {tailnet}"
                      + (preview ? "  ·  PRÉVIA LOCAL" : "");
        string signature = string.Join("|", rooms.Select(r => r.Id + ":" + r.Count + ":" + r.LiveStreams));
        if (_signature == signature) return;
        _signature = signature;
        _rooms.SuspendLayout();
        foreach (Control old in _rooms.Controls.Cast<Control>().ToList()) old.Dispose();
        _rooms.Controls.Clear();
        if (rooms.Count == 0)
        {
            _rooms.Controls.Add(new EmptyCard());
        }
        else
        {
            foreach (var room in rooms)
            {
                var card = new RoomCard(room);
                card.Clicked += (id, name) => RoomRequested?.Invoke(id, name);
                _rooms.Controls.Add(card);
            }
        }
        _rooms.ResumeLayout();
    }

    private sealed class DashboardHero : Control
    {
        public event Action? ShareRequested;
        public event Action? DjRequested;
        private readonly PrimButton _share = new("TRANSMITIR AGORA") { Size = new Size(178, 40) };
        private readonly PrimButton _dj = new("ESCUTA DJ", PrimButton.Style.Ghost) { Size = new Size(132, 40) };

        public DashboardHero()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            _share.Click += (_, _) => ShareRequested?.Invoke();
            _dj.Click += (_, _) => DjRequested?.Invoke();
            Controls.Add(_share); Controls.Add(_dj);
            Resize += (_, _) => LayoutButtons();
            LayoutButtons();
        }

        private void LayoutButtons()
        {
            _share.Location = new Point(28, 168);
            _dj.Location = new Point(216, 168);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Pv.Charcoal);
            var box = new Rectangle(18, 16, Math.Max(1, Width - 36), 204);
            using (var b = new SolidBrush(Pv.SurfaceLow))
            using (var p = Pv.RoundRect(box, 14)) g.FillPath(b, p);
            using (var pen = new Pen(Pv.OrangeDim, 1))
            using (var p = Pv.RoundRect(box, 14)) g.DrawPath(pen, p);
            using var glow = new SolidBrush(Color.FromArgb(28, Pv.Orange));
            g.FillEllipse(glow, box.Right - 180, box.Y - 70, 240, 240);
            BrandAssets.Draw(g, new Rectangle(box.Right - 125, box.Y + 26, 96, 88));
            using (var b = new SolidBrush(Pv.Orange))
                Pv.DrawTracked(g, "O PALCO É SEU", Pv.Label, b, box.X + 28, box.Y + 24, 1.5f);
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString("Entra, transmite e fica na call", Pv.Display, b, box.X + 26, box.Y + 51);
            using (var b = new SolidBrush(Pv.BoneDim))
                g.DrawString("Voz, tela em alta taxa de quadros, clipes instantâneos e uma escuta DJ separada.",
                    Pv.Body, b, new RectangleF(box.X + 28, box.Y + 92, box.Width - 210, 42));
            base.OnPaint(e);
        }
    }

    private sealed class RoomCard : Control
    {
        private readonly RoomInfo _room;
        private bool _hover;
        public event Action<string, string>? Clicked;
        public RoomCard(RoomInfo room)
        {
            _room = room; Width = 260; Height = 118; Margin = new Padding(0, 0, 12, 12);
            Cursor = Cursors.Hand; BackColor = Pv.Charcoal;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); }
        protected override void OnClick(EventArgs e) { Clicked?.Invoke(_room.Id, _room.Name); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(_hover ? Pv.SurfaceHover : Pv.Char2))
            using (var p = Pv.RoundRect(r, 10)) g.FillPath(b, p);
            using (var pen = new Pen(_room.LiveStreams > 0 ? Pv.Orange : Pv.Border, 1))
            using (var p = Pv.RoundRect(r, 10)) g.DrawPath(pen, p);
            Glyphs.Speaker(g, new RectangleF(16, 18, 20, 20), Pv.Orange, 2f);
            using (var b = new SolidBrush(Pv.Bone)) g.DrawString(_room.Name, Pv.BodyBold, b, 48, 18);
            using (var b = new SolidBrush(Pv.BoneDim))
                g.DrawString($"{_room.Count} na sala", Pv.Body, b, 48, 44);
            using (var b = new SolidBrush(_room.LiveStreams > 0 ? Pv.Orange : Pv.Muted))
                g.DrawString(_room.LiveStreams > 0 ? "● TRANSMISSÃO AO VIVO" : "sala de voz aberta",
                    Pv.Label, b, 16, 88);
        }
    }

    private sealed class EmptyCard : Control
    {
        public EmptyCard() { Width = 480; Height = 70; BackColor = Pv.Char2; }
        protected override void OnPaint(PaintEventArgs e)
        {
            using var b = new SolidBrush(Pv.BoneDim);
            e.Graphics.DrawString("Nenhuma sala publicada ainda — crie uma sala de voz para abrir o acampamento.",
                Pv.Body, b, 16, 24);
        }
    }
}
