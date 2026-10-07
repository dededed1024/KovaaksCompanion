using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record CachedProgress(string Json, DateTime FetchedAt);
public sealed record CachedScores(List<CloudScore> Scores, DateTime FetchedAt);

/// <summary>
/// Signed binary cache (<see cref="SignedFile"/>) of server data under one folder (benchmark progress, cloud last scores, usernames) so the app can show
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

    /// <summary>Every cached cloud-scores entry in one read.</summary>
    public Dictionary<string, CachedScores> LoadAllScores() => Read<CachedScores>("scores.dat");
    public void SaveScoresBatch(IReadOnlyDictionary<string, List<CloudScore>> items, DateTime? now = null) =>
        Update<CachedScores>("scores.dat", d => { foreach (var (n, l) in items) d[n] = new(l, now ?? DateTime.Now); });

    /// <summary>Cloud last scores per scenario name.</summary>
    public CachedScores? LoadScores(string scenario) => Read<CachedScores>("scores.dat").GetValueOrDefault(scenario);
    public void SaveScores(string scenario, IEnumerable<CloudScore> scores, DateTime? now = null) =>
        Update<CachedScores>("scores.dat", d => d[scenario] = new(scores.ToList(), now ?? DateTime.Now));

    /// <summary>Resolved webapp username per steamId.</summary>
    public string? LoadUsername(string steamId) => Read<string>("usernames.dat").GetValueOrDefault(steamId);
    public void SaveUsername(string steamId, string username) =>
        Update<string>("usernames.dat", d => d[steamId] = username);

    Dictionary<string, V> Read<V>(string name)
    {
        lock (_gate)
            try { return SignedFile.TryRead(FilePath(name)) is { } b ? JsonSerializer.Deserialize<Dictionary<string, V>>(b) ?? [] : []; }
            catch (JsonException) { return []; }
    }

    void Update<V>(string name, Action<Dictionary<string, V>> change)
    {
        lock (_gate)
        {
            var d = Read<V>(name);
            change(d);
            Directory.CreateDirectory(folder);
            SignedFile.Write(FilePath(name), JsonSerializer.SerializeToUtf8Bytes(d));
        }
    }
}
