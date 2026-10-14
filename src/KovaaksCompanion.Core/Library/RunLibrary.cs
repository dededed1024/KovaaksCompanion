using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Replays;
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
    /// Drops runs whose score differs from the score of their replay (matched by <see cref="ReplayStore.FindRun"/>):
    /// the stats CSV was edited. Runs without a replay cannot be checked and stay.
    /// </summary>
    public RunLibrary Verified(IReadOnlyList<ReplayInfo> replays) => new(AllRuns.Where(r =>
        ReplayStore.FindRun(replays, r.Scenario, r.End) is var i && (i < 0 || Math.Abs(replays[i].Score - r.Score) < 1e-6)));

    /// <summary>Adds the archived statistics of deleted runs whose stats CSV is gone (matched by scenario and end time).</summary>
    public RunLibrary WithArchived(IReadOnlyCollection<RunRecord> archived)
    {
        if (archived.Count == 0) return this;
        var have = AllRuns.Select(r => (r.Scenario.ToLowerInvariant(), r.End)).ToHashSet();
        return new(AllRuns.Concat(archived.Where(r => !have.Contains((r.Scenario.ToLowerInvariant(), r.End)))));
    }

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

    /// <summary>Bump when <see cref="ScanEntry"/> or <see cref="RunRecord"/> changes shape, so older scan caches are rebuilt.</summary>
    const int ScanSchema = 3;

    /// <summary>What a scanned CSV looked like (and its .perf, which feeds the run too); the cached run is reused while neither file changes.</summary>
    record ScanEntry(FileStamp Csv, FileStamp Perf, RunRecord? Run);

    static (FileStamp Csv, FileStamp Perf) Stamp(string statsCsv) => (FileStamp.Of(statsCsv), FileStamp.OfOptional(PerfParser.PathFor(statsCsv)));

    /// <summary>
    /// The local runs as every screen shows them: the stats folder scanned, runs whose replay disagrees with the CSV dropped, runs only the archive still has
    /// added back, and the DPI corrections applied. With <paramref name="archiveNew"/>, runs the archive does not hold yet are kept in it first (so they outlive their CSV).
    /// Also returns the archive as it was read for this, so a caller that needs its scores does not read the database again.
    /// </summary>
    public static (RunLibrary Library, ArchiveSnapshot Archive) Load(string statsFolder, string? scanCacheFile, IReadOnlyList<ReplayInfo> replays,
        StatsStore store, IReadOnlyDictionary<string, int> dpiFixes, bool archiveNew)
    {
        var scanned = Scan(statsFolder, scanCacheFile).Verified(replays);
        if (archiveNew)
        {
            try { store.ArchiveNew(scanned.AllRuns); }
            catch (IOException) { } // stats-folder runs played without the app would be lost with their CSV; the next load tries again
        }
        var archive = store.ReadArchive();
        return (scanned.WithArchived(archive.Runs()).WithDpiFixes(dpiFixes), archive);
    }

    /// <summary>
    /// Parses every stats CSV in the folder. Unreadable or foreign files are skipped. With <paramref name="cacheFile"/>, only the CSVs
    /// that are new or changed since the last scan are parsed; the rest come from the cache.
    /// </summary>
    public static RunLibrary Scan(string statsFolder, string? cacheFile = null)
    {
        if (!Directory.Exists(statsFolder)) return new RunLibrary([]);
        var files = Directory.EnumerateFiles(statsFolder, "* Stats.csv").ToArray();
        var known = cacheFile == null ? [] : CacheFile.Read<ScanEntry>(cacheFile, ScanSchema);
        var runs = new RunRecord?[files.Length];
        var entries = new ScanEntry?[files.Length];
        var parsed = 0;
        Parallel.For(0, files.Length, i =>
        {
            (FileStamp Csv, FileStamp Perf) stamp;
            try { stamp = Stamp(files[i]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            if (known.TryGetValue(Path.GetFileName(files[i]), out var hit) && hit.Csv == stamp.Csv && hit.Perf == stamp.Perf) { (runs[i], entries[i]) = (hit.Run, hit); return; }
            Interlocked.Increment(ref parsed);
            try
            {
                using var fs = new FileStream(files[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                runs[i] = RunRecord.From(StatsCsvParser.Parse(files[i], sr.ReadToEnd()), ReadDrift(files[i]));
                entries[i] = new(stamp.Csv, stamp.Perf, runs[i]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // maybe still being written: not cached
            catch (Exception e) when (e is FormatException or ArgumentException) { entries[i] = new(stamp.Csv, stamp.Perf, null); } // foreign file: remembered as no run
        });
        if (cacheFile != null && (parsed > 0 || known.Count != entries.Count(e => e != null)))
            CacheFile.Write(cacheFile, ScanSchema, files.Zip(entries).Where(p => p.Second != null).ToDictionary(p => Path.GetFileName(p.First), p => p.Second!, StringComparer.OrdinalIgnoreCase));
        return new RunLibrary(runs.OfType<RunRecord>());
    }
}
