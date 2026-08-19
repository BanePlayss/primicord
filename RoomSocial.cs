namespace Primicord;

/// <summary>
/// Campos legados mantidos no protocolo para clientes anteriores à grade 0.6.12.
/// A interface atual não posiciona participantes livremente.
/// </summary>
public readonly record struct SocialPosition(double X, double Y, int Scale)
{
    public const int MinScale = 48;
    public const int DefaultScale = 92;
    public const int MaxScale = 140;

    public SocialPosition Normalized() => new(
        Math.Clamp(double.IsFinite(X) ? X : 0.5, 0.0, 1.0),
        Math.Clamp(double.IsFinite(Y) ? Y : 0.5, 0.0, 1.0),
        Math.Clamp(Scale, MinScale, MaxScale));

    /// <summary>Distancia normalizada, pronta para recursos futuros de proximidade.</summary>
    public double DistanceTo(SocialPosition other)
    {
        double dx = X - other.X, dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Posicao inicial estavel e espalhada. O mesmo id sempre nasce no mesmo lugar,
    /// sem empilhar todos no centro enquanto ainda nao existe estado persistido.
    /// </summary>
    public static SocialPosition DefaultFor(uint id)
    {
        double angle = (id % 3600) / 3600.0 * Math.PI * 2;
        double radius = 0.18 + ((id >> 12) % 1000) / 1000.0 * 0.18;
        return new SocialPosition(
            0.5 + Math.Cos(angle) * radius,
            0.5 + Math.Sin(angle) * radius,
            DefaultScale).Normalized();
    }
}
