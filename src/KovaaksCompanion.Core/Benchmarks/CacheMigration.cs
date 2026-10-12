using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>
/// One-time move of the server-data cache to its new home (%LOCALAPPDATA%\KovaaksCompanion\cache, masked format).
/// The new cache starts empty and refills from the server; only the scores of deleted runs (not on the server) are carried over.
/// Old-format files are removed once the new cache holds fetched data. Replays, records and settings are never touched.
/// </summary>
public static class CacheMigration
{
    const string Version = "2";
    static readonly string[] CacheFiles = ["progress.dat", "scores.dat", "usernames.dat", "deleted.dat"];

    static string Marker => Path.Combine(AppSettings.DefaultDataFolder, "cache-version");

    /// <summary>Runs once per install: keeps the deleted-run history, then records the cache version. Safe to call on every start.</summary>
    public static void Run(AppSettings s)
    {
        try
        {
            if (File.Exists(Marker) && File.ReadAllText(Marker).Trim() == Version) return;
            Directory.CreateDirectory(s.CacheFolder);
            var old = Path.Combine(s.LegacyCacheFolder, "deleted.dat");
            if (SignedFile.TryReadMasked(Path.Combine(s.CacheFolder, "deleted.dat")) == null && SignedFile.TryRead(old) is { } deleted)
                SignedFile.WriteMasked(Path.Combine(s.CacheFolder, "deleted.dat"), deleted);
            File.WriteAllText(Marker, Version);
            AppLog.Write("cache", "new cache started");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("cache", "migration failed: " + e.Message); }
    }

    /// <summary>
    /// Deletes old-format cache files (signed but not masked) from the old locations, but only once the new cache holds fetched progress.
    /// A file that already is in the new format is never deleted, even when old and new folder are the same.
    /// </summary>
    public static void CleanLegacy(AppSettings s)
    {
        try
        {
            if (SignedFile.TryReadMasked(Path.Combine(s.CacheFolder, "progress.dat")) == null) return;
            foreach (var f in CacheFiles.Select(n => Path.Combine(s.LegacyCacheFolder, n)).Append(s.LegacyPlaylistIndexFile))
            {
                if (!File.Exists(f) || SignedFile.TryReadMasked(f) != null || SignedFile.TryRead(f) == null) continue;
                File.Delete(f);
            }
            if (Directory.Exists(s.LegacyCacheFolder) && !Directory.EnumerateFileSystemEntries(s.LegacyCacheFolder).Any()) Directory.Delete(s.LegacyCacheFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("cache", "legacy cleanup failed: " + e.Message); }
    }
}
