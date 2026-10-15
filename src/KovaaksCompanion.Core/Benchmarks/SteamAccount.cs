using System.Text.RegularExpressions;

namespace KovaaksCompanion.Core.Benchmarks;

public static partial class SteamAccount
{
    [GeneratedRegex("\"(?<id>7656\\d{13})\"\\s*\\{(?<body>[^}]*)\\}")]
    private static partial Regex UserBlock();

    [GeneratedRegex("\"(?<key>[A-Za-z]+)\"\\s+\"(?<value>[^\"]*)\"")]
    private static partial Regex Pair();

    /// <summary>The SteamID64 Steam last logged in with, from config/loginusers.vdf. Null when unknown.</summary>
    public static string? FromLoginUsers(string vdf)
    {
        string? best = null;
        long bestStamp = -1;
        foreach (Match m in UserBlock().Matches(vdf))
        {
            var kv = Pair().Matches(m.Groups["body"].Value).ToDictionary(x => x.Groups["key"].Value, x => x.Groups["value"].Value);
            var stamp = kv.TryGetValue("MostRecent", out var mr) && mr == "1" ? long.MaxValue
                : kv.TryGetValue("AutoLogin", out var al) && al == "1" ? long.MaxValue - 1
                : long.TryParse(kv.GetValueOrDefault("Timestamp"), out var t) ? t : 0;
            if (stamp > bestStamp) { bestStamp = stamp; best = m.Groups["id"].Value; }
        }
        return best;
    }

    /// <summary>Steam display name of <paramref name="steamId"/> from loginusers.vdf, and Steam's cached avatar file (config/avatarcache). Either is null when unknown.</summary>
    public static (string? Name, string? AvatarPath) Profile(string kovaaksPath, string steamId)
    {
        try
        {
            var root = FindRoot(kovaaksPath);
            if (root == null || steamId.Length == 0) return (null, null);
            string? name = null;
            var vdf = Path.Combine(root, "config", "loginusers.vdf");
            if (File.Exists(vdf))
                foreach (Match m in UserBlock().Matches(File.ReadAllText(vdf)))
                    if (m.Groups["id"].Value == steamId)
                    {
                        var kv = Pair().Matches(m.Groups["body"].Value).ToDictionary(x => x.Groups["key"].Value, x => x.Groups["value"].Value);
                        name = kv.GetValueOrDefault("PersonaName");
                    }
            var avatar = Path.Combine(root, "config", "avatarcache", steamId + ".png");
            return (string.IsNullOrWhiteSpace(name) ? null : name, File.Exists(avatar) ? avatar : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null); }
    }

    /// <summary>The Steam install folder (the one holding config\loginusers.vdf): the registry first, then the folder KovaaK's sits in when its library is the Steam folder itself.
    /// KovaaK's can live in another Steam library, so its path alone does not lead to the Steam install.</summary>
    public static string? FindRoot(string kovaaksPath) => FindRoot(kovaaksPath, PathDetector.ReadRegistry, File.Exists);

    public static string? FindRoot(string kovaaksPath, Func<string, string, string, string?> registry, Func<string, bool> fileExists)
    {
        var candidates = new List<string?>
        {
            registry("HKCU", @"Software\Valve\Steam", "SteamPath"),
            registry("HKLM", @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
        };
        try { candidates.Add(new DirectoryInfo(kovaaksPath).Parent?.Parent?.Parent?.FullName); }
        catch (ArgumentException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("steam account", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); }
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            var root = c.Replace('/', '\\');
            if (fileExists(Path.Combine(root, "config", "loginusers.vdf"))) return root;
        }
        return null;
    }

    /// <summary>The SteamID64 Steam last logged in with. Null when unknown.</summary>
    public static string? Detect(string kovaaksPath)
    {
        try
        {
            var root = FindRoot(kovaaksPath);
            if (root != null && FromLoginUsers(File.ReadAllText(Path.Combine(root, "config", "loginusers.vdf"))) is { } id) return id;
            return ActiveUser(PathDetector.ReadRegistryDword);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>SteamID64 of the account logged in right now (HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser holds its 32-bit account id; 0 when logged out).</summary>
    public static string? ActiveUser(Func<string, string, string, long?> dword)
        => dword("HKCU", @"Software\Valve\Steam\ActiveProcess", "ActiveUser") is > 0 and var acc ? (SteamId64Base + acc).ToString() : null;

    const long SteamId64Base = 76561197960265728;
}
