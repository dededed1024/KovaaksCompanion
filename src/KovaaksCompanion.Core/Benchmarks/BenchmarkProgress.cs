using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>Score in the same unit as the local stats CSV (the server sends hundredths).</summary>
public sealed record ScenarioProgress(string Scenario, double Score, int Rank, IReadOnlyList<double> RankMaxes, long LeaderboardId, int? LeaderboardRank);

/// <summary><see cref="Parent"/> is the benchmark site category the subcategory belongs to; empty when it cannot be matched.</summary>
public sealed record SubcategoryProgress(string Parent, string Name, double Progress, int Rank, IReadOnlyList<double> RankMaxes, IReadOnlyList<ScenarioProgress> Scenarios);

public sealed record BenchmarkProgress(double Progress, int OverallRank, IReadOnlyList<string> RankNames, IReadOnlyList<SubcategoryProgress> Subcategories)
{
    public string RankName(int rank) => rank >= 0 && rank < RankNames.Count ? RankNames[rank] : "";
    public string OverallRankName => RankName(OverallRank);
    public IEnumerable<ScenarioProgress> Scenarios => Subcategories.SelectMany(s => s.Scenarios);

    /// <summary>Playlist point cap: the sum of each subcategory's top rank max (its point cap).</summary>
    public double MaxProgress => Subcategories.Sum(s => s.RankMaxes.Count == 0 ? 0 : s.RankMaxes[^1]);

    /// <summary><see cref="Progress"/> over <see cref="MaxProgress"/> (0..1); null when the cap is unknown.</summary>
    public double? ProgressShare => MaxProgress > 0 ? Math.Clamp(Progress / MaxProgress, 0, 1) : null;

    /// <summary>Progress from the overall tier to the next: mean continuous tier progress (<see cref="Tier.ValueOf"/>) of the ranked scenarios minus <see cref="OverallRank"/>, clamped to 0..1; 1 at the top tier.</summary>
    public double OverallFraction
    {
        get
        {
            var ranked = Scenarios.Where(s => s.RankMaxes.Count > 0).ToList();
            if (OverallRank >= Math.Max(RankNames.Count - 1, 0)) return 1;
            if (ranked.Count == 0) return 0;
            return Math.Clamp(ranked.Average(s => Tier.ValueOf(s.Score, s.RankMaxes)) - OverallRank, 0, 1);
        }
    }

    /// <summary>
    /// Raises scenario scores (and their tiers) to a higher local best, and the overall tier to the mean tier progress when that is
    /// higher than the server's. Never lowers anything. Returns this when no local best beats the server.
    /// </summary>
    public BenchmarkProgress WithLocalBests(Func<string, double?> localBest)
    {
        var changed = false;
        var subs = Subcategories.Select(sub => sub with
        {
            Scenarios = sub.Scenarios.Select(s =>
            {
                if (localBest(s.Scenario) is not { } b || b <= s.Score) return s;
                changed = true;
                return s with { Score = b, Rank = s.RankMaxes.Count == 0 ? s.Rank : Math.Max(s.Rank, Tier.TierOf(b, s.RankMaxes).Rank) };
            }).ToList(),
        }).ToList();
        if (!changed) return this;

        var ranked = subs.SelectMany(s => s.Scenarios).Where(s => s.RankMaxes.Count > 0).ToList();
        var top = Math.Max(RankNames.Count - 1, 0);
        var local = ranked.Count == 0 ? 0 : Math.Min((int)Math.Floor(ranked.Average(s => Tier.ValueOf(s.Score, s.RankMaxes)) + 1e-9), top);
        return this with { Subcategories = subs, OverallRank = Math.Max(OverallRank, local) };
    }

    /// <summary>
    /// Parses KovaaK's player-progress-rank-benchmark JSON. Subcategories come in the same order as the benchmark site
    /// definition, which is how they get their parent category.
    /// </summary>
    public static BenchmarkProgress Parse(string json, Difficulty? definition = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var err)) throw new InvalidOperationException(err.GetString());

        var parents = definition?.Categories.SelectMany(c => c.Subcategories.Select(_ => c.CategoryName)).ToList() ?? [];
        var cats = root.GetProperty("categories");
        var subs = new List<SubcategoryProgress>();
        var i = 0;
        foreach (var c in cats.EnumerateObject())
        {
            var scenarios = c.Value.GetProperty("scenarios").EnumerateObject().Select(s => new ScenarioProgress(
                s.Name,
                s.Value.GetProperty("score").GetDouble() / 100.0,
                s.Value.GetProperty("scenario_rank").GetInt32(),
                Doubles(s.Value, "rank_maxes"),
                s.Value.TryGetProperty("leaderboard_id", out var lid) ? lid.GetInt64() : 0,
                s.Value.TryGetProperty("leaderboard_rank", out var lr) && lr.ValueKind == JsonValueKind.Number ? lr.GetInt32() : null)).ToList();
            subs.Add(new SubcategoryProgress(
                parents.Count == cats.EnumerateObject().Count() ? parents[i] : "",
                c.Name.Trim(),
                c.Value.GetProperty("benchmark_progress").GetDouble(),
                c.Value.GetProperty("category_rank").GetInt32(),
                Doubles(c.Value, "rank_maxes"), scenarios));
            i++;
        }

        var ranks = root.TryGetProperty("ranks", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetProperty("name").GetString()!.Trim()).ToList()
            : [];
        return new BenchmarkProgress(root.GetProperty("benchmark_progress").GetDouble(), root.GetProperty("overall_rank").GetInt32(), ranks, subs);
    }

    static List<double> Doubles(JsonElement e, string key) =>
        e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetDouble()).ToList() : [];
}

/// <summary>Reads a player's benchmark progress from KovaaK's public web API (no login needed).</summary>
public sealed class KovaaksApi(HttpClient? http = null)
{
    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<BenchmarkProgress> GetProgressAsync(Difficulty difficulty, string steamId, CancellationToken ct = default)
    {
        var url = $"https://kovaaks.com/webapp-backend/benchmarks/player-progress-rank-benchmark?benchmarkId={difficulty.KovaaksBenchmarkId}&steamId={Uri.EscapeDataString(steamId)}&page=0&max=100";
        return BenchmarkProgress.Parse(await _http.GetStringAsync(url, ct), difficulty);
    }

    /// <summary>Raw player-progress JSON (for caching); parse with <see cref="BenchmarkProgress.Parse"/>.</summary>
    public Task<string> GetProgressJsonAsync(Difficulty difficulty, string steamId, CancellationToken ct = default) =>
        _http.GetStringAsync($"https://kovaaks.com/webapp-backend/benchmarks/player-progress-rank-benchmark?benchmarkId={difficulty.KovaaksBenchmarkId}&steamId={Uri.EscapeDataString(steamId)}&page=0&max=100", ct);

    /// <summary>
    /// Resolves the player's webapp username from their own leaderboard entry on a scenario that has a rank.
    /// Null when no scenario qualifies or the entry is not theirs.
    /// </summary>
    public async Task<string?> ResolveUsernameAsync(string steamId, IEnumerable<ScenarioProgress> scenarios, CancellationToken ct = default)
    {
        foreach (var s in scenarios.Where(s => s.LeaderboardId > 0 && s.LeaderboardRank is > 0).Take(3))
        {
            var json = await _http.GetStringAsync($"https://kovaaks.com/webapp-backend/leaderboard/scores/global?leaderboardId={s.LeaderboardId}&page={s.LeaderboardRank!.Value - 1}&max=1", ct);
            if (CloudScores.ParseEntry(json, steamId) is { } e) return e.Username;
        }
        return null;
    }

    /// <summary>Last ~10 server-side runs of a scenario for the user, oldest first.</summary>
    public async Task<List<CloudScore>> GetLastScoresAsync(string username, string scenario, CancellationToken ct = default) =>
        CloudScores.ParseLastScores(await _http.GetStringAsync(
            $"https://kovaaks.com/webapp-backend/user/scenario/last-scores/by-name?username={Uri.EscapeDataString(username)}&scenarioName={Uri.EscapeDataString(scenario)}", ct));

    /// <summary>The higher of the local and server scores; a zero server score counts as none.</summary>
    public static double? Reconcile(double? local, double server) =>
        server > 0 && (local == null || server > local.Value) ? server : local;
}
