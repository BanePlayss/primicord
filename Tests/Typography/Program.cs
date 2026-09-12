using Primicord;
using System.Drawing.Text;

// Rasterization only: this does not claim per-monitor WinForms layout coverage.
foreach (int dpi in new[] { 96, 120, 144, 192 })
foreach (var font in new[] { Pv.Label, Pv.Button, Pv.Body, Pv.DisplaySm, Pv.Display })
{
    using var bitmap = new Bitmap(1200,160);
    bitmap.SetResolution(dpi,dpi);
    using (var graphics = Graphics.FromImage(bitmap))
    {
        graphics.Clear(Color.Black);
        // Deliberately start with jagged rendering, as a guard against a missing setup.
        graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixel;
        Pv.PrepareText(graphics);
        if (graphics.TextRenderingHint != TextRenderingHint.AntiAliasGridFit)
            throw new Exception("The shared renderer must explicitly enable grayscale antialiasing.");
        graphics.DrawString("Transmissão · ação · você",font,Brushes.White,8,8);
    }
    int edgePixels = 0, inkPixels = 0;
    for (int y=0;y<bitmap.Height;y++)
    for (int x=0;x<bitmap.Width;x++)
    {
        var pixel=bitmap.GetPixel(x,y);
        if (pixel.R != pixel.G || pixel.G != pixel.B) throw new Exception("Unexpected colored text fringe.");
        if (pixel.R > 0) inkPixels++;
        if (pixel.R > 0 && pixel.R < 255) edgePixels++;
    }
    if (inkPixels == 0 || edgePixels < 20) throw new Exception("Text is blank or antialiasing is missing.");
    Console.WriteLine($"PASS: {dpi} DPI · {font.Name} {font.SizeInPoints} pt · {edgePixels} smooth edge pixels");
}
