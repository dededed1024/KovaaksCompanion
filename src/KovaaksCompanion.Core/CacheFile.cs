using System.Collections.Concurrent;
using System.Text.Json;

namespace KovaaksCompanion.Core;

/// <summary>
/// A cache file (*.cache): a JSON dictionary stamped with the schema number of its entries (each cache owns one and bumps it when the entry shape changes). Everything in it can be computed again,
/// so a missing, corrupt or other-schema file reads as empty and the caller fills it anew.
/// </summary>
/// <summary>Size and write time of a file, to tell whether it changed.</summary>
public readonly record struct FileStamp(long Size, long Ticks)
{
    /// <summary>Throws <see cref="IOException"/> when the file is missing.</summary>
    public static FileStamp Of(string path)
    {
        var f = new FileInfo(path);
        return new(f.Length, f.LastWriteTimeUtc.Ticks);
    }

    /// <summary><c>default</c> when the file is missing.</summary>
    public static FileStamp OfOptional(string path) => File.Exists(path) ? Of(path) : default;
}

public static class CacheFile
{
    public const string Extension = ".cache";

    static readonly object Gate = new();

    /// <summary>What each file last held, so a read of an unchanged file skips the read, check and parse. Never mutated once stored.</summary>
    static readonly ConcurrentDictionary<string, (FileStamp Stamp, int Schema, object Items)> Memo = new(StringComparer.OrdinalIgnoreCase);

    record Wrapper<V>(int Schema, Dictionary<string, V> Items);

    /// <summary>Case-insensitive keys; empty when the file is missing, damaged or written with another schema.</summary>
    public static Dictionary<string, V> Read<V>(string path, int schema)
    {
        try
        {
            var stamp = FileStamp.OfOptional(path);
            if (stamp == default) return new(StringComparer.OrdinalIgnoreCase);
            if (Memo.TryGetValue(path, out var m) && m.Stamp == stamp && m.Schema == schema && m.Items is Dictionary<string, V> kept)
                return new(kept, StringComparer.OrdinalIgnoreCase);
            if (JsonSerializer.Deserialize<Wrapper<V>>(File.ReadAllBytes(path)) is { } w && w.Schema == schema && w.Items != null)
            {
                var items = new Dictionary<string, V>(w.Items, StringComparer.OrdinalIgnoreCase);
                Memo[path] = (stamp, schema, items);
                return new(items, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or IOException or UnauthorizedAccessException) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("cache read", e); }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Atomic write; a cache that cannot be written is skipped (the next run computes it again).</summary>
    public static void Write<V>(string path, int schema, Dictionary<string, V> items)
    {
        lock (Gate)
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.Write(path,JsonSerializer.SerializeToUtf8Bytes(new Wrapper<V>(schema, items)));
                Memo[path] = (FileStamp.Of(path), schema, new Dictionary<string, V>(items, StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.AppLog.Write("cache", "write failed: " + e.Message); }
    }
}
