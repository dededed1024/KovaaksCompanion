using System.Text.Json;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);

/// <summary>One read of the archive (see <see cref="StatsStore.ReadArchive"/>) and the views of it a reload needs.</summary>
public sealed class ArchiveSnapshot
{
    readonly Dictionary<string, CachedRuns> _all;
    readonly Dictionary<string, double> _bests;

    internal ArchiveSnapshot(Dictionary<string, CachedRuns> all, Dictionary<string, double> bests) => (_all, _bests) = (all, bests);

    public Dictionary<string, CachedScores> Scores() =>
        _all.ToDictionary(kv => kv.Key, kv => new CachedScores(kv.Value.Runs.Select(r => new CloudScore(r.Time, r.Score)).ToList(), kv.Value.FetchedAt), StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs with kept statistics.</summary>
    public List<RunRecord> Runs() => _all.SelectMany(p => p.Value.Runs.Select(r => r.Record).OfType<RunRecord>()).ToList();

    /// <summary>The highest archived score per scenario: the plays kept (cloud scores and runs with statistics alike) and the best the server reported.</summary>
    public Dictionary<string, double> ArchivedBests()
    {
        var best = new Dictionary<string, double>(_bests, StringComparer.OrdinalIgnoreCase);
        foreach (var (n, c) in _all)
            if (c.Runs.Count > 0) best[n] = Math.Max(best.GetValueOrDefault(n), c.Runs.Max(r => r.Score));
        return best;
    }
}
record CachedRun(DateTime Time, double Score, RunRecord? Record);
record CachedRuns(List<CachedRun> Runs, DateTime FetchedAt);

/// <summary>
/// The archive database (<see cref="ArchiveDb"/>) as the app uses it: what only this PC knows and no server gives back (run statistics, cloud scores older than the few the server still lists, the server's best scores, session ids). Writes are atomic; an unreadable file is set aside.
/// </summary>
public sealed partial class StatsStore(string archiveFolder)
{
    readonly ArchiveDb _db = ArchiveDb.At(Path.Combine(archiveFolder, ArchiveDb.FileName));

    /// <summary>The archive database's file name inside the archive folder.</summary>
    public const string ArchiveDbFile = ArchiveDb.FileName;

    /// <summary>True once the server's best scores were downloaded (see <see cref="SaveCloudBests"/>).</summary>
    public bool CloudBestsSynced() => _db.BestsSynced();

    /// <summary>Keeps the server's best scores, the higher one when a scenario already has one, and marks the download as done.</summary>
    public void SaveCloudBests(IReadOnlyDictionary<string, double> bests) => _db.SaveBests(bests);

    /// <summary>The earlier run archive file; imported into the database once (see <see cref="ArchiveDb"/>).</summary>
    public const string RunsFile = "runs.dat";

    /// <summary>The whole archive read once; ask it for the parts a reload needs instead of reading the database for each.</summary>
    public ArchiveSnapshot ReadArchive() => new(_db.ReadAll(), _db.ReadBests());

    /// <summary>Every cached run score per scenario in one read, whatever its origin (server, played here, deleted), with the time the server was last asked.</summary>
    public Dictionary<string, CachedScores> LoadAllScores() => ReadArchive().Scores();

    public void SaveScoresBatch(IReadOnlyDictionary<string, List<CloudScore>> items, DateTime? now = null) =>
        _db.Update(items.Keys, (n, old) => new(Merge(old?.Runs ?? [], items[n].Select(c => new CachedRun(c.Time, c.Score, null))), now ?? DateTime.Now));

    public void SaveScores(string scenario, IEnumerable<CloudScore> scores, DateTime? now = null) =>
        SaveScoresBatch(new Dictionary<string, List<CloudScore>> { [scenario] = scores.ToList() }, now);

    /// <summary>Keeps runs' full statistics (accuracy, kills, drift, sens), e.g. of a replay folder, so they still count once it is deleted.</summary>
    public void Archive(IEnumerable<RunRecord> records)
    {
        var list = records.ToList();
        if (list.Count == 0) return;
        var byScenario = list.GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        _db.Update(byScenario.Keys, (n, old) => new(Merge(old?.Runs ?? [], byScenario[n].Select(r => new CachedRun(r.End, r.Score, r))), old?.FetchedAt ?? DateTime.MinValue));
    }

    /// <summary>A copy of the archive database (<c>.bak</c>) at most once a day, which a corrupt database is restored from.</summary>
    public void BackupArchive() => _db.Backup(TimeSpan.FromDays(1));

    /// <summary>Keeps the statistics of the runs the archive holds none of yet (matched by scenario and end time), e.g. stats-folder runs played without the app.</summary>
    public int ArchiveNew(IEnumerable<RunRecord> runs)
    {
        var have = _db.ArchivedKeys();
        var fresh = runs.Where(r => !have.Contains((r.Scenario.ToLowerInvariant(), r.End))).ToList();
        Archive(fresh);
        return fresh.Count;
    }

    /// <summary>
    /// Adds the reload and distance figures of <paramref name="fresh"/> to the archived runs of the same end time that lack them (runs archived before they were kept).
    /// Nothing else of an archived run changes.
    /// </summary>
    public void AddRunFigures(IReadOnlyCollection<RunRecord> fresh)
    {
        if (fresh.Count == 0) return;
        var byScenario = fresh.GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.End), StringComparer.OrdinalIgnoreCase);
        _db.Update(byScenario.Keys, (n, old) => new(
            (old?.Runs ?? []).Select(c => c.Record is { Reloads: null } rec && byScenario[n].TryGetValue(c.Time, out var f)
                ? c with { Record = rec with { Reloads = f.Reloads, Distance = f.Distance } } : c).ToList(),
            old?.FetchedAt ?? DateTime.MinValue));
    }

    public void Archive(RunRecord? record) { if (record != null) Archive([record]); }

    /// <summary>Runs with kept statistics.</summary>
    public List<RunRecord> ArchivedRuns() => ReadArchive().Runs();

    /// <summary>Scenario (lower case) and end time of every archived run, without decoding the runs.</summary>
    public HashSet<(string Scenario, DateTime End)> ArchivedKeys() => _db.ArchivedKeys();

    /// <summary>The given scores plus what is already cached for the scenario, oldest first, without repeats.</summary>
    public List<CloudScore> WithCached(string scenario, IEnumerable<CloudScore> scores) =>
        Merge(_db.Read(scenario)?.Runs ?? [], scores.Select(c => new CachedRun(c.Time, c.Score, null)))
            .Select(r => new CloudScore(r.Time, r.Score)).ToList();

    /// <summary>
    /// Union by time and score; an entry with statistics wins over one without, and a score without statistics within
    /// <see cref="ScenarioHistory.SameRunWindow"/> of an entry with statistics is that run.
    /// </summary>
    internal static List<CachedRun> Merge(IEnumerable<CachedRun> a, IEnumerable<CachedRun> b)
    {
        var all = a.Concat(b).GroupBy(r => (r.Time, r.Score)).Select(g => g.FirstOrDefault(r => r.Record != null) ?? g.First()).OrderBy(r => r.Time).ToList();
        var recorded = all.Where(r => r.Record != null).Select(r => r.Time).ToArray();
        if (recorded.Length == 0) return all;
        return all.Where(r => r.Record != null || !ScenarioHistory.IsSameRun(recorded, r.Time)).ToList();
    }
}
