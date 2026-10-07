using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Unpacks the ffmpeg.exe embedded in the app to &lt;root&gt;\&lt;key&gt;\ffmpeg.exe, verifies it and checks it actually runs.</summary>
public static partial class FfmpegBundle
{
    public static string Root => Path.Combine(AppSettings.DefaultDataFolder, "ffmpeg");
    public static string FallbackRoot => Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "ffmpeg");

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

    public static bool VerifySha256(string file, string expectedHex)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f)).Equals(expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts (if needed) into the first root that works and whose exe runs; null when nothing is embedded or every root fails.</summary>
    public static string? Ensure(Func<Stream?> open, IReadOnlyList<string>? roots = null, Func<string, bool>? runs = null)
    {
        roots ??= [Root, FallbackRoot];
        runs ??= RunsOk;
        using var src = open();
        if (src == null) return null;
        var hash = Sha256(src);
        foreach (var root in roots)
        {
            string exe;
            try { exe = Extract(src, hash, root); }
            catch (Exception e) { AppLog.Write("ffmpeg", $"extract to {root} failed: {e}"); continue; }
            if (runs(exe)) { Cleanup(root, Key(hash)); return exe; }
            AppLog.Write("ffmpeg", "embedded ffmpeg does not run: " + exe);
        }
        return null;
    }

    static string Extract(Stream src, string hash, string root)
    {
        var dir = Path.Combine(root, Key(hash));
        var exe = Path.Combine(dir, "ffmpeg.exe");
        if (Valid(exe, src.Length, hash)) return exe;
        Directory.CreateDirectory(dir);
        var tmp = exe + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            src.Position = 0;
            using (var o = File.Create(tmp)) src.CopyTo(o);
            if (!VerifySha256(tmp, hash)) throw new IOException("written ffmpeg.exe failed hash check");
            try { File.Move(tmp, exe, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Valid(exe, src.Length, hash)) return exe; // another instance won the race
                throw;
            }
        }
        finally { try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        return exe;
    }

    static bool Valid(string exe, long length, string hash)
    {
        try { return File.Exists(exe) && new FileInfo(exe).Length == length && VerifySha256(exe, hash); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

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
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("ffmpeg", $"stale {old} not removed: {e.Message}"); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("ffmpeg", $"cleanup {root} failed: {e.Message}"); }
    }

    /// <summary>Runs `ffmpeg -hide_banner -version` with a 5 s timeout.</summary>
    public static bool RunsOk(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "-hide_banner -version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                AppLog.Write("ffmpeg", "version check timed out: " + exe);
                return false;
            }
            if (p.ExitCode != 0) { AppLog.Write("ffmpeg", $"version check exit {p.ExitCode}: {exe}"); return false; }
            return true;
        }
        catch (Exception e) { AppLog.Write("ffmpeg", $"version check failed for {exe}: {e}"); return false; }
    }
}
