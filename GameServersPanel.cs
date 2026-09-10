using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Lista visual de endpoints que o usuário cadastrou localmente.</summary>
public sealed class GameServersPanel : Panel
{
    private readonly FlowLayoutPanel _list = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = false,
        FlowDirection = FlowDirection.TopDown, BackColor = Pv.Charcoal,
        Padding = new Padding(20, 10, 20, 20),
    };
    private readonly Label _hint = new() { Dock = DockStyle.Top, Height = 52 };

    public event Action? AddRequested;
    public event Action? RefreshRequested;
    public event Action<string>? RemoveRequested;

    public GameServersPanel()
    {
        BackColor = Pv.Charcoal;
        var head = new Panel { Dock = DockStyle.Top, Height = 126, BackColor = Pv.Charcoal };
        head.Paint += (_, e) =>
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Pv.Bone); g.DrawString("Servidores abertos", Pv.Display, b, 22, 18);
            using var d = new SolidBrush(Pv.BoneDim);
            g.DrawString("Minecraft, LoL e qualquer endpoint que a tribo quiser anunciar.", Pv.Body, d, 24, 56);
            g.DrawString("O Primicord testa somente os endereços cadastrados — sem varredura da sua rede.", Pv.Label, d, 24, 78);
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(20, 4, 20, 4), WrapContents = false };
        var add = new PrimButton("ADICIONAR SERVIDOR") { Size = new Size(178, 36) };
        var refresh = new PrimButton("ATUALIZAR STATUS", PrimButton.Style.Ghost) { Size = new Size(160, 36) };
        add.Click += (_, _) => AddRequested?.Invoke();
        refresh.Click += (_, _) => RefreshRequested?.Invoke();
        actions.Controls.AddRange(new Control[] { add, refresh });
        head.Controls.Add(actions);
        _hint.Padding = new Padding(22, 12, 22, 0); _hint.Font = Pv.Body; _hint.ForeColor = Pv.BoneDim;
        Controls.Add(_list); Controls.Add(_hint); Controls.Add(head);
    }

    public void SetStatuses(IReadOnlyList<GameServerStatus> statuses, bool checking = false)
    {
        _hint.Text = statuses.Count == 0
            ? "Nenhum servidor foi cadastrado. Use Adicionar servidor para publicar um endpoint da tribo."
            : (checking ? "Consultando endpoints..." : $"{statuses.Count} endpoint(s) cadastrado(s) · última verificação: {DateTime.Now:HH:mm:ss}");
        _list.SuspendLayout();
        foreach (Control old in _list.Controls.Cast<Control>().ToList()) old.Dispose();
        _list.Controls.Clear();
        foreach (var status in statuses)
        {
            var row = new ServerRow(status) { Width = Math.Max(500, _list.ClientSize.Width - 42) };
            row.Remove += id => RemoveRequested?.Invoke(id);
            _list.Controls.Add(row);
        }
        _list.ResumeLayout();
    }

    private sealed class ServerRow : Control
    {
        private readonly GameServerStatus _status;
        public event Action<string>? Remove;
        public ServerRow(GameServerStatus status)
        {
            _status = status; Height = 84; Margin = new Padding(0, 0, 0, 10);
            BackColor = Pv.Char2; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Remover cadastro", null, (_, _) => Remove?.Invoke(_status.Server.Id));
                menu.Show(this, e.Location);
            }
            base.OnMouseClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Pv.Char2)) using (var p = Pv.RoundRect(r, 10)) g.FillPath(b, p);
            using (var pen = new Pen(ColorFor(_status.State), 2)) g.DrawLine(pen, 0, 12, 0, Height - 12);
            using (var b = new SolidBrush(Pv.Bone)) g.DrawString(_status.Server.Name, Pv.BodyBold, b, 22, 14);
            using (var d = new SolidBrush(Pv.BoneDim)) g.DrawString(_status.Server.Game + "  ·  " + _status.Server.Endpoint, Pv.Body, d, 22, 38);
            string state = _status.State switch
            {
                GameServerState.Online => "● ONLINE · " + (_status.Players is int p ? $"{p}/{_status.MaxPlayers}" : "status confirmado"),
                GameServerState.Reachable => "● PORTA ABERTA · jogo não confirmado",
                GameServerState.Unreachable => "○ OFFLINE / INALCANÇÁVEL",
                GameServerState.InvalidResponse => "! RESPOSTA INVÁLIDA",
                _ => "? AINDA NÃO TESTADO",
            };
            using (var s = new SolidBrush(ColorFor(_status.State))) g.DrawString(state, Pv.Label, s, 22, 61);
            if (_status.LatencyMs is long latency)
                using (var d = new SolidBrush(Pv.BoneDim)) g.DrawString(latency + " ms", Pv.Label, d, Width - 72, 16);
        }
        private static Color ColorFor(GameServerState state) => state switch
        {
            GameServerState.Online => Pv.Green,
            GameServerState.Reachable => Pv.Yellow,
            GameServerState.Unreachable => Pv.Red,
            GameServerState.InvalidResponse => Pv.Yellow,
            _ => Pv.Muted,
        };
    }
}
