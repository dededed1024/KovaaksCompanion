using System.Text.Json;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);
record CachedRun(DateTime Time, double Score, RunRecord? Record);
record CachedRuns(List<CachedRun> Runs, DateTime FetchedAt);

/// <summary>
/// Signed, masked binary cache (<see cref="SignedFile"/>) of server data under one folder (server bests, run history, usernames; the run history lives in <c>runsFolder</c> when given) so the app can show
/// data at startup and offline. Writes are atomic; unreadable files are treated as missing.
/// </summary>
public sealed partial class StatsCache(string folder, string? runsFolder = null)
{
    readonly object _gate = new();

    string FilePath(string name) => Path.Combine(name == RunsFile ? runsFolder ?? folder : folder, name);

    /// <summary>The player's server bests per scenario (one sync); empty until the first sync has run.</summary>
    public Dictionary<string, ServerBest> LoadServerBests(string steamId)
    {
        var prefix = steamId + ":";
        return Read<ServerBest>("serverbest.dat").Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key[prefix.Length..], kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Stores the synced bests, keeping the higher score and plays of each scenario when it is already cached.</summary>
    public void SaveServerBests(string steamId, IReadOnlyDictionary<string, ServerBest> bests) =>
        Update<ServerBest>("serverbest.dat", d =>
        {
            foreach (var (name, b) in bests)
            {
                var key = steamId + ":" + name;
                d[key] = d.TryGetValue(key, out var old) ? new(Math.Max(old.Score, b.Score), Math.Max(old.Plays, b.Plays)) : b;
            }
        });

    public const string RunsFile = "runs.dat";

    /// <summary>Every cached run score per scenario in one read, whatever its origin (server, played here, deleted), with the time the server was last asked.</summary>
    public Dictionary<string, CachedScores> LoadAllScores() =>
        Read<CachedRuns>(RunsFile).ToDictionary(kv => kv.Key, kv => new CachedScores(kv.Value.Runs.Select(r => new CloudScore(r.Time, r.Score)).OrderBy(s => s.Time).ToList(), kv.Value.FetchedAt), StringComparer.OrdinalIgnoreCase);

    public CachedScores? LoadScores(string scenario) => LoadAllScores().GetValueOrDefault(scenario);

    public void SaveScoresBatch(IReadOnlyDictionary<string, List<CloudScore>> items, DateTime? now = null) =>
        Update<CachedRuns>(RunsFile, d =>
        {
            foreach (var (n, l) in items)
                d[n] = new(Merge(d.GetValueOrDefault(n)?.Runs ?? [], l.Select(c => new CachedRun(c.Time, c.Score, null))), now ?? DateTime.Now);
        });

    public void SaveScores(string scenario, IEnumerable<CloudScore> scores, DateTime? now = null) =>
        SaveScoresBatch(new Dictionary<string, List<CloudScore>> { [scenario] = scores.ToList() }, now);

    /// <summary>Keeps runs' full statistics (accuracy, kills, drift, sens), e.g. of a replay folder, so they still count once it is deleted.</summary>
    public void Archive(IEnumerable<RunRecord> records)
    {
        var list = records.ToList();
        if (list.Count == 0) return;
        Update<CachedRuns>(RunsFile, d =>
        {
            foreach (var g in list.GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase))
            {
                var old = d.GetValueOrDefault(g.Key);
                d[g.Key] = new(Merge(old?.Runs ?? [], g.Select(r => new CachedRun(r.End, r.Score, r))), old?.FetchedAt ?? DateTime.MinValue);
            }
        });
    }

    /// <summary>Keeps the statistics of the runs the cache holds none of yet (matched by scenario and end time), e.g. stats-folder runs played without the app.</summary>
    public int ArchiveNew(IEnumerable<RunRecord> runs)
    {
        var have = Read<CachedRuns>(RunsFile).SelectMany(p => p.Value.Runs).Where(r => r.Record != null).Select(r => (r.Record!.Scenario.ToLowerInvariant(), r.Record.End)).ToHashSet();
        var fresh = runs.Where(r => !have.Contains((r.Scenario.ToLowerInvariant(), r.End))).ToList();
        Archive(fresh);
        return fresh.Count;
    }

    public void Archive(RunRecord? record) { if (record != null) Archive([record]); }

    /// <summary>Runs with kept statistics.</summary>
    public List<RunRecord> ArchivedRuns() => Read<CachedRuns>(RunsFile).SelectMany(p => p.Value.Runs.Select(r => r.Record).OfType<RunRecord>()).ToList();

    /// <summary>Best cached score per scenario.</summary>
    public Dictionary<string, double> BestScores()
    {
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, c) in Read<CachedRuns>(RunsFile)) if (c.Runs.Count > 0) best[n] = c.Runs.Max(r => r.Score);
        return best;
    }

    /// <summary>The given scores plus what is already cached for the scenario, oldest first, without repeats.</summary>
    public List<CloudScore> WithCached(string scenario, IEnumerable<CloudScore> scores) =>
        Merge(Read<CachedRuns>(RunsFile).GetValueOrDefault(scenario)?.Runs ?? [], scores.Select(c => new CachedRun(c.Time, c.Score, null)))
            .Select(r => new CloudScore(r.Time, r.Score)).ToList();

    /// <summary>
    /// Union by time and score; an entry with statistics wins over one without, and a score without statistics within
    /// <see cref="ScenarioStats.SameRunWindow"/> of an entry with statistics is that run.
    /// </summary>
    static List<CachedRun> Merge(IEnumerable<CachedRun> a, IEnumerable<CachedRun> b)
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

    /// <summary>Resolved webapp username per steamId.</summary>
    public string? LoadUsername(string steamId) => Read<string>("usernames.dat").GetValueOrDefault(steamId);
    public void SaveUsername(string steamId, string username) =>
        Update<string>("usernames.dat", d => d[steamId] = username);

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
