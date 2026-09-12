using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Community overview. Cards use presence; no fabricated live thumbnails.</summary>
public sealed class DashboardView : Panel
{
    private readonly FlowLayoutPanel _rooms = new() { Dock = DockStyle.Fill, AutoScroll = true,
        Padding = new Padding(18,8,18,8), BackColor = Pv.Charcoal };
    private readonly Label _summary = new() { Dock = DockStyle.Top, Height = 34,
        Padding = new Padding(20,8,0,0), Font = Pv.Label, ForeColor = Pv.BoneDim };
    private readonly Label _servers = new() { Dock = DockStyle.Fill, Font = Pv.Body, ForeColor = Pv.BoneDim };
    private readonly Label _activity = new() { Dock = DockStyle.Fill, Font = Pv.Body, ForeColor = Pv.BoneDim };
    private string _signature = "";
    public event Action<string,string>? RoomRequested;
    public event Action? DjRequested;
    public event Action? ServersRequested;
    public event Action? ShareRequested;
    public event Action? ClipsRequested;
    public event Action? JoinRequested;

    public DashboardView()
    {
        BackColor = Pv.Charcoal;
        var hero = new Hero { Dock = DockStyle.Top, Height = 218 };
        hero.Join += () => JoinRequested?.Invoke();
        hero.Share += () => ShareRequested?.Invoke();
        hero.Jam += () => DjRequested?.Invoke();
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 164, ColumnCount = 2,
            Padding = new Padding(18,8,18,16), BackColor = Pv.Charcoal };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,60));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,40));
        Panel Card(string title, Label body, string action, Action callback)
        {
            var card = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), Margin = new Padding(0,0,12,0), BackColor = Pv.Char2 };
            var header = new Label { Dock = DockStyle.Top, Height = 30, Text = title, Font = Pv.Label, ForeColor = Pv.Bone };
            var button = new PrimButton(action, PrimButton.Style.Ghost) { Dock = DockStyle.Bottom, Height = 38 };
            button.Click += (_,_) => callback();
            card.Controls.Add(body); card.Controls.Add(button); card.Controls.Add(header);
            return card;
        }
        bottom.Controls.Add(Card("Servidores de jogos",_servers,"Ver servidores",() => ServersRequested?.Invoke()),0,0);
        bottom.Controls.Add(Card("Atividade da tribo",_activity,"Ver clipes",() => ClipsRequested?.Invoke()),1,0);
        Controls.Add(_rooms); Controls.Add(bottom); Controls.Add(_summary); Controls.Add(hero);
        _rooms.Resize += (_,_) => LayoutCards();
    }

    public void Configure(IReadOnlyList<RoomInfo> rooms, string tailnet, int onlineMembers, bool preview)
    {
        _summary.Text = $"Salas da tribo    ·    {onlineMembers} online    ·    {tailnet}";
        _summary.AutoEllipsis = true;
        _activity.Text = rooms.Any(r => r.Count > 0)
            ? string.Join("\n", rooms.Where(r => r.Count > 0).Take(3).Select(r => $"{r.Name} · {r.Count} na call · {r.LiveStreams} ao vivo"))
            : "A tribo está quieta. Abra uma sala para começar.";
        string signature = string.Join("|",rooms.Select(r => r.Id + ":" + r.Count + ":" + r.LiveStreams + ":" + r.JamCount));
        if (_signature == signature && _rooms.Controls.Count > 0) return;
        _signature = signature;
        foreach (Control c in _rooms.Controls.Cast<Control>().ToList()) c.Dispose();
        _rooms.Controls.Clear();
        if (rooms.Count == 0) _rooms.Controls.Add(new Label { Text = "Nenhuma sala disponível. Use Nova sala no rail.",
            AutoSize = true, ForeColor = Pv.BoneDim, Padding = new Padding(16) });
        int entryDelay = 0;
        foreach (var room in rooms)
        {
            var card = new RoomCard(room);
            card.Click += (_,_) => RoomRequested?.Invoke(room.Id,room.Name);
            _rooms.Controls.Add(card);
            card.Reveal(entryDelay);
            entryDelay = Math.Min(200, entryDelay + 45);
        }
        LayoutCards();
    }

    public void SetServers(IReadOnlyList<GameServerEntry> entries, IReadOnlyDictionary<string,GameServerStatus> statuses)
    {
        _servers.Text = entries.Count == 0 ? "Nenhum servidor cadastrado. Adicione o primeiro."
            : string.Join("\n", entries.Take(3).Select(e => e.Game + " · " + e.Name + " · " +
                (statuses.TryGetValue(e.Id,out var status) ? status.State.ToString() : "não verificado")));
    }

    private void LayoutCards()
    {
        int columns = _rooms.ClientSize.Width >= 870 ? 3 : _rooms.ClientSize.Width >= 550 ? 2 : 1;
        int width = Math.Max(180,(_rooms.ClientSize.Width-52)/columns-12);
        foreach (var card in _rooms.Controls.OfType<RoomCard>()) card.Width = width;
    }

    private sealed class Hero : Panel
    {
        private readonly MotionValue _intro;
        public event Action? Join, Share, Jam;
        private readonly FlowLayoutPanel _actions = new() { Height = 50, WrapContents = false };
        public Hero()
        {
            _intro = new MotionValue(this, 1);
            VisibleChanged += (_, _) => { if (Visible) { _intro.Snap(0); _intro.To(1, 700); } };
            DoubleBuffered = true;
            foreach (var (text,action,style) in new (string,Action,PrimButton.Style)[] {
                ("Entrar na call",() => Join?.Invoke(),PrimButton.Style.Solid),
                ("Transmitir agora",() => Share?.Invoke(),PrimButton.Style.Ghost),
                ("♪ Jam do Spotify",() => Jam?.Invoke(),PrimButton.Style.Ghost) })
            {
                var button = new PrimButton(text,style) { Size = new Size(150,40), Margin = new Padding(0,0,10,0), BackColor = Pv.Char2 };
                button.Click += (_,_) => action(); _actions.Controls.Add(button);
            }
            Controls.Add(_actions);
            _actions.BackColor = Color.Transparent;
            Resize += (_,_) => {
                _actions.SetBounds(36,158,Math.Max(1,Width-72),44);
                foreach (Control button in _actions.Controls) button.Width = Math.Min(150, Math.Max(80,(_actions.Width-30)/3));
                Invalidate();
            };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            Pv.PrepareText(g);
            var box=new Rectangle(18,4,Math.Max(1,Width-36),202);
            using(var path=Pv.RoundRect(box,12))
            {
                using var bg=new SolidBrush(Pv.Char2); g.FillPath(bg,path);
                if (BrandAssets.Camp is { } art)
                {
                    var state = g.Save();
                    g.SetClip(path);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(art, box);
                    // Guarantee contrast for labels even when the artwork is compressed.
                    using var shade = new LinearGradientBrush(box, Color.FromArgb(225,Pv.Char2), Color.FromArgb(0,Pv.Char2), 0f);
                    g.FillRectangle(shade,box);
                    g.Restore(state);
                }
                using var pen=new Pen(Pv.Border,1); g.DrawPath(pen,path);
            }
            int x=36;
            g.TranslateTransform(0, (float)Math.Round((1-_intro.Value)*10));
            using var accent=new SolidBrush(Pv.NitroPurple);
            using var bone=new SolidBrush(Pv.Bone);
            using var dim=new SolidBrush(Pv.BoneDim);
            g.DrawString("PRIMITIVOS DA NOVA ERA",Pv.Label,accent,x,24);
            using var title=new Font("Segoe UI Semibold",Width < 600 ? 24 : 30);
            g.DrawString("Seu lugar é com a tribo.",title,bone,x-2,49);
            g.DrawString("Uma call, seu jogo e todo mundo por perto.",Pv.Body,dim,x,113);
        }
    }

    private sealed class RoomCard : Control
    {
        private readonly RoomInfo _room;
        private readonly MotionValue _hover, _entry;
        public RoomCard(RoomInfo room)
        {
            _hover = new MotionValue(this);
            _entry = new MotionValue(this, 1);
            _room=room; Size=new Size(300,184); Margin=new Padding(0,0,12,12); Cursor=Cursors.Hand; TabStop=true;
            AccessibleRole=AccessibleRole.PushButton;
            AccessibleName="Entrar na sala "+room.Name;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        public void Reveal(int delay) { _entry.Snap(0); _entry.To(1, 340, delay); }
        protected override void OnMouseEnter(EventArgs e) { _hover.To(1, 200); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.To(0, 240); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode is Keys.Enter or Keys.Space) { e.SuppressKeyPress=true; OnClick(EventArgs.Empty); } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            Pv.PrepareText(g);
            g.TranslateTransform(0, (float)Math.Round((1-_entry.Value)*10 + (1-_hover.Value)*2));
            using var path=Pv.RoundRect(new Rectangle(1,1,Width-3,Height-3),12);
            using var bg=new SolidBrush(UiMotion.Blend(Pv.Char2, Pv.SurfaceHover, _hover.Value)); g.FillPath(bg,path);
            using var border=new Pen(Focused?Pv.NitroPurple:UiMotion.Blend(Pv.Border,Pv.NitroPurple,_hover.Value),1); g.DrawPath(border,path);
            using var bone=new SolidBrush(Pv.Bone); using var dim=new SolidBrush(Pv.BoneDim);
            using var nameFormat = new StringFormat { Trimming=StringTrimming.EllipsisCharacter, FormatFlags=StringFormatFlags.NoWrap };
            g.DrawString(_room.Name,Pv.BodyBold,bone,new RectangleF(16,18,Width-32,28),nameFormat);
            g.DrawString($"{_room.Count} na voz · {_room.JamCount} na jam",Pv.Body,dim,16,51);
            var stage=new Rectangle(16,81,Width-32,40);
            using var surface=new SolidBrush(Pv.SurfaceLowest);
            using var stagePath=Pv.RoundRect(stage,8); g.FillPath(surface,stagePath);
            using var sf=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center };
            g.DrawString(_room.LiveStreams>0?$"● {_room.LiveStreams} ao vivo":_room.Count>0?"Call aberta · pode chegar":"Sala livre",Pv.Label,dim,stage,sf);
            int x=16;
            foreach(var nick in _room.Occupants.Take(3)) { Glyphs.Avatar(g,new Rectangle(x,140,28,28),nick,Pv.AvatarColor(nick),Pv.Bone); x+=24; }
            if(_room.Count>3) g.DrawString("+"+(_room.Count-3),Pv.Label,dim,x+10,145);
            using var orange=new SolidBrush(Pv.NitroPurple);
            g.DrawString(_room.Count>0?"Entrar →":"Abrir →",Pv.BodyBold,orange,Width-92,145);
        }
    }
}
