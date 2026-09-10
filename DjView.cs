using System.Drawing.Drawing2D;

namespace Primicord;

public sealed record DjParticipant(string Nick, bool IsHost, bool IsMe);

/// <summary>
/// Camada de escuta musical paralela a chamada. Entrar ou sair daqui nunca muda a
/// sala de voz; apenas assina ou remove o fluxo de musica do mixer local.
/// </summary>
public sealed class DjView : Panel
{
    private readonly DjHero _hero = new() { Dock = DockStyle.Top, Height = 204 };
    private readonly Panel _members = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true, BackColor = Pv.Charcoal,
        Padding = new Padding(18, 0, 18, 12),
    };
    private readonly PrimButton _join = new("ENTRAR NA ESCUTA") { Size = new Size(184, 40) };
    private readonly PrimButton _host = new("TRANSMITIR MINHA MUSICA", PrimButton.Style.Ghost)
    { Size = new Size(224, 40) };
    private readonly PrimButton _back = new("VOLTAR PARA A CALL", PrimButton.Style.Ghost)
    { Size = new Size(176, 40) };
    private readonly Label _memberTitle = new()
    {
        Dock = DockStyle.Top, Height = 38, Padding = new Padding(20, 11, 0, 0),
        Font = Pv.Label, ForeColor = Pv.BoneDim, BackColor = Pv.Charcoal,
    };

    private string _signature = "";

    public event Action? JoinToggled;
    public event Action? HostToggled;
    public event Action? BackToVoice;

    public DjView()
    {
        BackColor = Pv.Charcoal;

        var head = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Pv.Charcoal };
        head.Paint += (_, e) =>
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var icon = new RectangleF(18, 18, 20, 20);
            Glyphs.Music(g, icon, Pv.NitroPink, 2f);
            using (var b = new SolidBrush(Pv.Bone)) g.DrawString("DJ · escuta conjunta", Pv.BodyBold, b, 48, 18);
            using (var p = new Pen(Pv.Border, 1)) g.DrawLine(p, 0, head.Height - 1, head.Width, head.Height - 1);
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 108, BackColor = Pv.Charcoal,
            WrapContents = true, Padding = new Padding(20, 10, 20, 10),
        };
        _join.Margin = new Padding(0, 0, 10, 0);
        _host.Margin = new Padding(0, 0, 10, 0);
        _back.Margin = new Padding(0);
        _join.Click += (_, _) => JoinToggled?.Invoke();
        _host.Click += (_, _) => HostToggled?.Invoke();
        _back.Click += (_, _) => BackToVoice?.Invoke();
        actions.Controls.AddRange(new Control[] { _join, _host, _back });

        var footer = new Label
        {
            Dock = DockStyle.Bottom, Height = 38, BackColor = Pv.SurfaceLow,
            ForeColor = Pv.BoneDim, Font = Pv.Body,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "A voz continua com toda a sala. So a musica e separada para quem entrou aqui.",
        };

        Controls.Add(_members);
        Controls.Add(footer);
        Controls.Add(_memberTitle);
        Controls.Add(actions);
        Controls.Add(_hero);
        Controls.Add(head);
    }

    public void Configure(bool inVoice, bool joined, bool hosting, string voiceRoom,
                          string track, string hostNick, IReadOnlyList<DjParticipant> people)
    {
        _hero.InVoice = inVoice;
        _hero.Joined = joined;
        _hero.Hosting = hosting;
        _hero.VoiceRoom = voiceRoom;
        _hero.Track = track;
        _hero.HostNick = hostNick;
        _hero.Invalidate();

        _join.Enabled = inVoice;
        _join.Text = joined ? "SAIR DA ESCUTA" : "ENTRAR NA ESCUTA";
        _join.Kind = joined ? PrimButton.Style.Danger : PrimButton.Style.Solid;
        _join.Invalidate();

        _host.Enabled = inVoice && joined && (hosting || hostNick.Length == 0);
        _host.Text = hosting ? "PARAR TRANSMISSAO"
                   : hostNick.Length > 0 ? hostNick + " E O DJ"
                   : "TRANSMITIR MINHA MUSICA";
        _host.Kind = hosting ? PrimButton.Style.Danger : PrimButton.Style.Ghost;
        _host.Invalidate();

        _back.Enabled = inVoice;
        _memberTitle.Text = !inVoice ? "ENTRE EM UMA SALA DE VOZ PARA USAR O DJ"
                          : $"NA ESCUTA — {people.Count}";

        string signature = inVoice + "|" + string.Join("|", people.Select(p => $"{p.Nick}:{p.IsHost}:{p.IsMe}"));
        if (_signature == signature) return;
        _signature = signature;

        _members.SuspendLayout();
        foreach (Control old in _members.Controls.Cast<Control>().ToList()) old.Dispose();
        _members.Controls.Clear();

        if (people.Count == 0)
        {
            _members.Controls.Add(new Label
            {
                Dock = DockStyle.Top, Height = 54, Font = Pv.Body,
                ForeColor = Pv.BoneDim, BackColor = Pv.Charcoal,
                Text = inVoice ? "Ninguem entrou na escuta ainda." : "A aba DJ fica disponivel durante uma call.",
                Padding = new Padding(8, 16, 0, 0),
            });
        }
        else
        {
            foreach (var person in people.Reverse())
                _members.Controls.Add(new DjMemberRow(person) { Dock = DockStyle.Top });
        }
        _members.ResumeLayout();
    }

    private sealed class DjHero : Control
    {
        public bool InVoice, Joined, Hosting;
        public string VoiceRoom = "", Track = "", HostNick = "";

        public DjHero()
        {
            BackColor = Pv.Charcoal;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var card = new Rectangle(20, 18, Math.Max(1, Width - 40), 168);
            using (var gradient = new LinearGradientBrush(card, Pv.NitroPurple, Pv.NitroPink, 18f))
            using (var path = Pv.RoundRect(card, 14)) g.FillPath(gradient, path);

            using (var glow = new SolidBrush(Color.FromArgb(38, Color.White)))
            {
                g.FillEllipse(glow, card.Right - 160, card.Y - 80, 230, 230);
                g.FillEllipse(glow, card.Right - 260, card.Bottom - 70, 150, 150);
            }

            string title = InVoice ? "Ouvir junto, sem sair da call" : "Entre numa call para comecar";
            using (var b = new SolidBrush(Pv.Bone)) g.DrawString(title, Pv.Display, b, card.X + 24, card.Y + 20);
            using (var b = new SolidBrush(Color.FromArgb(225, Pv.Bone)))
                g.DrawString(InVoice
                    ? $"Voce continua na sala {VoiceRoom}. A musica chega apenas a quem entrou nesta aba."
                    : "Escolha uma sala de voz na barra lateral; depois volte para a aba DJ.",
                    Pv.Body, b, new RectangleF(card.X + 26, card.Y + 62, card.Width - 52, 38));

            string status = !InVoice ? "DJ INDISPONIVEL"
                          : Hosting ? "VOCE ESTA TRANSMITINDO"
                          : HostNick.Length > 0 ? "DJ: " + HostNick.ToUpperInvariant()
                          : Joined ? "NA ESCUTA · AGUARDANDO UM DJ"
                          : "FORA DA ESCUTA";
            using (var b = new SolidBrush(Pv.Bone))
            using (var path = Pv.RoundRect(new Rectangle(card.X + 24, card.Y + 112, 230, 32), 8))
            using (var badge = new SolidBrush(Color.FromArgb(42, Color.Black)))
            {
                g.FillPath(badge, path);
                g.DrawString(status, Pv.Label, b, card.X + 36, card.Y + 122);
            }

            if (Track.Length > 0)
            {
                using var b = new SolidBrush(Pv.Bone);
                var box = new RectangleF(card.X + 272, card.Y + 108, card.Width - 304, 42);
                g.DrawString("♫  " + Track, Pv.BodyBold, b, box);
            }
        }
    }

    private sealed class DjMemberRow : Control
    {
        private readonly DjParticipant _person;

        public DjMemberRow(DjParticipant person)
        {
            _person = person;
            Height = 54;
            BackColor = Pv.Charcoal;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var row = new Rectangle(0, 3, Width - 1, Height - 6);
            using (var b = new SolidBrush(Pv.Char2))
            using (var path = Pv.RoundRect(row, 8)) g.FillPath(b, path);

            var avatar = new Rectangle(10, 10, 34, 34);
            Glyphs.Avatar(g, avatar, _person.Nick, Pv.Orange, Pv.Bone);
            using (var b = new SolidBrush(Pv.Bone))
                g.DrawString(_person.Nick + (_person.IsMe ? " (voce)" : ""), Pv.BodyBold, b, 56, 12);
            using (var b = new SolidBrush(_person.IsHost ? Pv.NitroPink : Pv.Green))
                g.DrawString(_person.IsHost ? "TRANSMITINDO" : "OUVINDO", Pv.Label, b, 56, 31);
            Glyphs.Music(g, new RectangleF(Width - 34, 18, 18, 18),
                         _person.IsHost ? Pv.NitroPink : Pv.BoneDim, 1.8f);
        }
    }
}
