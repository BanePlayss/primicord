using System.Drawing.Drawing2D;

namespace Primicord;

public static class SpotifyJam
{
    public static bool TryInvite(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme != "https" || candidate.UserInfo.Length != 0 ||
            !candidate.IsDefaultPort || candidate.AbsolutePath == "/") return false;
        if (candidate.Host != "spotify.link" && candidate.Host != "open.spotify.com") return false;
        uri = candidate;
        return true;
    }
}

/// <summary>Presence only. Playback, queue and actual membership belong to Spotify.</summary>
public sealed class JamPanel : Panel
{
    private readonly Label _people = new() { Dock = DockStyle.Fill, Font = Pv.Body, ForeColor = Pv.BoneDim };
    private readonly Label _title = new() { Dock = DockStyle.Top, Height = 30, Font = Pv.BodyBold, ForeColor = Pv.Bone };
    private readonly PrimButton _join = new("ENTRAR NA JAM") { Dock = DockStyle.Bottom, Height = 44 };
    private readonly PrimButton _link = new("COMPARTILHAR CONVITE", PrimButton.Style.Ghost) { Dock = DockStyle.Bottom, Height = 38 };
    public event Action? JoinRequested;
    public event Action? LinkRequested;

    public JamPanel()
    {
        Height = 242; Padding = new Padding(16); BackColor = Pv.SurfaceLow;
        Controls.Add(_people);
        Controls.Add(_link);
        Controls.Add(_join);
        Controls.Add(_title);
        _join.Click += (_,_) => JoinRequested?.Invoke();
        _link.Click += (_,_) => LinkRequested?.Invoke();
        Paint += (_,e) => { using var p = new Pen(Pv.OrangeDim); e.Graphics.DrawLine(p,0,0,Width,0); };
    }

    public void Configure(bool inVoice, bool joined, IReadOnlyList<string> people)
    {
        _title.Text = !inVoice ? "♪  JAM DO SPOTIFY · ENTRE NA CALL"
            : people.Count == 0 ? "♪  JAM DO SPOTIFY · VAZIA"
            : $"♪  JAM DO SPOTIFY · {people.Count} OUVINDO";
        _people.Text = !inVoice ? "Entre em uma sala de voz para ouvir junto."
            : people.Count == 0 ? "Ninguém na jam ainda. Bora abrir?\n\nFila e play ficam no Spotify."
            : string.Join("\n", people.Select(p => "♪  " + p))
              + "\n\nFila e play continuam no Spotify.";
        _join.Enabled = inVoice;
        _join.Text = !inVoice ? "ENTRE EM UMA CALL" : joined ? "SAIR DA JAM" : "ENTRAR NA JAM";
        _link.Enabled = inVoice;
        _join.Invalidate();
    }
}

public sealed class InviteTile : Control
{
    public InviteTile()
    {
        Margin = new Padding(6); Cursor = Cursors.Hand; TabStop = true;
        AccessibleName = "Chamar a tribo, copiar código da sala";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Focused ? Pv.Orange : Pv.BoneDim, 2) { DashStyle = DashStyle.Dash };
        using var path = Pv.RoundRect(new Rectangle(2,2,Math.Max(1,Width-5),Math.Max(1,Height-5)), 12);
        g.DrawPath(pen,path);
        int y = Math.Max(12, Height / 2 - 52);
        g.DrawEllipse(pen,Width/2-24,y,48,48);
        using var b = new SolidBrush(Pv.Bone);
        using var sf = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("+",Pv.Display,b,new RectangleF(0,y+5,Width,40),sf);
        g.DrawString("CHAMAR A TRIBO",Pv.BodyBold,b,new RectangleF(0,y+56,Width,24),sf);
        using var dim = new SolidBrush(Pv.BoneDim);
        g.DrawString("copiar código da sala",Pv.Label,dim,new RectangleF(0,y+80,Width,24),sf);
    }
}
