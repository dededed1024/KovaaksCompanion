using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>Unpacks the libobs runtime zip embedded in the app to &lt;root&gt;\&lt;key&gt;\ (bin\64bit, obs-plugins\64bit, data) and checks it is complete.</summary>
public static partial class ObsBundle
{
    public static string Root => Path.Combine(AppSettings.DefaultDataFolder, "libobs");
    public static string FallbackRoot => Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "libobs");

    const string DoneMarker = ".complete";

    [GeneratedRegex("^[0-9a-f]{12}$")]
    private static partial Regex KeyName();

    /// <summary>Folder name for the content: first 12 hex chars of its SHA-256.</summary>
    public static string Key(string sha256Hex) => sha256Hex[..12].ToLowerInvariant();

    public static string Sha256(Stream s)
    {
        var h = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        if (s.CanSeek) s.Position = 0;
        return h;
    }

    /// <summary>Extracts (if needed) into the first root that works; returns the folder holding bin\ and data\, or null when nothing is embedded or every root fails.</summary>
    public static string? Ensure(Func<Stream?> open, IReadOnlyList<string>? roots = null)
    {
        roots ??= [Root, FallbackRoot];
        using var src = open();
        if (src == null) return null;
        var hash = Sha256(src);
        foreach (var root in roots)
        {
            try
            {
                var dir = Extract(src, hash, root);
                Cleanup(root, Key(hash));
                return dir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ErrorReporter.Report($"obs extract to {root}", e);
            }
        }
        return null;
    }

    static string Extract(Stream src, string hash, string root)
    {
        var dir = Path.Combine(root, Key(hash));
        if (IsComplete(dir)) return dir;
        Directory.CreateDirectory(root);
        var tmp = dir + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            src.Position = 0;
            using (var zip = new ZipArchive(src, ZipArchiveMode.Read, leaveOpen: true))
            {
                var full = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
                foreach (var e in zip.Entries)
                {
                    if (e.FullName.EndsWith('/')) continue;
                    var target = Path.GetFullPath(Path.Combine(tmp, e.FullName));
                    if (!target.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bad entry " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    e.ExtractToFile(target, true);
                }
            }
            File.WriteAllText(Path.Combine(tmp, DoneMarker), hash);
            if (!File.Exists(Path.Combine(tmp, "bin", "64bit", "obs.dll"))) throw new InvalidDataException("bundle has no obs.dll");
            try { Directory.Move(tmp, dir); }
            catch (IOException e) when (IsComplete(dir)) { ErrorReporter.Report("obs bundle move (another instance won the race)", e); }
        }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorReporter.Report("obs bundle tmp delete", e); } }
        return dir;
    }

    static bool IsComplete(string dir)
        => File.Exists(Path.Combine(dir, DoneMarker)) && File.Exists(Path.Combine(dir, "bin", "64bit", "obs.dll"));

    /// <summary>Deletes stale key folders only (12 lowercase hex chars); folders in use or foreign names are left alone.</summary>
    static void Cleanup(string root, string keep)
    {
        try
        {
            foreach (var old in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(old);
                if (name == keep || !KeyName().IsMatch(name)) continue;
                try { Directory.Delete(old, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorReporter.Report($"obs stale {old} not removed", e); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ErrorReporter.Report($"obs cleanup {root}", e); }
    }
}
