using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.Core.Library;

/// <summary>All local runs, grouped by scenario (case-insensitive).</summary>
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
    /// Recomputes the sensitivity of runs whose recorded DPI and sensitivity have a correction (<see cref="RunRecord.DpiKey"/> to the real DPI).
    /// Idempotent: it starts from the recorded values, so removing a correction restores the run.
    /// </summary>
    public RunLibrary WithDpiFixes(IReadOnlyDictionary<string, int> fixes) => new(AllRuns.Select(r =>
    {
        var raw = r with { Cm360 = r.RawCm360, FixedDpi = 0 };
        if (raw.DpiKey is { } k && raw.Cm360 is { } cm && fixes.TryGetValue(k, out var dpi) && dpi > 0 && dpi != raw.Dpi)
            return raw with { Cm360 = cm * raw.Dpi / dpi, FixedDpi = dpi };
        return raw;
    }));

    /// <summary>
    /// The local runs as every screen shows them, read from the archive database alone (<see cref="StatsIngest"/> and the replay saver put runs there), with the DPI corrections applied.
    /// Also returns the archive as it was read for this, so a caller that needs its scores does not read the database again.
    /// </summary>
    public static (RunLibrary Library, ArchiveSnapshot Archive) Load(StatsStore store, IReadOnlyDictionary<string, int> dpiFixes)
    {
        var archive = store.ReadArchive();
        return (new RunLibrary(archive.Runs()).WithDpiFixes(dpiFixes), archive);
    }
}
