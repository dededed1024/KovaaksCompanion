using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace KovaaksCompanion.Core;

/// <summary>
/// Local data container: magic + HMAC-SHA256 + deflated payload. Not readable in a text editor, and any edit breaks the
/// signature so <see cref="TryRead"/> rejects the file. The key ships in the exe, so this deters hand edits, not a determined reverse engineer.
/// </summary>
public static class SignedFile
{
    static readonly byte[] Magic = "KCB1"u8.ToArray();
    const int MacLength = 32;
    static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("KovaaksCompanion.local-data.v1"));

    public static byte[] Pack(byte[] payload)
    {
        using var body = new MemoryStream();
        using (var z = new DeflateStream(body, CompressionLevel.Optimal, leaveOpen: true)) z.Write(payload);
        var packed = body.ToArray();
        var mac = HMACSHA256.HashData(Key, packed);
        return [.. Magic, .. mac, .. packed];
    }

    /// <summary>Null when the data is not a container, was edited, or is truncated.</summary>
    public static byte[]? Unpack(byte[] data)
    {
        if (data.Length < Magic.Length + MacLength || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var mac = data.AsSpan(Magic.Length, MacLength);
        var packed = data.AsSpan(Magic.Length + MacLength);
        if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(Key, packed))) return null;
        try
        {
            using var z = new DeflateStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
            using var o = new MemoryStream();
            z.CopyTo(o);
            return o.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Atomic write (temp file + move).</summary>
    public static void Write(string path, byte[] payload)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Pack(payload));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Null when the file is missing, unreadable or tampered.</summary>
    public static byte[]? TryRead(string path)
    {
        try { return File.Exists(path) ? Unpack(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
