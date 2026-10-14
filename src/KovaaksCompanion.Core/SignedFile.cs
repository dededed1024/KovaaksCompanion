using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace KovaaksCompanion.Core;

/// <summary>
/// Reads the signed containers (magic + HMAC-SHA256 + payload, optionally masked) that earlier versions wrote, so the migration of their files and database rows keeps working.
/// Nothing is signed any more: new data is written as plain JSON.
/// </summary>
public static class SignedFile
{
    static readonly byte[] Magic = "KCB1"u8.ToArray();
    static readonly byte[] RawMagic = "KCB2"u8.ToArray();
    const int MacLength = 32;
    static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("KovaaksCompanion.local-data.v1"));

    /// <summary>Null when the data is not a container, was edited, or is truncated.</summary>
    public static byte[]? Unpack(byte[] data)
    {
        if (data.Length < Magic.Length + MacLength) return null;
        var raw = data.AsSpan(0, RawMagic.Length).SequenceEqual(RawMagic);
        if (!raw && !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var mac = data.AsSpan(Magic.Length, MacLength);
        var packed = data.AsSpan(Magic.Length + MacLength);
        if (!CryptographicOperations.FixedTimeEquals(mac, HMACSHA256.HashData(Key, packed))) return null;
        if (raw) return packed.ToArray();
        try
        {
            using var z = new DeflateStream(new MemoryStream(packed.ToArray()), CompressionMode.Decompress);
            using var o = new MemoryStream();
            z.CopyTo(o);
            return o.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>XOR keystream over the whole container (magic, MAC and body). Self-inverse.</summary>
    static void Mask(byte[] data)
    {
        for (var i = 0; i < data.Length; i++) data[i] ^= (byte)(Key[i & 31] + (i >> 5));
    }

    /// <summary>Null when the bytes are not a masked container or were edited.</summary>
    public static byte[]? UnpackMasked(byte[] data)
    {
        var copy = (byte[])data.Clone();
        Mask(copy);
        return Unpack(copy);
    }

    /// <summary>Null when the file is missing, unreadable, unmasked (older format) or tampered.</summary>
    public static byte[]? TryReadMasked(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var data = File.ReadAllBytes(path);
            Mask(data);
            return Unpack(data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Null when the file is missing, unreadable or tampered.</summary>
    public static byte[]? TryRead(string path)
    {
        try { return File.Exists(path) ? Unpack(File.ReadAllBytes(path)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
