using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Session;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Library;

/// <summary>All runs found in KovaaK's stats folder, grouped by scenario (case-insensitive).</summary>
public sealed class RunLibrary
{
    readonly Dictionary<string, List<RunRecord>> _byScenario;

    public RunLibrary(IEnumerable<RunRecord> runs)
    {
        _byScenario = runs
            .GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.End).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    public IEnumerable<string> Scenarios => _byScenario.Keys.Order(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<RunRecord> AllRuns => _byScenario.Values.SelectMany(x => x);

    /// <summary>Runs of one scenario, oldest first. Empty when never played.</summary>
    public IReadOnlyList<RunRecord> Runs(string scenario) =>
        _byScenario.TryGetValue(scenario, out var l) ? l : [];

    /// <summary>
    /// Drops runs whose score differs from the signed score of their session (matched by <see cref="SessionStore.FindRun"/>):
    /// the stats CSV was edited. Runs without a session cannot be checked and stay.
    /// </summary>
    public RunLibrary Verified(IReadOnlyList<SessionInfo> sessions) => new(AllRuns.Where(r =>
        SessionStore.FindRun(sessions, r.Scenario, r.End) is var i && (i < 0 || Math.Abs(sessions[i].Score - r.Score) < 1e-6)));

    /// <summary>Summary of the run's .perf, or null when it is missing or unreadable (older runs may have none).</summary>
    static PerfSummary? ReadPerf(string statsCsv)
    {
        try
        {
            var path = PerfParser.PathFor(statsCsv);
            return File.Exists(path) ? PerfSummary.From(PerfParser.Parse(File.ReadAllBytes(path))) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PerfFormatException) { return null; }
    }

    /// <summary>Parses every stats CSV in the folder. Unreadable or foreign files are skipped.</summary>
    public static RunLibrary Scan(string statsFolder)
    {
        if (!Directory.Exists(statsFolder)) return new RunLibrary([]);
        var files = Directory.EnumerateFiles(statsFolder, "* Stats.csv").ToArray();
        var runs = new RunRecord?[files.Length];
        Parallel.For(0, files.Length, i =>
        {
            try
            {
                using var fs = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                runs[i] = RunRecord.From(StatsCsvParser.Parse(files[i], sr.ReadToEnd()), ReadPerf(files[i]));
            }
            catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or ArgumentException) { }
        });
        return new RunLibrary(runs.OfType<RunRecord>());
    }
}
