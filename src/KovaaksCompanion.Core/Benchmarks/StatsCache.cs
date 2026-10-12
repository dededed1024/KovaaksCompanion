using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedProgress(string Json, DateTime FetchedAt);
public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);

/// <summary>
/// Signed, masked binary cache (<see cref="SignedFile"/>) of server data under one folder (benchmark progress, cloud last scores, usernames) so the app can show
/// data at startup and offline. Writes are atomic; unreadable files are treated as missing.
/// </summary>
public sealed class StatsCache(string folder)
{
    readonly object _gate = new();

    string FilePath(string name) => Path.Combine(folder, name);

    /// <summary>Raw player-progress JSON per KovaaksBenchmarkId; parse with <see cref="BenchmarkProgress.Parse"/>.</summary>
    public CachedProgress? LoadProgress(string benchmarkId) => Read<CachedProgress>("progress.dat").GetValueOrDefault(benchmarkId);
    public void SaveProgress(string benchmarkId, string json, DateTime? now = null) =>
        Update<CachedProgress>("progress.dat", d => d[benchmarkId] = new(json, now ?? DateTime.Now));

    /// <summary>Every cached progress entry in one read (the per-id loaders re-read the file each time).</summary>
    public Dictionary<string, CachedProgress> LoadAllProgress() => Read<CachedProgress>("progress.dat");
    public void SaveProgressBatch(IReadOnlyDictionary<string, string> items, DateTime? now = null) =>
        Update<CachedProgress>("progress.dat", d => { foreach (var (id, json) in items) d[id] = new(json, now ?? DateTime.Now); });

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

    /// <summary>Every cached cloud-scores entry in one read, with the scores of deleted runs merged in.</summary>
    public Dictionary<string, CachedScores> LoadAllScores()
    {
        var all = Read<CachedScores>("scores.dat");
        foreach (var (n, l) in Read<List<CloudScore>>(DeletedFile))
            all[n] = all.TryGetValue(n, out var c) ? c with { Scores = Merge(c.Scores, l) } : new(l, DateTime.MinValue);
        return all;
    }
    public void SaveScoresBatch(IReadOnlyDictionary<string, List<CloudScore>> items, DateTime? now = null) =>
        Update<CachedScores>("scores.dat", d => { foreach (var (n, l) in items) d[n] = new(l, now ?? DateTime.Now); });

    /// <summary>Cloud last scores per scenario name.</summary>
    public CachedScores? LoadScores(string scenario) => LoadAllScores().GetValueOrDefault(scenario);
    public void SaveScores(string scenario, IEnumerable<CloudScore> scores, DateTime? now = null) =>
        Update<CachedScores>("scores.dat", d => d[scenario] = new(scores.ToList(), now ?? DateTime.Now));

    const string DeletedFile = "deleted.dat";

    /// <summary>Remembers a deleted run: it stays in the score history like a server score, and <see cref="HiddenRuns"/> keeps it out of run lists.</summary>
    public void HideRun(string scenario, DateTime end, double score) =>
        Update<List<CloudScore>>(DeletedFile, d =>
        {
            if (!d.TryGetValue(scenario, out var l)) d[scenario] = l = [];
            if (!l.Any(s => s.Time == end)) l.Add(new CloudScore(end, score));
        });

    /// <summary>Scores of deleted runs per scenario.</summary>
    public Dictionary<string, List<CloudScore>> DeletedScores() => Read<List<CloudScore>>(DeletedFile);

    /// <summary>Deleted runs as (lower-case scenario, end time).</summary>
    public HashSet<(string Scenario, DateTime End)> HiddenRuns() =>
        Read<List<CloudScore>>(DeletedFile).SelectMany(p => p.Value.Select(s => (p.Key.ToLowerInvariant(), s.Time))).ToHashSet();

    /// <summary>Best score of the deleted runs per scenario.</summary>
    public Dictionary<string, double> DeletedBest()
    {
        var best = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (n, l) in Read<List<CloudScore>>(DeletedFile)) if (l.Count > 0) best[n] = l.Max(s => s.Score);
        return best;
    }

    /// <summary>Scores fetched from the server plus the scenario's deleted runs, oldest first, without repeats.</summary>
    public List<CloudScore> WithDeleted(string scenario, IEnumerable<CloudScore> scores) =>
        Merge(scores, Read<List<CloudScore>>(DeletedFile).GetValueOrDefault(scenario) ?? []);

    static List<CloudScore> Merge(IEnumerable<CloudScore> a, IEnumerable<CloudScore> b) =>
        a.Concat(b).GroupBy(s => (s.Time, s.Score)).Select(g => g.First()).OrderBy(s => s.Time).ToList();

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
            Directory.CreateDirectory(folder);
            SignedFile.WriteMasked(FilePath(name), JsonSerializer.SerializeToUtf8Bytes(d));
        }
    }
}
