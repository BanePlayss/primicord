using System.Security.Cryptography;
using System.Text;

namespace Primicord;

/// <summary>
/// Posicao social de um jogador dentro da arena. X e Y sao normalizados para a
/// janela poder mudar de tamanho sem deslocar a pessoa; Scale e o diametro do avatar.
/// </summary>
public readonly record struct SocialPosition(double X, double Y, int Scale)
{
    public const int MinScale = 48;
    public const int DefaultScale = 72;
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

/// <summary>
/// Persistencia duravel da bolinha por sala e usuario. A presenca efemera continua
/// no peer da RoomSession; este documento sobrevive quando o peer sai da call.
/// </summary>
public sealed class RoomSocialService
{
    private readonly Firestore _fs;

    public RoomSocialService(Firestore fs) => _fs = fs;

    public async Task<SocialPosition?> LoadAsync(string roomId, string nick,
                                                  CancellationToken ct = default)
    {
        var fields = await _fs.GetAsync(DocumentPathFor(roomId, nick), ct).ConfigureAwait(false);
        if (fields == null) return null;

        return new SocialPosition(
            Firestore.Real(fields, "x", 0.5),
            Firestore.Real(fields, "y", 0.5),
            (int)Firestore.Num(fields, "scale", SocialPosition.DefaultScale)).Normalized();
    }

    public Task SaveAsync(string roomId, string nick, SocialPosition position,
                          CancellationToken ct = default)
    {
        position = position.Normalized();
        return _fs.SetAsync(DocumentPathFor(roomId, nick), new Dictionary<string, object?>
        {
            ["nick"] = nick,
            ["x"] = position.X,
            ["y"] = position.Y,
            ["scale"] = position.Scale,
            ["updatedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // Documento reservado dentro da colecao que as rules atuais ja aceitam.
            // Zero garante que RoomDirectory/RoomSession nunca contem isto como gente.
            ["lastSeen"] = 0L,
        }, mergeFields: true, ct);
    }

    public static string DocumentIdFor(string nick)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(nick.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static string DocumentPathFor(string roomId, string nick)
        => $"pc_rooms/{roomId}/peers/saved-social-{DocumentIdFor(nick)}";
}
