using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedProgress(string Json, DateTime FetchedAt);
public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);

/// <summary>
/// JSON cache of server data under one folder (benchmark progress, cloud last scores, usernames) so the app can show
/// data at startup and offline. Writes are atomic; unreadable files are treated as missing.
/// </summary>
public sealed class StatsCache(string folder)
{
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    readonly object _gate = new();

    string FilePath(string name) => Path.Combine(folder, name);

    /// <summary>Raw player-progress JSON per KovaaksBenchmarkId; parse with <see cref="BenchmarkProgress.Parse"/>.</summary>
    public CachedProgress? LoadProgress(string benchmarkId) => Read<CachedProgress>("progress.json").GetValueOrDefault(benchmarkId);
    public void SaveProgress(string benchmarkId, string json, DateTime? now = null) =>
        Update<CachedProgress>("progress.json", d => d[benchmarkId] = new(json, now ?? DateTime.Now));

    /// <summary>Every cached progress entry in one read (the per-id loaders re-read the file each time).</summary>
    public Dictionary<string, CachedProgress> LoadAllProgress() => Read<CachedProgress>("progress.json");
    public void SaveProgressBatch(IReadOnlyDictionary<string, string> items, DateTime? now = null) =>
        Update<CachedProgress>("progress.json", d => { foreach (var (id, json) in items) d[id] = new(json, now ?? DateTime.Now); });

    /// <summary>Every cached cloud-scores entry in one read.</summary>
    public Dictionary<string, CachedScores> LoadAllScores() => Read<CachedScores>("scores.json");
    public void SaveScoresBatch(IReadOnlyDictionary<string, List<CloudScore>> items, DateTime? now = null) =>
        Update<CachedScores>("scores.json", d => { foreach (var (n, l) in items) d[n] = new(l, now ?? DateTime.Now); });

    /// <summary>Cloud last scores per scenario name.</summary>
    public CachedScores? LoadScores(string scenario) => Read<CachedScores>("scores.json").GetValueOrDefault(scenario);
    public void SaveScores(string scenario, IEnumerable<CloudScore> scores, DateTime? now = null) =>
        Update<CachedScores>("scores.json", d => d[scenario] = new(scores.ToList(), now ?? DateTime.Now));

    /// <summary>Resolved webapp username per steamId.</summary>
    public string? LoadUsername(string steamId) => Read<string>("usernames.json").GetValueOrDefault(steamId);
    public void SaveUsername(string steamId, string username) =>
        Update<string>("usernames.json", d => d[steamId] = username);

    Dictionary<string, V> Read<V>(string name)
    {
        lock (_gate)
            try { return JsonSerializer.Deserialize<Dictionary<string, V>>(File.ReadAllText(FilePath(name))) ?? []; }
            catch { return []; }
    }

    void Update<V>(string name, Action<Dictionary<string, V>> change)
    {
        lock (_gate)
        {
            var d = Read<V>(name);
            change(d);
            Directory.CreateDirectory(folder);
            var tmp = FilePath(name + ".tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(d, Opts));
            File.Move(tmp, FilePath(name), true);
        }
    }
}
