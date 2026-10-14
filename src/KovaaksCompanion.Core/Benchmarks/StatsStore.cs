using System.Text.Json;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);

/// <summary>One read of the archive (see <see cref="StatsStore.ReadArchive"/>) and the views of it a reload needs.</summary>
public sealed class ArchiveSnapshot
{
    readonly Dictionary<string, CachedRuns> _all;

    internal ArchiveSnapshot(Dictionary<string, CachedRuns> all) => _all = all;

    public Dictionary<string, CachedScores> Scores() =>
        _all.ToDictionary(kv => kv.Key, kv => new CachedScores(kv.Value.Runs.Select(r => new CloudScore(r.Time, r.Score)).ToList(), kv.Value.FetchedAt), StringComparer.OrdinalIgnoreCase);

    /// <summary>Runs with kept statistics.</summary>
    public List<RunRecord> Runs() => _all.SelectMany(p => p.Value.Runs.Select(r => r.Record).OfType<RunRecord>()).ToList();

    public Dictionary<string, double> BestScores()
    {
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, c) in _all) if (c.Runs.Count > 0) best[n] = c.Runs.Max(r => r.Score);
        return best;
    }
}
record CachedRun(DateTime Time, double Score, RunRecord? Record);
record CachedRuns(List<CachedRun> Runs, DateTime FetchedAt);

/// <summary>
/// Signed, masked binary store (<see cref="SignedFile"/>) so the app can show data at startup and offline: server bests in <c>cacheFolder</c> (refetchable), run archive in <c>archiveFolder</c> (kept for good). Writes are atomic; unreadable files are treated as missing.
/// </summary>
public sealed partial class StatsStore(string cacheFolder, string archiveFolder)
{
    readonly ArchiveDb _db = new(Path.Combine(archiveFolder, ArchiveDb.FileName));

    /// <summary>The archive database's file name inside the archive folder.</summary>
    public const string ArchiveDbFile = ArchiveDb.FileName;

    /// <summary>One lock for every instance: <see cref="AppSettings.OpenStore"/> makes a new store per call, and they all read-modify-write the same files.</summary>
    static readonly object _gate = new();

    string FilePath(string name) => Path.Combine(cacheFolder, name);

    /// <summary>The player's server totals per scenario (one sync); empty until the first sync has run.</summary>
    public Dictionary<string, ScenarioTotals> LoadTotals(string steamId)
    {
        var prefix = steamId + ":";
        return Read<ScenarioTotals>(TotalsFile).Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stores the synced totals, keeping the higher score and plays of each scenario when it is already cached.</summary>
    public void SaveTotals(string steamId, IReadOnlyDictionary<string, ScenarioTotals> bests) =>
        Update<ScenarioTotals>(TotalsFile, d =>
        {
            foreach (var (name, b) in bests)
            {
                var key = steamId + ":" + name;
                d[key] = d.TryGetValue(key, out var old) ? new(Math.Max(old.Score, b.Score), Math.Max(old.Plays, b.Plays)) : b;
            }
        });

    /// <summary>The earlier run archive file; imported into the database once (see <see cref="ArchiveDb"/>).</summary>
    public const string RunsFile = "runs.dat";
    public const string TotalsFile = "totals" + CacheFile.Extension, SyncFile = "sync" + CacheFile.Extension;

    /// <summary>When the player's server totals were last downloaded, so a restart within the sync interval does not download them again.</summary>
    public DateTime? LoadSyncedAt(string steamId) => Read<DateTime>(SyncFile) is var d && d.TryGetValue(steamId, out var at) ? at : null;

    public void SaveSyncedAt(string steamId, DateTime at) => Update<DateTime>(SyncFile, d => d[steamId] = at);

    /// <summary>The whole archive read once; ask it for the parts a reload needs instead of reading the database for each.</summary>
    public ArchiveSnapshot ReadArchive() => new(_db.ReadAll());

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
    /// <see cref="ScenarioStats.SameRunWindow"/> of an entry with statistics is that run.
    /// </summary>
    internal static List<CachedRun> Merge(IEnumerable<CachedRun> a, IEnumerable<CachedRun> b)
    {
        var all = a.Concat(b).GroupBy(r => (r.Time, r.Score)).Select(g => g.FirstOrDefault(r => r.Record != null) ?? g.First()).OrderBy(r => r.Time).ToList();
        var recorded = all.Where(r => r.Record != null).Select(r => r.Time).ToArray();
        if (recorded.Length == 0) return all;
        return all.Where(r =>
        {
            if (r.Record != null) return true;
            var i = Array.BinarySearch(recorded, r.Time);
            if (i >= 0) return false;
            i = ~i;
            return !((i < recorded.Length && recorded[i] - r.Time < ScenarioStats.SameRunWindow) || (i > 0 && r.Time - recorded[i - 1] < ScenarioStats.SameRunWindow));
        }).ToList();
    }

    Dictionary<string, V> Read<V>(string name)
    {
        lock (_gate)
            try { return SignedFile.TryReadMasked(FilePath(name)) is { } b ? JsonSerializer.Deserialize<Dictionary<string, V>>(b) ?? [] : []; }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { return []; } // corrupt: treated as missing, the server refills it
    }

    void Update<V>(string name, Action<Dictionary<string, V>> change)
    {
        lock (_gate)
        {
            var d = Read<V>(name);
            change(d);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath(name))!);
            SignedFile.WriteMasked(FilePath(name), JsonSerializer.SerializeToUtf8Bytes(d));
        }
    }
}
