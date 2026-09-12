using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Primicord;

// Synthetic pixels only: no desktop capture, network, or user configuration.
const int tileSize = 128;
using var receiver = new ScreenReceiver();
int cases = 0;
var sizes = args.Contains("--full")
    ? new[] { new Size(262,134), new Size(1280,720), new Size(1366,768), new Size(1920,1080),
        new Size(2560,1440), new Size(3440,1440), new Size(1080,1920), new Size(3840,2160) }
    : new[] { new Size(262,134) };
foreach (var size in sizes)
foreach (int dpi in new[] { 144, 96, 120, 192, 72, 300 })
foreach (var codec in new[] { ImageFormat.Png, ImageFormat.Jpeg })
{
    int columns = (size.Width+127)/128, rows=(size.Height+127)/128;
    using var packet = new MemoryStream();
    using var writer = new BinaryWriter(packet);
    writer.Write((byte)0); writer.Write((ushort)(columns*rows)); writer.Write((byte)(tileSize/8));
    writer.Write((ushort)size.Width); writer.Write((ushort)size.Height);
    var expected = new byte[size.Width*size.Height*4];
    for (int row=0;row<rows;row++)
    for (int col=0;col<columns;col++)
    {
        int width=Math.Min(tileSize,size.Width-col*tileSize), height=Math.Min(tileSize,size.Height-row*tileSize);
        using var tile=new Bitmap(width,height,PixelFormat.Format24bppRgb);
        tile.SetResolution(dpi,dpi);
        using (var g=Graphics.FromImage(tile))
        {
            g.Clear(Color.FromArgb(50+(col*17)%180,60+(row*23)%170,180));
            g.FillRectangle(Brushes.White,0,0,Math.Min(3,width),height);
            g.FillRectangle(Brushes.Yellow,0,0,width,Math.Min(3,height));
            g.FillRectangle(Brushes.Red,width-1,0,1,height);
            g.FillRectangle(Brushes.Lime,0,height-1,width,1);
        }
        using var compressed=new MemoryStream();
        tile.Save(compressed,codec);
        var bytes=compressed.ToArray();
        if (bytes.Length>ushort.MaxValue) throw new Exception("Fixture exceeds tile protocol.");
        writer.Write((byte)col); writer.Write((byte)row); writer.Write((ushort)bytes.Length); writer.Write(bytes);
        compressed.Position=0;
        using var decoded=new Bitmap(compressed);
        var pixels=Pixels(decoded);
        for (int y=0;y<height;y++)
            Buffer.BlockCopy(pixels,y*width*4,expected,((row*tileSize+y)*size.Width+col*tileSize)*4,width*4);
    }
    // Intentionally reuse sender id across resolution switches, as in a real call.
    if (!receiver.OnUpdate(7,packet.ToArray(),size.Width,size.Height)) throw new Exception("Update rejected.");
    using var actual=receiver.FrameOf(7) ?? throw new Exception("No frame.");
    if (actual.Size!=size) throw new Exception("Wrong canvas dimensions.");
    var actualPixels=Pixels(actual);
    int mismatch=-1;
    for (int i=0;i<expected.Length;i++) if (expected[i]!=actualPixels[i]) { mismatch=i; break; }
    if (mismatch>=0)
        throw new Exception($"PIXEL MISMATCH: {size.Width}x{size.Height}, source={dpi} DPI, {codec}, at ({mismatch/4%size.Width},{mismatch/4/size.Width}); expected={expected[mismatch]}, actual={actualPixels[mismatch]}");
    Console.WriteLine($"PASS: {size.Width}x{size.Height} · {dpi} DPI · {codec} · every decoded pixel preserved");
    cases++;
}
Console.WriteLine($"PASS: {cases} mixed-resolution/DPI/codec cases.");

static byte[] Pixels(Bitmap image)
{
    var data=image.LockBits(new Rectangle(Point.Empty,image.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
    try
    {
        var pixels=new byte[image.Width*image.Height*4];
        for (int y=0;y<image.Height;y++) Marshal.Copy(data.Scan0+y*data.Stride,pixels,y*image.Width*4,image.Width*4);
        return pixels;
    }
    finally { image.UnlockBits(data); }
}
