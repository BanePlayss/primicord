using Primicord;
using Xunit;
using System.Drawing;

namespace Primicord.Tests;

public sealed class RoomUxTests
{
    [Fact]
    public void Notificacao_pode_ser_fechada_pelo_usuario()
    {
        using var banner = new AppBanner();
        banner.ShowMessage("Convite da sala copiado.", autoDismissMs: 0);

        Assert.True(banner.Visible);
        Assert.Equal(42, banner.Height);
        Assert.Equal("Convite da sala copiado.", banner.Message);

        var close = Assert.Single(banner.Controls.OfType<Button>());
        Assert.Equal("Fechar notificacao", close.AccessibleName);
        close.PerformClick();

        Assert.False(banner.Visible);
        Assert.Equal(0, banner.Height);
    }

    [Fact]
    public void Sala_renderiza_grade_de_chamada_e_tres_colunas()
    {
        using var root = new Panel { Size = new Size(1100, 700), BackColor = Color.Black };
        using var left = new Panel { Dock = DockStyle.Left, Width = 236, BackColor = Pv.Char2 };
        using var right = new Panel { Dock = DockStyle.Right, Width = 288, BackColor = Pv.Char2 };
        using var center = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

        var brand = new Label
        {
            Dock = DockStyle.Top, Height = 58, Text = "PRIMICORD",
            Font = Pv.DisplaySm, ForeColor = Pv.Orange, Padding = new Padding(16, 18, 0, 0),
        };
        var roomList = new Panel { Dock = DockStyle.Fill, BackColor = Pv.Char2 };
        var sala3 = new RailItem("SALA 03", RailItem.Kind.Voice)
        {
            Dock = DockStyle.Top, Height = 48, Active = true,
            Detail = "6/8", Badge = "AO VIVO",
        };
        var sala1 = new RailItem("SALA 01", RailItem.Kind.Voice)
        {
            Dock = DockStyle.Top, Height = 48, Detail = "4/8", Badge = "ESPERANDO",
        };
        roomList.Controls.Add(sala1);
        roomList.Controls.Add(sala3);

        // A sala usa o mesmo shell do restante do app: conexao e identidade
        // ficam persistentes no rodape, sem trocar por ranking.
        var voiceStrip = new Panel { Dock = DockStyle.Bottom, Height = 106, BackColor = Pv.Char2 };
        var voiceTitle = new Label
        {
            Text = "VOZ CONECTADA\nSALA 03", ForeColor = Pv.Green, Font = Pv.Label,
            Location = new Point(14, 10), AutoSize = true,
        };
        voiceStrip.Controls.Add(voiceTitle);
        foreach (var button in new[]
        {
            new GlyphButton((g, r, c, w) => Glyphs.Mic(g, r, c, false)),
            new GlyphButton(Glyphs.Speaker), new GlyphButton(Glyphs.Gear),
            new GlyphButton(Glyphs.Exit) { Accent = Pv.Red },
        })
        {
            button.Size = new Size(36, 36);
            button.Location = new Point(14 + voiceStrip.Controls.OfType<GlyphButton>().Count() * 44, 58);
            voiceStrip.Controls.Add(button);
        }
        var userPanel = new Label
        {
            Dock = DockStyle.Bottom, Height = 62, BackColor = Pv.Char3,
            ForeColor = Pv.Bone, Text = "  bane\n  331K PC", Padding = new Padding(12, 11, 0, 0),
        };
        left.Controls.Add(roomList);
        left.Controls.Add(voiceStrip);
        left.Controls.Add(userPanel);
        left.Controls.Add(brand);

        var chat = new ChatView(compact: true) { Dock = DockStyle.Fill };
        chat.SetHeader("CHAT DA SALA", "SALA 03");
        long now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        chat.SetMessages(new List<ChatMessage>
        {
            new() { Id = "1", Nick = "mohamed", Text = "salve rapaziada! 👋", At = now - 180000 },
            new() { Id = "2", Nick = "bane", Text = "bora pra cima 💪", At = now - 120000 },
            new() { Id = "3", Nick = "ricle", Text = "3x0 hoje", At = now - 60000 },
            new() { Id = "4", Nick = "celin", Text = "gg demais 🔥", At = now },
        }, "bane");
        var activity = new RoomActivityView();
        var baneJoined = new DateTimeOffset(2026, 8, 19, 21, 40, 0, TimeSpan.FromHours(-3));
        var mohamedJoined = baneJoined.AddMinutes(5);
        activity.Reset("bane", baneJoined);
        activity.UpdateSnapshot(6, "Voz WebRTC · Opus");
        Assert.Equal(6, activity.Participants);
        activity.RecordJoin("mohamed", mohamedJoined);
        // Um snapshot atrasado nao pode apagar quem entrou depois.
        activity.RecordJoin("ricle", baneJoined.AddMinutes(2));
        Assert.Equal("mohamed", activity.LastJoinedUser);
        Assert.Equal(mohamedJoined, activity.LastJoinedAt);
        right.Controls.Add(chat);
        right.Controls.Add(activity);

        var header = new RoomHeader { Dock = DockStyle.Top, RoomName = "SALA 03", Participants = 6 };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 76, BackColor = Pv.Char2,
            WrapContents = false, Padding = new Padding(8, 7, 0, 0),
        };
        foreach (var button in new[]
        {
            new ActionIcon((g, r, c, w) => Glyphs.Mic(g, r, c, false), "MICROFONE")
                { Wide = true, Active = true, Subtitle = "Ativo" },
            new ActionIcon(Glyphs.Speaker, "AUDIO")
                { Wide = true, Active = true, Subtitle = "Alto" },
            new ActionIcon(Glyphs.Plus, "CONVIDAR")
                { Wide = true, Subtitle = "amigos" },
            new ActionIcon(Glyphs.Exit, "SAIR DA SALA")
                { Wide = true, Subtitle = "voltar ao lobby" },
        })
        {
            button.Size = new Size(158, 62);
            button.Margin = Padding.Empty;
            actions.Controls.Add(button);
        }

        var call = new CallGrid { Dock = DockStyle.Fill };
        call.SetOwn(new PeerTile { Nick = "bane", Connected = true, Level = .2f });
        call.SetParticipant(1, new PeerTile { Nick = "ricle", Connected = true, Level = .3f });
        call.SetParticipant(2, new PeerTile { Nick = "mohamed", Connected = true, Level = .5f });
        call.SetParticipant(3, new PeerTile { Nick = "celin", Connected = true, Muted = true });
        call.SetParticipant(4, new PeerTile { Nick = "pitera", Connected = true, Level = .15f });
        call.SetParticipant(5, new PeerTile { Nick = "vitinho", Connected = true, Muted = true });

        center.Controls.Add(call);
        center.Controls.Add(actions);
        center.Controls.Add(header);
        root.Controls.Add(center);
        root.Controls.Add(right);
        root.Controls.Add(left);

        root.CreateControl();
        root.PerformLayout();
        using var bitmap = new Bitmap(root.Width, root.Height);
        root.DrawToBitmap(bitmap, root.ClientRectangle);

        Assert.True(call.Width > left.Width * 2);
        Assert.True(call.Height > 520);
        Assert.Equal(6, call.ParticipantCount);
        Assert.Empty(left.Controls.OfType<ClassificacaoView>());
        Assert.Equal(4, voiceStrip.Controls.OfType<GlyphButton>().Count());
        Assert.True(brand.Visible);
        Assert.True(userPanel.Visible);
        Assert.All(call.Controls.OfType<PeerTile>(), tile => Assert.True(tile.Width > tile.Height));
        bitmap.Save(Path.Combine(Path.GetTempPath(), "primicord-room-grid-preview.png"));
    }
}
