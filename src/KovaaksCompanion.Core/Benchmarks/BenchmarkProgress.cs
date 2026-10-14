using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>Score in the same unit as the local stats CSV (the server sends hundredths).</summary>
public sealed record ScenarioProgress(string Scenario, double Score, int Rank, IReadOnlyList<double> RankMaxes, long LeaderboardId, int? LeaderboardRank);

/// <summary><see cref="Parent"/> is the benchmark category the subcategory belongs to; empty when it cannot be matched.</summary>
public sealed record SubcategoryProgress(string Parent, string Name, double Progress, int Rank, IReadOnlyList<double> RankMaxes, IReadOnlyList<ScenarioProgress> Scenarios);

public sealed record BenchmarkProgress(double Progress, int OverallRank, IReadOnlyList<string> RankNames, IReadOnlyList<SubcategoryProgress> Subcategories)
{
    public RankSetup? Setup { get; init; }
    /// <summary>Progress (0..1) toward the next overall tier, from the playlist's rank calculation.</summary>
    public double OverallProgress { get; init; }
    public string RankName(int rank) => rank >= 0 && rank < RankNames.Count ? RankNames[rank] : "";
    /// <summary>Empty while unranked, so the API's own label for tier 0 ("No Rank") never reaches the UI.</summary>
    public string OverallRankName => OverallRank > 0 ? RankName(OverallRank) : "";
    public IEnumerable<ScenarioProgress> Scenarios => Subcategories.SelectMany(s => s.Scenarios);

    /// <summary>evxl's "Complete": every scenario is at or above the tier (lowest scenario rank), independent of the playlist's overall rank calculation.</summary>
    public bool IsTierComplete(int tier)
    {
        var scenarios = Scenarios.ToList();
        return tier > 0 && tier <= Math.Max(RankNames.Count - 1, 0) && scenarios.Count > 0 && scenarios.All(s => s.Rank >= tier);
    }

    /// <summary>Progress to the next overall tier using the playlist's evxl rank calculation.</summary>
    public double OverallFraction => OverallRank >= Math.Max(RankNames.Count - 1, 0) ? 1 : OverallProgress;

    /// <summary>Applies the playlist's evxl rank calculation (higher of the lowest scenario tier and the custom method).</summary>
    BenchmarkProgress Recalculated(IReadOnlyList<SubcategoryProgress> subs)
    {
        if (RankNames.Count == 0) return this with { Subcategories = subs };
        var setup = Setup ?? new RankSetup("basic", "", RankNames.Skip(1).ToList(), [], null);
        var r = RankCalculationEngine.Compute(setup, RankNames, subs);
        return this with { Subcategories = subs, OverallRank = r.Rank, OverallProgress = r.Progress };
    }

    /// <summary>
    /// Raises scenario scores (and their tiers) to a higher local best, then applies this playlist's rank calculation.
    /// Returns this when no local best beats the server.
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

        return Recalculated(subs);
    }

    /// <summary>
    /// Parses KovaaK's player-progress-rank-benchmark JSON. Subcategories come in the same order as the benchmark
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
        var catCount = cats.EnumerateObject().Count();
        var i = 0;
        foreach (var c in cats.EnumerateObject())
        {
            var scenarios = c.Value.GetProperty("scenarios").EnumerateObject().Select(s => new ScenarioProgress(
                s.Name,
                Dbl(s.Value.GetProperty("score")) / 100.0,
                (int)Dbl(s.Value.GetProperty("scenario_rank")),
                Doubles(s.Value, "rank_maxes"),
                s.Value.TryGetProperty("leaderboard_id", out var lid) ? (long)Dbl(lid) : 0,
                s.Value.TryGetProperty("leaderboard_rank", out var lr) && lr.ValueKind is JsonValueKind.Number or JsonValueKind.String ? (int)Dbl(lr) : null)).ToList();
            AddCategory(subs, definition, parents, catCount, i++, c.Name.Trim(), Dbl(c.Value.GetProperty("benchmark_progress")),
                (int)Dbl(c.Value.GetProperty("category_rank")), Doubles(c.Value, "rank_maxes"), scenarios);
        }

        var ranks = root.TryGetProperty("ranks", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(x => x.GetProperty("name").GetString()!.Trim()).ToList()
            : [];
        var parsed = new BenchmarkProgress(Dbl(root.GetProperty("benchmark_progress")), (int)Dbl(root.GetProperty("overall_rank")), ranks, subs)
        {
            Setup = definition?.Setup,
        };
        return definition == null ? parsed : parsed.Recalculated(subs);
    }

    /// <summary>
    /// Adds a category as subcategories. Some benchmarks (Voltaic) report whole categories; those are split into the definition's
    /// subcategories in scenario order.
    /// </summary>
    static void AddCategory(List<SubcategoryProgress> subs, Difficulty? definition, List<string> parents, int catCount, int index, string name,
        double progress, int catRank, IReadOnlyList<double> maxes, List<ScenarioProgress> scenarios)
    {
        var def = definition is { } dd && parents.Count != catCount && catCount == dd.Categories.Count ? dd.Categories.FirstOrDefault(x => x.CategoryName.Trim() == name) : null;
        if (def is { Subcategories.Count: > 1 } && def.Subcategories.Sum(x => x.ScenarioCount) == scenarios.Count)
        {
            var offset = 0;
            foreach (var part in def.Subcategories)
            {
                var share = (double)part.ScenarioCount / scenarios.Count;
                subs.Add(new SubcategoryProgress(def.CategoryName, part.SubcategoryName.Trim(), progress * share, catRank,
                    maxes.Select(x => x * share).ToList(), scenarios.Skip(offset).Take(part.ScenarioCount).ToList()));
                offset += part.ScenarioCount;
            }
        }
        else
            subs.Add(new SubcategoryProgress(parents.Count == catCount ? parents[index] : "", name, progress, catRank, maxes, scenarios));
    }

    /// <summary>
    /// Builds a difficulty's progress from the bundled scenario list and thresholds (<see cref="RankData"/>) and the given scores;
    /// tiers and the overall rank are calculated locally. Null when the difficulty is not in the bundle.
    /// </summary>
    public static BenchmarkProgress? FromBundle(Difficulty definition, Func<string, double?> score)
    {
        if (RankData.Get(definition.KovaaksBenchmarkId) is not { } data) return null;
        var parents = definition.Categories.SelectMany(c => c.Subcategories.Select(_ => c.CategoryName)).ToList();
        var subs = new List<SubcategoryProgress>();
        var i = 0;
        foreach (var c in data.Categories)
        {
            var scenarios = c.Scenarios.Select(s =>
            {
                var best = score(s.Name) ?? 0;
                var rank = best > 0 && s.RankMaxes.Count > 0 ? Tier.TierOf(best, s.RankMaxes).Rank : 0;
                return new ScenarioProgress(s.Name, best, rank, s.RankMaxes, s.LeaderboardId, null);
            }).ToList();
            AddCategory(subs, definition, parents, data.Categories.Count, i++, c.Name, 0, 0, [], scenarios);
        }
        return new BenchmarkProgress(0, 0, data.RankNames, subs) { Setup = definition.Setup }.Recalculated(subs);
    }

    static List<double> Doubles(JsonElement e, string key) =>
        e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(Dbl).ToList() : [];

    /// <summary>KovaaK's sometimes sends numbers as strings (or null); read both.</summary>
    static double Dbl(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => 0,
    };
}

/// <summary>A player's best score and number of plays of one scenario on the server.</summary>
public sealed record ScenarioTotals(double Score, int Plays);

/// <summary>Reads a player's server data from KovaaK's public web API (no login needed).</summary>
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

    /// <summary>
    /// Resolves the username by looking up the player's progress on the given difficulties (most likely played first) until one
    /// has a leaderboard entry of theirs. Gives up after <paramref name="maxTries"/> requests.
    /// </summary>
    public async Task<string?> ResolveUsernameAsync(string steamId, IEnumerable<Difficulty> difficulties, int maxTries = 6, CancellationToken ct = default)
    {
        foreach (var d in difficulties.Take(maxTries))
        {
            try
            {
                var p = BenchmarkProgress.Parse(await GetProgressJsonAsync(d, steamId, ct));
                if (await ResolveUsernameAsync(steamId, p.Scenarios, ct) is { } name) return name;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException) { }
        }
        return null;
    }

    /// <summary>The player's best score and play count of every scenario they have played (a few paged requests).</summary>
    public async Task<Dictionary<string, ScenarioTotals>> GetTotalPlaysAsync(string username, CancellationToken ct = default)
    {
        var res = new Dictionary<string, ScenarioTotals>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < 20; page++)
        {
            var json = await _http.GetStringAsync(
                $"https://kovaaks.com/webapp-backend/user/scenario/total-play?username={Uri.EscapeDataString(username)}&page={page}&max=100", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) break;
            foreach (var e in data.EnumerateArray())
            {
                if (!e.TryGetProperty("scenarioName", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                var score = e.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var plays = e.TryGetProperty("counts", out var c) && c.TryGetProperty("plays", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
                res[name] = new ScenarioTotals(score, plays);
            }
            if (data.GetArrayLength() < 100) break;
        }
        return res;
    }

    /// <summary>Last ~10 server-side runs of a scenario for the user, oldest first.</summary>
    public async Task<List<CloudScore>> GetLastScoresAsync(string username, string scenario, CancellationToken ct = default) =>
        CloudScores.ParseLastScores(await _http.GetStringAsync(
            $"https://kovaaks.com/webapp-backend/user/scenario/last-scores/by-name?username={Uri.EscapeDataString(username)}&scenarioName={Uri.EscapeDataString(scenario)}", ct));

    /// <summary>The higher of the local and server scores; a zero server score counts as none.</summary>
    public static double? Reconcile(double? local, double server) =>
        server > 0 && (local == null || server > local.Value) ? server : local;

}
