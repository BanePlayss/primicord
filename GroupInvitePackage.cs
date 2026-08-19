using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Primicord.SetupShared;

/// <summary>
/// Convite privado anexado ao fim do instalador publico. O executavel continua
/// sendo um PE normal; somente quem conhece a senha consegue recuperar a auth key.
/// </summary>
public sealed record GroupInvite(string ServerUrl, string AuthKey);

public static class GroupInvitePackage
{
    private static readonly byte[] Magic = "PRIMICORD-GROUP!"u8.ToArray(); // 16 bytes
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int FooterSize = 4 + 16;
    private const int Iterations = 350_000;
    private const int MaxEnvelopeSize = 64 * 1024;

    public static bool HasInvite(string installerPath)
        => TryReadEnvelope(installerPath, out _);

    public static void CreateInstaller(string sourceInstaller, string destinationInstaller,
                                       GroupInvite invite, string password)
    {
        if (!File.Exists(sourceInstaller))
            throw new FileNotFoundException("O instalador base nao foi encontrado.", sourceInstaller);
        if (new FileInfo(sourceInstaller).Length < 1_000_000)
            throw new InvalidDataException("O instalador base parece incompleto.");
        if (HasInvite(sourceInstaller))
            throw new InvalidDataException("Use o instalador publico como base, nao outro convite privado.");

        Validate(invite, password);
        byte[] plain = Serialize(invite);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations,
                                               HashAlgorithmName.SHA256, 32);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Encrypt(nonce, plain, cipher, tag, Magic);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationInstaller))!);
            using var source = new FileStream(sourceInstaller, FileMode.Open, FileAccess.Read,
                                              FileShare.Read, 128 * 1024);
            using var output = new FileStream(destinationInstaller, FileMode.Create, FileAccess.Write,
                                              FileShare.None, 128 * 1024);
            source.CopyTo(output);
            output.Write(salt);
            output.Write(nonce);
            output.Write(tag);
            output.Write(cipher);

            int envelopeLength = SaltSize + NonceSize + TagSize + cipher.Length;
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, envelopeLength);
            output.Write(length);
            output.Write(Magic);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(cipher);
        }
    }

    public static GroupInvite? ReadInstaller(string installerPath, string password)
    {
        if (!TryReadEnvelope(installerPath, out byte[]? envelope)) return null;
        byte[] salt = envelope.AsSpan(0, SaltSize).ToArray();
        byte[] nonce = envelope.AsSpan(SaltSize, NonceSize).ToArray();
        byte[] tag = envelope.AsSpan(SaltSize + NonceSize, TagSize).ToArray();
        byte[] cipher = envelope.AsSpan(SaltSize + NonceSize + TagSize).ToArray();
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations,
                                               HashAlgorithmName.SHA256, 32);
        byte[] plain = new byte[cipher.Length];

        try
        {
            using (var aes = new AesGcm(key, TagSize))
                aes.Decrypt(nonce, cipher, tag, plain, Magic);
            return Deserialize(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private static bool TryReadEnvelope(string installerPath, out byte[]? envelope)
    {
        envelope = null;
        try
        {
            using var file = new FileStream(installerPath, FileMode.Open, FileAccess.Read,
                                            FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < FooterSize) return false;

            file.Position = file.Length - FooterSize;
            Span<byte> footer = stackalloc byte[FooterSize];
            file.ReadExactly(footer);
            if (!footer[4..].SequenceEqual(Magic)) return false;

            int length = BinaryPrimitives.ReadInt32LittleEndian(footer[..4]);
            if (length < SaltSize + NonceSize + TagSize + 5 || length > MaxEnvelopeSize
                || length > file.Length - FooterSize)
                throw new InvalidDataException("O convite privado esta corrompido.");

            envelope = new byte[length];
            file.Position = file.Length - FooterSize - length;
            file.ReadExactly(envelope);
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    private static byte[] Serialize(GroupInvite invite)
    {
        byte[] server = Encoding.UTF8.GetBytes(invite.ServerUrl.Trim());
        byte[] auth = Encoding.UTF8.GetBytes(invite.AuthKey.Trim());
        if (server.Length > ushort.MaxValue || auth.Length > ushort.MaxValue)
            throw new InvalidDataException("Os dados do convite sao grandes demais.");

        byte[] result = new byte[1 + 2 + server.Length + 2 + auth.Length];
        result[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1, 2), (ushort)server.Length);
        server.CopyTo(result, 3);
        int at = 3 + server.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(at, 2), (ushort)auth.Length);
        auth.CopyTo(result, at + 2);
        CryptographicOperations.ZeroMemory(auth);
        return result;
    }

    private static GroupInvite Deserialize(ReadOnlySpan<byte> plain)
    {
        if (plain.Length < 5 || plain[0] != 1)
            throw new InvalidDataException("Versao de convite nao reconhecida.");
        int serverLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.Slice(1, 2));
        int authLengthAt = 3 + serverLength;
        if (serverLength < 1 || authLengthAt + 2 > plain.Length)
            throw new InvalidDataException("Convite privado corrompido.");
        int authLength = BinaryPrimitives.ReadUInt16LittleEndian(plain.Slice(authLengthAt, 2));
        if (authLength < 1 || authLengthAt + 2 + authLength != plain.Length)
            throw new InvalidDataException("Convite privado corrompido.");

        return new GroupInvite(
            Encoding.UTF8.GetString(plain.Slice(3, serverLength)),
            Encoding.UTF8.GetString(plain.Slice(authLengthAt + 2, authLength)));
    }

    private static void Validate(GroupInvite invite, string password)
    {
        if (!Uri.TryCreate(invite.ServerUrl, UriKind.Absolute, out var server)
            || server.Scheme != Uri.UriSchemeHttp || string.IsNullOrWhiteSpace(server.Host))
            throw new ArgumentException("Informe um endereco HTTP valido do mini servidor.");
        if (!invite.AuthKey.Trim().StartsWith("tskey-auth-", StringComparison.Ordinal)
            || invite.AuthKey.Trim().Length < 30)
            throw new ArgumentException("Informe uma auth key do Tailscale (tskey-auth-...).");
        if (password.Length < 10)
            throw new ArgumentException("A senha do grupo precisa ter pelo menos 10 caracteres.");
    }
}
