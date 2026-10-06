using System.Globalization;
using System.Text.RegularExpressions;

namespace KovaaksCompanion.Core.Stats;

/// <summary>
/// Parses the "&lt;Scenario&gt; - Challenge - yyyy.MM.dd-HH.mm.ss Stats.csv" files KovaaK's writes when a run ends.
/// The file holds only times of day, so dates come from the file name (the run's end).
/// </summary>
public static partial class StatsCsvParser
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    [GeneratedRegex(@"^(?<scenario>.+) - Challenge - (?<end>\d{4}\.\d{2}\.\d{2}-\d{2}\.\d{2}\.\d{2}) Stats\.csv$")]
    private static partial Regex FileNamePattern();

    public static RunStats Parse(string fileName, string text)
    {
        var m = FileNamePattern().Match(Path.GetFileName(fileName));
        if (!m.Success) throw new FormatException($"Not a KovaaK's stats file name: {fileName}");
        var end = DateTime.ParseExact(m.Groups["end"].Value, "yyyy.MM.dd-HH.mm.ss", Inv);

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var values = ReadKeyValues(lines);

        var start = values.TryGetValue("Challenge Start", out var cs) ? AtOrBefore(end, ParseTime(cs)) : end;

        return new RunStats
        {
            Scenario = values.GetValueOrDefault("Scenario", m.Groups["scenario"].Value),
            Start = start,
            End = end,
            Score = Double(values, "Score"),
            Kills = Int(values, "Kills"),
            HitCount = Int(values, "Hit Count"),
            MissCount = Int(values, "Miss Count"),
            Hash = values.GetValueOrDefault("Hash", ""),
            GameVersion = values.GetValueOrDefault("Game Version", ""),
            PauseCount = Int(values, "Pause Count"),
            PauseDuration = TimeSpan.FromSeconds(Double(values, "Pause Duration")),
            Settings = new InputSettings
            {
                SensScale = values.GetValueOrDefault("Sens Scale", ""),
                HorizSens = Double(values, "Horiz Sens"),
                VertSens = Double(values, "Vert Sens"),
                Dpi = Int(values, "DPI"),
                Fov = Double(values, "FOV"),
                FovScale = values.GetValueOrDefault("FOVScale", ""),
                Resolution = values.GetValueOrDefault("Resolution", ""),
            },
            KillEvents = ReadKills(lines, start),
        };
    }

    /// <summary>"Key:,Value" lines. The first occurrence of a key wins.</summary>
    static Dictionary<string, string> ReadKeyValues(string[] lines)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            var comma = line.IndexOf(":,", StringComparison.Ordinal);
            if (comma <= 0) continue;
            d.TryAdd(line[..comma], line[(comma + 2)..].Trim());
        }
        return d;
    }

    static List<KillEvent> ReadKills(string[] lines, DateTime start)
    {
        var kills = new List<KillEvent>();
        var i = Array.FindIndex(lines, l => l.StartsWith("Kill #,", StringComparison.Ordinal));
        if (i < 0) return kills;

        for (i++; i < lines.Length && lines[i].Length > 0; i++)
        {
            var c = lines[i].Split(',');
            kills.Add(new KillEvent(
                Index: int.Parse(c[0], Inv),
                Time: AtOrAfter(start, ParseTime(c[1])),
                Bot: c[2],
                Weapon: c[3],
                Ttk: TimeSpan.FromSeconds(double.Parse(c[4].TrimEnd('s'), Inv)),
                Shots: int.Parse(c[5], Inv),
                Hits: int.Parse(c[6], Inv),
                OverShots: c.Length > 12 ? int.Parse(c[12], Inv) : 0));
        }
        return kills;
    }

    static TimeSpan ParseTime(string s) => TimeSpan.ParseExact(s, @"hh\:mm\:ss\.fff", Inv);

    /// <summary>Latest moment with this time of day that is not after <paramref name="anchor"/>.</summary>
    static DateTime AtOrBefore(DateTime anchor, TimeSpan timeOfDay)
    {
        var t = anchor.Date + timeOfDay;
        return t > anchor ? t.AddDays(-1) : t;
    }

    /// <summary>Earliest moment with this time of day that is not before <paramref name="anchor"/>.</summary>
    static DateTime AtOrAfter(DateTime anchor, TimeSpan timeOfDay)
    {
        var t = anchor.Date + timeOfDay;
        return t < anchor ? t.AddDays(1) : t;
    }

    static double Double(Dictionary<string, string> d, string key) =>
        d.TryGetValue(key, out var v) && double.TryParse(v, NumberStyles.Float, Inv, out var x) ? x : 0;

    static int Int(Dictionary<string, string> d, string key) => (int)Double(d, key);
}
