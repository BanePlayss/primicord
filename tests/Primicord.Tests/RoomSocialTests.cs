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
    public void Posicao_inicial_e_id_social_sao_estaveis()
    {
        Assert.Equal(SocialPosition.DefaultFor(12345), SocialPosition.DefaultFor(12345));
        Assert.Equal(RoomSocialService.DocumentIdFor(" Bane "),
                     RoomSocialService.DocumentIdFor("bane"));
        Assert.NotEqual(RoomSocialService.DocumentIdFor("bane"),
                        RoomSocialService.DocumentIdFor("ricle"));
    }

    [Fact]
    public void Arena_social_renderiza_avatares_em_posicoes_livres()
    {
        using var arena = new SocialArena { Size = new Size(760, 460), RoomName = "SALA 03" };
        arena.SetOwn(new PeerTile { Nick = "bane", Connected = true },
                     new SocialPosition(0.22, 0.28, 112));
        arena.SetParticipant(10, new PeerTile { Nick = "ricle", Connected = true },
                             new SocialPosition(0.76, 0.30, 86));
        arena.SetParticipant(11, new PeerTile { Nick = "mohamed", Connected = true },
                             new SocialPosition(0.52, 0.68, 138));

        using var bitmap = new Bitmap(arena.Width, arena.Height);
        arena.DrawToBitmap(bitmap, arena.ClientRectangle);

        Assert.NotEqual(bitmap.GetPixel(20, 20), bitmap.GetPixel(arena.Width / 2, arena.Height / 2));
        bitmap.Save(Path.Combine(Path.GetTempPath(), "primicord-social-arena-preview.png"));
    }
}
