using System.Reflection;

namespace Primicord;

/// <summary>Asset da marca fornecido pelo Primitivão. O arquivo é embutido no exe.</summary>
internal static class BrandAssets
{
    private static readonly Lazy<Bitmap?> Cached = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static Bitmap? Logo => Cached.Value;

    private static Bitmap? Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("Primicord.Brand");
            if (stream == null) return null;
            using var source = new Bitmap(stream);
            return new Bitmap(source);
        }
        catch (Exception ex)
        {
            Log.Write("logo da marca não carregou: " + ex.Message);
            return null;
        }
    }

    public static void Draw(Graphics g, Rectangle destination)
    {
        var logo = Logo;
        if (logo == null || destination.Width <= 0 || destination.Height <= 0) return;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(logo, destination);
    }
}
