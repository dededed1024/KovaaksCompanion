using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Library;

/// <summary>
/// The one place KovaaK's stats CSVs and .perf files are read besides the replay saver: runs played while the app was not running move into the archive database,
/// so every screen can read the database alone. Runs the app saw are archived from their replay.
/// </summary>
public static class StatsIngest
{
    /// <summary>The run's end in the archive is its file name plus one second, or the last kill when later; an archived end this close after a file name is that run.</summary>
    static readonly TimeSpan NameSlack = TimeSpan.FromSeconds(3);

    /// <summary>Archives the runs of the stats folder the database holds none of yet. Returns how many it added; unreadable files are left for the next time.</summary>
    public static int Run(string statsFolder, StatsStore store)
    {
        if (!Directory.Exists(statsFolder)) return 0;
        var ends = store.ArchivedKeys().GroupBy(k => k.Scenario, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(k => k.End).Order().ToArray(), StringComparer.OrdinalIgnoreCase);
        var fresh = new List<RunRecord>();
        foreach (var file in Directory.EnumerateFiles(statsFolder, "* Stats.csv"))
        {
            if (!StatsCsvParser.TryParseName(file, out var scenario, out var end) || IsArchived(ends, scenario, end)) continue;
            try
            {
                string text;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                fresh.Add(RunRecord.From(StatsCsvParser.Parse(file, text), ReadDrift(file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // maybe still being written
            catch (Exception e) when (e is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException) { AppLog.Write("stats", $"skipped {Path.GetFileName(file)}: {e.Message}"); }
        }
        return store.ArchiveNew(fresh);
    }

    static bool IsArchived(Dictionary<string, DateTime[]> ends, string scenario, DateTime nameEnd) =>
        ends.TryGetValue(scenario, out var list) && list.Any(e => e >= nameEnd && e - nameEnd <= NameSlack);

    /// <summary>Accuracy drift of the run's .perf, or null when it is missing or unreadable (older runs may have none).</summary>
    static AccuracyDrift? ReadDrift(string statsCsv)
    {
        try
        {
            var path = PerfParser.PathFor(statsCsv);
            return File.Exists(path) ? AccuracyDrift.From(PerfParser.Parse(File.ReadAllBytes(path))) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PerfFormatException) { return null; }
    }
}
