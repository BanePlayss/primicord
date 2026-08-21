using Primicord;
using Xunit;
using System.Drawing;

namespace Primicord.Tests;

public sealed class RoomSocialTests
{
    [Fact]
    public void Posicao_normaliza_coordenadas_tamanho_e_valores_invalidos()
    {
        var p = new SocialPosition(double.NaN, 2.5, 999).Normalized();
        Assert.Equal(0.5, p.X);
        Assert.Equal(1.0, p.Y);
        Assert.Equal(SocialPosition.MaxScale, p.Scale);
    }

    [Fact]
    public void Distancia_e_euclidiana_e_independe_do_tamanho_da_janela()
    {
        var a = new SocialPosition(0.1, 0.2, 48);
        var b = new SocialPosition(0.4, 0.6, 140);
        Assert.Equal(0.5, a.DistanceTo(b), 6);
    }

    [Fact]
    public void Posicao_legada_inicial_e_estavel_para_clientes_antigos()
    {
        Assert.Equal(SocialPosition.DefaultFor(12345), SocialPosition.DefaultFor(12345));
    }

    [Fact]
    public void Compartilhamento_mantem_cards_em_miniaturas_alinhadas()
    {
        using var root = new Panel { Size = new Size(760, 460) };
        using var call = new CallGrid { Dock = DockStyle.Fill };
        using var stage = new StageView();
        root.Controls.Add(call);
        call.AttachStage(stage);
        call.SetOwn(new PeerTile { Nick = "bane", Connected = true });
        call.SetParticipant(10, new PeerTile { Nick = "ricle", Connected = true });
        call.SetParticipant(11, new PeerTile { Nick = "mohamed", Connected = true });
        call.SetStageVisible(true);
        root.CreateControl();
        root.PerformLayout();
        foreach (Control child in call.Controls) child.CreateControl();

        using var bitmap = new Bitmap(root.Width, root.Height);
        root.DrawToBitmap(bitmap, root.ClientRectangle);

        Assert.True(stage.Visible);
        Assert.Equal(3, call.ParticipantCount);
        Assert.All(call.Controls.OfType<PeerTile>(), tile =>
        {
            Assert.True(tile.Bottom <= call.Height);
            Assert.True(tile.Top > call.Height / 2);
        });
        bitmap.Save(Path.Combine(Path.GetTempPath(), "primicord-call-stage-preview.png"));
    }
}
