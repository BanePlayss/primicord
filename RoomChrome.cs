using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Primicord;

/// <summary>Cabeçalho compacto da sala: contexto à esquerda e ferramentas à direita.</summary>
public sealed class RoomHeader : Panel
{
    private string _roomName = "SALA";
    private int _participants = 1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string RoomName
    {
        get => _roomName;
        set { _roomName = value; Invalidate(); }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Participants
    {
        get => _participants;
        set { _participants = Math.Max(1, value); Invalidate(); }
    }

    public RoomHeader()
    {
        Height = 56;
        Dock = DockStyle.Top;
        BackColor = Color.FromArgb(19, 15, 13);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var border = new Pen(Pv.Char3, 1))
            g.DrawLine(border, 0, Height - 1, Width, Height - 1);

        string room = RoomName.ToUpperInvariant();
        while (room.Length > 4 && g.MeasureString(room, Pv.DisplaySm).Width > 128)
            room = room[..^1];
        if (!string.Equals(room, RoomName, StringComparison.OrdinalIgnoreCase)) room += "…";

        using (var title = new SolidBrush(Pv.Bone))
            g.DrawString(room, Pv.DisplaySm, title, 16, 17);

        float titleW = Math.Min(130, g.MeasureString(room, Pv.DisplaySm).Width);
        var live = new Rectangle((int)(24 + titleW), 18, 51, 19);
        using (var path = Pv.RoundRect(live, 3))
        using (var fill = new SolidBrush(Pv.Red)) g.FillPath(fill, path);
        using (var label = new SolidBrush(Pv.Bone))
            Pv.DrawTracked(g, "AO VIVO", Pv.Label, label, live.X + 6, live.Y + 4, .5f);

        int infoX = live.Right + 14;
        using (var people = new SolidBrush(Pv.BoneDim))
        {
            g.FillEllipse(people, infoX, 21, 7, 7);
            g.DrawString($"{Participants}/8", Pv.Label, people, infoX + 16, 21);
        }
        using (var peoplePen = new Pen(Pv.BoneDim, 1.4f))
            g.DrawEllipse(peoplePen, infoX - 3, 29, 13, 7);

        int publicX = infoX + 54;
        using (var pen = new Pen(Pv.BoneDim, 1.4f))
            g.DrawEllipse(pen, publicX, 21, 12, 12);
        using (var text = new SolidBrush(Pv.BoneDim))
            Pv.DrawTracked(g, "PUBLICA", Pv.Label, text, publicX + 18, 21, .7f);
    }
}

/// <summary>Atividade local e real da sala, sem notificações fictícias.</summary>
public sealed class RoomActivityView : Control
{
    private int _participants = 1;
    private string _connection = "conectando voz...";
    private string _selfNick = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string LastJoinedUser { get; private set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public DateTimeOffset? LastJoinedAt { get; private set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Participants => _participants;

    public RoomActivityView()
    {
        Dock = DockStyle.Bottom;
        Height = 164;
        BackColor = Pv.Char2;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void UpdateSnapshot(int participants, string connection)
    {
        _participants = Math.Max(1, participants);
        _connection = string.IsNullOrWhiteSpace(connection) ? "conectando voz..." : connection;
        Invalidate();
    }

    /// <summary>
    /// Atualiza o ultimo ingresso observado. O painel guarda isso em memoria; o
    /// banco recebe somente joinedAt dentro da presenca que ja existia.
    /// </summary>
    public void RecordJoin(string nick, DateTimeOffset joinedAt)
    {
        if (string.IsNullOrWhiteSpace(nick)) return;
        if (LastJoinedAt is { } current && joinedAt < current) return;
        LastJoinedUser = nick.Trim();
        LastJoinedAt = joinedAt;
        Invalidate();
    }

    public void Reset(string selfNick, DateTimeOffset joinedAt)
    {
        _selfNick = selfNick.Trim();
        _participants = 1;
        _connection = "conectando voz...";
        LastJoinedUser = "";
        LastJoinedAt = null;
        RecordJoin(_selfNick, joinedAt);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Pv.Char2);

        using (var line = new Pen(Pv.Char3, 1)) g.DrawLine(line, 12, 0, Width - 12, 0);
        using (var heading = new SolidBrush(Pv.Red))
            Pv.DrawTracked(g, "ATIVIDADE DA SALA", Pv.Label, heading, 14, 13, 1.4f);

        var rows = new List<(string Title, string Detail)>
        {
            ($"{_participants} participante{(_participants == 1 ? "" : "s")} na sala", "agora"),
            (_connection, "conexao atual"),
        };
        if (LastJoinedAt is { } joinedAt)
        {
            string who = string.Equals(LastJoinedUser, _selfNick,
                                       StringComparison.OrdinalIgnoreCase)
                ? "Voce" : LastJoinedUser;
            rows.Add(($"{who} entrou na sala", joinedAt.ToLocalTime().ToString("HH:mm")));
        }

        int y = 42;
        foreach (var (title, detail) in rows.Take(3))
        {
            var icon = new Rectangle(14, y + 1, 26, 26);
            using (var path = Pv.RoundRect(icon, 6))
            using (var bg = new SolidBrush(Color.FromArgb(42, 35, 30))) g.FillPath(bg, path);
            using (var dot = new SolidBrush(Pv.Orange)) g.FillEllipse(dot, icon.X + 10, icon.Y + 10, 6, 6);

            using (var primary = new SolidBrush(Pv.Bone))
            {
                string clipped = title;
                while (clipped.Length > 8 && g.MeasureString(clipped, Pv.BodyBold).Width > Width - 62)
                    clipped = clipped[..^1];
                if (clipped != title) clipped = clipped[..^1] + "…";
                g.DrawString(clipped, Pv.BodyBold, primary, 50, y - 1);
            }
            using (var secondary = new SolidBrush(Pv.BoneDim))
                g.DrawString(detail, Pv.Label, secondary, 50, y + 17);
            y += 39;
        }
    }
}
