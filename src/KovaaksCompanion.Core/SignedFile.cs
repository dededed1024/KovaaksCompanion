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
    /// <summary>Same container with the payload stored as is: for many small values (database rows), where deflate costs more time than it saves space.</summary>
    static readonly byte[] RawMagic = "KCB2"u8.ToArray();
    const int MacLength = 32;
    static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("KovaaksCompanion.local-data.v1"));

    static byte[] PackRaw(byte[] payload)
    {
        var mac = HMACSHA256.HashData(Key, payload);
        return [.. RawMagic, .. mac, .. payload];
    }

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

    /// <summary>Atomic write (temp file + move).</summary>
    public static void Write(string path, byte[] payload)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, Pack(payload));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>XOR keystream over the whole container (magic, MAC and body): hides the structure from casual inspection at near-zero cost. Self-inverse.</summary>
    static void Mask(byte[] data)
    {
        for (var i = 0; i < data.Length; i++) data[i] ^= (byte)(Key[i & 31] + (i >> 5));
    }

    /// <summary>A masked container as bytes, for a value stored inside another file (a database row). Read it back with <see cref="UnpackMasked"/>.</summary>
    public static byte[] PackMasked(byte[] payload)
    {
        var data = PackRaw(payload);
        Mask(data);
        return data;
    }

    /// <summary>Null when the bytes are not a masked container or were edited.</summary>
    public static byte[]? UnpackMasked(byte[] data)
    {
        var copy = (byte[])data.Clone();
        Mask(copy);
        return Unpack(copy);
    }

    /// <summary>Like <see cref="Write"/>, but the file is also masked so no part of it is recognizable. Read it back with <see cref="TryReadMasked"/>.</summary>
    public static void WriteMasked(string path, byte[] payload)
    {
        var data = Pack(payload);
        Mask(data);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
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
