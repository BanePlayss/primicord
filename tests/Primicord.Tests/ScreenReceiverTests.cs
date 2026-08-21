using System.Drawing.Imaging;
using Primicord;
using Xunit;

namespace Primicord.Tests;

public sealed class ScreenReceiverTests
{
    [Fact]
    public async Task Pintura_bloqueia_atualizacao_do_mesmo_bitmap()
    {
        using var receiver = new ScreenReceiver();
        Assert.True(receiver.OnUpdate(7, TilePacket(Color.DarkRed), 64, 64));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = Task.Run(() => receiver.UseFrame(7, frame =>
        {
            _ = frame.Width;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var writer = Task.Run(() => receiver.OnUpdate(7, TilePacket(Color.DarkBlue), 64, 64));

        await Task.Delay(100);
        Assert.False(writer.IsCompleted);

        release.Set();
        Assert.True(await reader);
        Assert.True(await writer);
    }

    [Fact]
    public void StageView_desenha_o_quadro_por_meio_do_receptor()
    {
        using var receiver = new ScreenReceiver();
        Assert.True(receiver.OnUpdate(8, TilePacket(Color.DarkGreen), 64, 64));

        using var stage = new StageView { Size = new Size(640, 360) };
        stage.SetFrame(receiver, 8);
        using var output = new Bitmap(stage.Width, stage.Height);

        stage.DrawToBitmap(output, stage.ClientRectangle);

        Assert.NotEqual(Color.White.ToArgb(), output.GetPixel(320, 180).ToArgb());
    }

    private static byte[] TilePacket(Color color)
    {
        byte[] jpeg;
        using (var bitmap = new Bitmap(64, 64, PixelFormat.Format24bppRgb))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(color);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Jpeg);
            jpeg = stream.ToArray();
        }

        var packet = new byte[12 + jpeg.Length];
        packet[1] = 1;                 // um bloco
        packet[3] = 8;                 // bloco de 64px (8 * 8)
        packet[4] = 64;                // largura do quadro
        packet[6] = 64;                // altura do quadro
        packet[8] = 0;                 // coluna
        packet[9] = 0;                 // linha
        packet[10] = (byte)jpeg.Length;
        packet[11] = (byte)(jpeg.Length >> 8);
        jpeg.CopyTo(packet, 12);
        return packet;
    }
}
