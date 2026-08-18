using Primicord;
using Xunit;
using System.Drawing;

namespace Primicord.Tests;

public sealed class RoomUxTests
{
    [Fact]
    public void Sala_067_renderiza_com_arena_dominante_e_tres_colunas()
    {
        using var root = new Panel { Size = new Size(1100, 700), BackColor = Color.Black };
        using var left = new Panel { Dock = DockStyle.Left, Width = 188, BackColor = Pv.Char2 };
        using var right = new Panel { Dock = DockStyle.Right, Width = 258, BackColor = Pv.Char2 };
        using var center = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

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
        left.Controls.Add(roomList);

        var rankingPanel = new Panel { Dock = DockStyle.Bottom, Height = 194, BackColor = Pv.Char2 };
        var ranking = new ClassificacaoView { Dock = DockStyle.Fill, MeuNick = "bane", MaxLinhas = 5 };
        var tabela = new Classificacao { RodadaAtual = 3, TotalRodadas = 7, Situacao = "em andamento" };
        tabela.Times.AddRange(new[]
        {
            new TimeNaTabela { Nick = "jucamelero", J = 5, V = 4, Gp = 12, Gc = 3, P = 12 },
            new TimeNaTabela { Nick = "utirrabianc", J = 5, V = 3, E = 1, Gp = 9, Gc = 5, P = 10 },
            new TimeNaTabela { Nick = "spider", J = 5, V = 3, Gp = 8, Gc = 6, P = 9 },
            new TimeNaTabela { Nick = "bane", J = 5, V = 2, E = 1, Gp = 7, Gc = 7, P = 7 },
            new TimeNaTabela { Nick = "celin", J = 5, V = 1, E = 1, Gp = 4, Gc = 9, P = 4 },
        });
        ranking.Definir(tabela);
        rankingPanel.Controls.Add(ranking);
        left.Controls.Add(rankingPanel);

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
        activity.UpdateSnapshot(6, "Voz WebRTC · Opus");
        activity.Push("mohamed entrou na sala", "agora");
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

        var arena = new SocialArena { Dock = DockStyle.Fill, RoomName = "SALA 03" };
        arena.SetOwn(new PeerTile { Nick = "bane", Connected = true, Level = .2f },
                     new SocialPosition(.20, .24, 112));
        arena.SetParticipant(1, new PeerTile { Nick = "ricle", Connected = true, Level = .3f },
                             new SocialPosition(.76, .23, 88));
        arena.SetParticipant(2, new PeerTile { Nick = "mohamed", Connected = true, Level = .5f },
                             new SocialPosition(.48, .48, 140));
        arena.SetParticipant(3, new PeerTile { Nick = "celin", Connected = true, Muted = true },
                             new SocialPosition(.22, .72, 94));
        arena.SetParticipant(4, new PeerTile { Nick = "pitera", Connected = true, Level = .15f },
                             new SocialPosition(.78, .65, 104));
        arena.SetParticipant(5, new PeerTile { Nick = "vitinho", Connected = true, Muted = true },
                             new SocialPosition(.57, .80, 78));

        center.Controls.Add(arena);
        center.Controls.Add(actions);
        center.Controls.Add(header);
        root.Controls.Add(center);
        root.Controls.Add(right);
        root.Controls.Add(left);

        root.CreateControl();
        root.PerformLayout();
        using var bitmap = new Bitmap(root.Width, root.Height);
        root.DrawToBitmap(bitmap, root.ClientRectangle);

        Assert.True(arena.Width > left.Width * 3);
        Assert.True(arena.Height > 520);
        bitmap.Save(Path.Combine(Path.GetTempPath(), "primicord-room-067-preview.png"));
    }
}
