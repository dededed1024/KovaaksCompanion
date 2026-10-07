using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace KovaaksCompanion.Core;

/// <summary>Finds the KovaaK's folder and ffmpeg.exe without user input. Environment access goes through seams for tests.</summary>
public static partial class PathDetector
{
    [GeneratedRegex("\"path\"\\s+\"(?<p>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathValue();

    /// <summary>Library folders listed in steamapps/libraryfolders.vdf (escaped backslashes undone).</summary>
    public static List<string> ParseLibraryFolders(string vdf) =>
        PathValue().Matches(vdf).Select(m => m.Groups["p"].Value.Replace(@"\\", @"\")).Where(p => p.Length > 0).ToList();

    public const string GameDir = @"steamapps\common\FPSAimTrainer";

    static bool IsKovaaks(string dir, Func<string, bool> dirExists, Func<string, bool> fileExists) =>
        dirExists(Path.Combine(dir, "FPSAimTrainer", "stats")) || fileExists(Path.Combine(dir, "FPSAimTrainer.exe"));

    static string? ReadRegistry(string hive, string key, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var k = (hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(key);
            return k?.GetValue(value) as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public static string? FindKovaaks() => FindKovaaks(ReadRegistry, Directory.Exists, File.Exists, p => File.Exists(p) ? File.ReadAllText(p) : null,
        DriveInfo.GetDrives().Select(d => d.Name.TrimEnd('\\')).ToArray());

    /// <param name="registry">(hive "HKCU"/"HKLM", key, value) to string.</param>
    public static string? FindKovaaks(Func<string, string, string, string?> registry, Func<string, bool> dirExists, Func<string, bool> fileExists,
        Func<string, string?> readText, IEnumerable<string> drives)
    {
        var libs = new List<string>();
        var steam = registry("HKCU", @"Software\Valve\Steam", "SteamPath") ?? registry("HKLM", @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(steam))
        {
            steam = steam.Replace('/', '\\');
            libs.Add(steam);
            if (readText(Path.Combine(steam, "steamapps", "libraryfolders.vdf")) is { } vdf) libs.AddRange(ParseLibraryFolders(vdf));
        }
        foreach (var d in drives)
        {
            libs.Add(d + @"\SteamLibrary");
            libs.Add(d + @"\Steam");
            libs.Add(d + @"\Program Files (x86)\Steam");
        }
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(lib, GameDir);
            if (IsKovaaks(dir, dirExists, fileExists)) return dir;
        }
        return null;
    }

    public static string? FindFfmpeg() => FindFfmpeg(File.Exists, Environment.GetEnvironmentVariable, FindWinGetPackages);

    /// <summary>Ordered candidates: app-managed copy, next to the app, PATH, WinGet, scoop, chocolatey, C:\ffmpeg.</summary>
    public static IEnumerable<string> FfmpegCandidates(Func<string, string?> env, Func<string, IEnumerable<string>> wingetPackages)
    {
        var local = env("LOCALAPPDATA") ?? "";
        yield return Path.Combine(local, "KovaaksCompanion", "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (Environment.ProcessPath is { } exe && Path.GetDirectoryName(exe) is { } d) yield return Path.Combine(d, "ffmpeg.exe");
        foreach (var p in (env("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.Combine(p.Trim('"'), "ffmpeg.exe");
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
        foreach (var w in wingetPackages(Path.Combine(local, "Microsoft", "WinGet", "Packages"))) yield return w;
        yield return Path.Combine(env("USERPROFILE") ?? "", "scoop", "shims", "ffmpeg.exe");
        yield return @"C:\ProgramData\chocolatey\bin\ffmpeg.exe";
        yield return @"C:\ffmpeg\bin\ffmpeg.exe";
    }

    public static string? FindFfmpeg(Func<string, bool> fileExists, Func<string, string?> env, Func<string, IEnumerable<string>> wingetPackages)
    {
        foreach (var c in FfmpegCandidates(env, wingetPackages))
        {
            try { if (c.Length > 0 && Path.IsPathRooted(c) && fileExists(c)) return c; }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Packages\*ffmpeg*\bin\ffmpeg.exe and Packages\*ffmpeg*\*\bin\ffmpeg.exe (depth capped).</summary>
    static IEnumerable<string> FindWinGetPackages(string packages)
    {
        if (!Directory.Exists(packages)) yield break;
        string[] pkgs;
        try { pkgs = Directory.GetDirectories(packages, "*ffmpeg*"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var pkg in pkgs)
        {
            yield return Path.Combine(pkg, "bin", "ffmpeg.exe");
            string[] subs;
            try { subs = Directory.GetDirectories(pkg); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs) yield return Path.Combine(sub, "bin", "ffmpeg.exe");
        }
    }
}
