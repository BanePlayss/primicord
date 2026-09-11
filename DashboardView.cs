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
        var hero = new Hero { Dock = DockStyle.Top, Height = 238 };
        hero.Join += () => JoinRequested?.Invoke();
        hero.Share += () => ShareRequested?.Invoke();
        hero.Jam += () => DjRequested?.Invoke();
        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 190, ColumnCount = 2,
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
        bottom.Controls.Add(Card("SERVIDORES DE JOGOS ABERTOS",_servers,"VER SERVIDORES",() => ServersRequested?.Invoke()),0,0);
        bottom.Controls.Add(Card("ATIVIDADE DA TRIBO",_activity,"VER CLIPES",() => ClipsRequested?.Invoke()),1,0);
        Controls.Add(_rooms); Controls.Add(bottom); Controls.Add(_summary); Controls.Add(hero);
        _rooms.Resize += (_,_) => LayoutCards();
    }

    public void Configure(IReadOnlyList<RoomInfo> rooms, string tailnet, int onlineMembers, bool preview)
    {
        _summary.Text = $"SALAS ATIVAS    /    {onlineMembers} NA TRIBO    /    {tailnet}";
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
        int columns = _rooms.ClientSize.Width >= 870 ? 3 : 2;
        int width = Math.Max(230,(_rooms.ClientSize.Width-48)/columns-12);
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
                ("ENTRAR NA CALL",() => Join?.Invoke(),PrimButton.Style.Solid),
                ("TRANSMITIR AGORA",() => Share?.Invoke(),PrimButton.Style.Ghost),
                ("♪ JAM DO SPOTIFY",() => Jam?.Invoke(),PrimButton.Style.Ghost) })
            {
                var button = new PrimButton(text,style) { Size = new Size(176,44), Margin = new Padding(0,0,10,0) };
                button.Click += (_,_) => action(); _actions.Controls.Add(button);
            }
            Controls.Add(_actions);
            Resize += (_,_) => { _actions.SetBounds(Width > 900 ? 200 : 36,174,Math.Max(1,Width-60),50); Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            var box=new Rectangle(18,16,Math.Max(1,Width-36),210);
            using(var path=Pv.RoundRect(box,12))
            {
                using var bg=new SolidBrush(Pv.Char2); g.FillPath(bg,path);
                using var pen=new Pen(Pv.OrangeDim,1); g.DrawPath(pen,path);
            }
            int x=Width>900?200:36;
            if (Width > 900)
            {
                using var orbit = new Pen(Color.FromArgb(100, Pv.Orange), 2);
                using var orbitDim = new Pen(Pv.Border, 1);
                g.DrawArc(orbitDim, 38, 43, 139, 139, -100, 310);
                g.DrawArc(orbit, 38, 43, 139, 139, -100, (float)(310*_intro.Value));
            }
            if(Width>900) BrandAssets.Draw(g,new Rectangle(45,55,124,114));
            g.TranslateTransform(0, (float)((1-_intro.Value)*10));
            using var accent=new SolidBrush(Pv.Orange);
            using var bone=new SolidBrush(Pv.Bone);
            using var dim=new SolidBrush(Pv.BoneDim);
            g.DrawString("A TRIBO ESTÁ ONLINE",Pv.Label,accent,x,40);
            using var title=new Font("Bahnschrift",32,FontStyle.Bold);
            g.DrawString("O PALCO É SEU",title,bone,x-2,66);
            g.DrawString("Entre na sala, transmita sua tela e fique perto da Tribo.",Pv.Body,dim,x,133);
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
            _room=room; Size=new Size(300,242); Margin=new Padding(0,0,12,12); Cursor=Cursors.Hand; TabStop=true;
            AccessibleName="Entrar na sala "+room.Name;
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw,true);
        }
        public void Reveal(int delay) { _entry.Snap(0); _entry.To(1, 340, delay); }
        protected override void OnMouseEnter(EventArgs e) { _hover.To(1, 200); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover.To(0, 240); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if(e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
            g.TranslateTransform(0, (float)((1-_entry.Value)*10 + (1-_hover.Value)*2));
            using var path=Pv.RoundRect(new Rectangle(1,1,Width-3,Height-3),12);
            using var bg=new SolidBrush(UiMotion.Blend(Pv.Char2, Pv.SurfaceHover, _hover.Value)); g.FillPath(bg,path);
            using var border=new Pen(_room.LiveStreams>0 || Focused?Pv.Orange:UiMotion.Blend(Pv.Border,Pv.Orange,_hover.Value),1); g.DrawPath(border,path);
            using var bone=new SolidBrush(Pv.Bone); using var dim=new SolidBrush(Pv.BoneDim);
            g.DrawString(_room.Name,Pv.DisplaySm,bone,new RectangleF(16,18,Width-32,28));
            g.DrawString($"{_room.Count} na voz · {_room.JamCount} na jam",Pv.Body,dim,16,51);
            var stage=new Rectangle(16,83,Width-32,91);
            using var surface=new SolidBrush(Pv.SurfaceLowest); g.FillRectangle(surface,stage);
            using var sf=new StringFormat { Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center };
            g.DrawString(_room.LiveStreams>0?$"● {_room.LiveStreams} TRANSMISSÃO AO VIVO":_room.Count>0?"PALCO LIVRE":"NINGUÉM POR AQUI",Pv.Label,dim,stage,sf);
            int x=16;
            foreach(var nick in _room.Occupants.Take(3)) { Glyphs.Avatar(g,new Rectangle(x,193,28,28),nick,Pv.Orange,Pv.Bone); x+=24; }
            if(_room.Count>3) g.DrawString("+"+(_room.Count-3),Pv.Label,dim,x+10,202);
            using var orange=new SolidBrush(Pv.Orange);
            g.DrawString(_room.Count>0?"ENTRAR →":"ABRIR →",Pv.BodyBold,orange,Width-104,200);
        }
    }
}
