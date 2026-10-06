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

    /// <summary>Steam root derived from the KovaaK's folder (…\Steam\steamapps\common\FPSAimTrainer).</summary>
    public static string? Detect(string kovaaksPath)
    {
        try
        {
            var root = new DirectoryInfo(kovaaksPath).Parent?.Parent?.Parent?.FullName;
            var file = root == null ? null : Path.Combine(root, "config", "loginusers.vdf");
            return file != null && File.Exists(file) ? FromLoginUsers(File.ReadAllText(file)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
