using System.Drawing.Imaging;
using Primicord;
using Xunit;

namespace Primicord.Tests;

public sealed class CallUxTests
{
    [Fact]
    public void Palco_preserva_proporcao_sem_crop_ou_zoom()
    {
        Rectangle fitted = StageView.FitFrame(
            new Size(1920, 1080), new Rectangle(0, 0, 800, 600));

        Assert.Equal(new Rectangle(0, 75, 800, 450), fitted);
        Assert.Equal(16d / 9d, fitted.Width / (double)fitted.Height, 3);
    }

    [Fact]
    public void Miniaturas_ficam_fora_do_palco_compartilhado()
    {
        using var call = new CallGrid { Size = new Size(1000, 650) };
        using var stage = new StageView();
        call.AttachStage(stage);
        call.SetOwn(new PeerTile { Nick = "bane" });
        call.SetStageVisible(true);
        call.CreateControl();
        call.PerformLayout();

        PeerTile tile = Assert.Single(call.Controls.OfType<PeerTile>());
        Assert.True(call.StageBounds.Bottom < tile.Top,
            $"palco {call.StageBounds} ainda invade miniatura {tile.Bounds}");
    }

    [Fact]
    public void Volume_do_peer_pode_ser_definido_antes_do_primeiro_audio()
    {
        using var voice = new VoiceEngine();
        voice.SetPeerVolume(123, 0.45f);

        Assert.Equal(0.45f, voice.GetPeerVolume(123), 3);
        Assert.Equal(1f, voice.GetPeerVolume(456), 3);
    }

    [Fact]
    public void Palco_desenha_a_previa_local_recebida()
    {
        using var source = new Bitmap(160, 90);
        using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.FromArgb(210, 25, 20));
        using var encoded = new MemoryStream();
        source.Save(encoded, ImageFormat.Jpeg);

        using var stage = new StageView { Size = new Size(320, 200), SelfPreview = true };
        stage.CreateControl();
        stage.SetSelfFrame(encoded.ToArray());
        using var rendered = new Bitmap(stage.Width, stage.Height);
        stage.DrawToBitmap(rendered, stage.ClientRectangle);

        Color center = rendered.GetPixel(rendered.Width / 2, rendered.Height / 2);
        Assert.True(center.R > 150 && center.G < 80 && center.B < 80,
                    $"centro inesperado: {center}");
    }
}
