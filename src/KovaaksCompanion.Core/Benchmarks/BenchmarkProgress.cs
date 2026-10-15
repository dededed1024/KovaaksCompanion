using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>Score in the same unit as the local stats CSV.</summary>
public sealed record ScenarioProgress(string Scenario, double Score, int Tier, IReadOnlyList<double> TierThresholds);

/// <summary><see cref="Parent"/> is the benchmark category the subcategory belongs to; empty when it cannot be matched.</summary>
public sealed record SubcategoryProgress(string Parent, string Name, IReadOnlyList<ScenarioProgress> Scenarios);

public sealed record BenchmarkProgress(int OverallTier, IReadOnlyList<string> TierNames, IReadOnlyList<SubcategoryProgress> Subcategories)
{
    public TierSetup? Setup { get; init; }
    /// <summary>Progress (0..1) toward the next overall tier, from the playlist's rank calculation.</summary>
    public double OverallProgress { get; init; }
    public string TierName(int rank) => rank >= 0 && rank < TierNames.Count ? TierNames[rank] : "";
    /// <summary>Empty while unranked, so the API's own label for tier 0 ("No Tier") never reaches the UI.</summary>
    public string OverallTierName => OverallTier > 0 ? TierName(OverallTier) : "";
    public IEnumerable<ScenarioProgress> Scenarios => Subcategories.SelectMany(s => s.Scenarios);

    /// <summary>evxl's "Complete": every scenario is at or above the tier (lowest scenario rank), independent of the playlist's overall rank calculation.</summary>
    public bool IsTierComplete(int tier)
    {
        var scenarios = Scenarios.ToList();
        return tier > 0 && tier <= Math.Max(TierNames.Count - 1, 0) && scenarios.Count > 0 && scenarios.All(s => s.Tier >= tier);
    }

    /// <summary>Progress to the next overall tier using the playlist's evxl rank calculation.</summary>
    public double OverallFraction => OverallTier >= Math.Max(TierNames.Count - 1, 0) ? 1 : OverallProgress;

    /// <summary>Applies the playlist's evxl rank calculation (higher of the lowest scenario tier and the custom method).</summary>
    BenchmarkProgress Recalculated(IReadOnlyList<SubcategoryProgress> subs)
    {
        if (TierNames.Count == 0) return this with { Subcategories = subs };
        var setup = Setup ?? new TierSetup("basic", "", TierNames.Skip(1).ToList(), [], null);
        var r = TierCalculationEngine.Compute(setup, TierNames, subs);
        return this with { Subcategories = subs, OverallTier = r.Tier, OverallProgress = r.Progress };
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
                return s with { Score = b, Tier = s.TierThresholds.Count == 0 ? s.Tier : Math.Max(s.Tier, TierLadder.TierOf(b, s.TierThresholds).Tier) };
            }).ToList(),
        }).ToList();
        if (!changed) return this;

        return Recalculated(subs);
    }

    /// <summary>
    /// Adds a category as subcategories. Some benchmarks (Voltaic) report whole categories; those are split into the definition's
    /// subcategories in scenario order.
    /// </summary>
    static void AddCategory(List<SubcategoryProgress> subs, Difficulty? definition, List<string> parents, int catCount, int index, string name, List<ScenarioProgress> scenarios)
    {
        var def = definition is { } dd && parents.Count != catCount && catCount == dd.Categories.Count ? dd.Categories.FirstOrDefault(x => x.CategoryName.Trim() == name) : null;
        if (def is { Subcategories.Count: > 1 } && def.Subcategories.Sum(x => x.ScenarioCount) == scenarios.Count)
        {
            var offset = 0;
            foreach (var part in def.Subcategories)
            {
                subs.Add(new SubcategoryProgress(def.CategoryName, part.SubcategoryName.Trim(), scenarios.Skip(offset).Take(part.ScenarioCount).ToList()));
                offset += part.ScenarioCount;
            }
        }
        else
            subs.Add(new SubcategoryProgress(parents.Count == catCount ? parents[index] : "", name, scenarios));
    }

    /// <summary>
    /// Builds a difficulty's progress from the bundled scenario list and thresholds (<see cref="TierData"/>) and the given scores;
    /// tiers and the overall rank are calculated locally. Null when the difficulty is not in the bundle.
    /// </summary>
    public static BenchmarkProgress? FromBundle(Difficulty definition, Func<string, double?> score)
    {
        if (TierData.Get(definition.KovaaksBenchmarkId) is not { } data) return null;
        var parents = definition.Categories.SelectMany(c => c.Subcategories.Select(_ => c.CategoryName)).ToList();
        var subs = new List<SubcategoryProgress>();
        var i = 0;
        foreach (var c in data.Categories)
        {
            var scenarios = c.Scenarios.Select(s =>
            {
                var best = score(s.Name) ?? 0;
                var rank = best > 0 && s.TierThresholds.Count > 0 ? TierLadder.TierOf(best, s.TierThresholds).Tier : 0;
                return new ScenarioProgress(s.Name, best, rank, s.TierThresholds);
            }).ToList();
            AddCategory(subs, definition, parents, data.Categories.Count, i++, c.Name, scenarios);
        }
        return new BenchmarkProgress(0, data.TierNames, subs) { Setup = definition.Setup }.Recalculated(subs);
    }
}

/// <summary>Reads a player's server data from KovaaK's public web API (no login needed).</summary>
public sealed class KovaaksApi(HttpClient? http = null)
{
    readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The player's webapp username, read from their own leaderboard entry: the benchmark progress of <paramref name="steamId"/> names a scenario's leaderboard and the
    /// player's rank in it, and that entry carries the username. Tries the given benchmarks (most likely played first) until one works; null when none does.
    /// </summary>
    public async Task<string?> ResolveUsernameAsync(string steamId, IEnumerable<int> benchmarkIds, int maxTries = 6, CancellationToken ct = default)
    {
        foreach (var id in benchmarkIds.Take(maxTries))
        {
            try
            {
                using var progress = JsonDocument.Parse(await _http.GetStringAsync(
                    $"https://kovaaks.com/webapp-backend/benchmarks/player-progress-rank-benchmark?benchmarkId={id}&steamId={Uri.EscapeDataString(steamId)}&page=0&max=100", ct));
                if (!progress.RootElement.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Object) continue;
                var tried = 0;
                foreach (var scenario in cats.EnumerateObject().SelectMany(c => c.Value.TryGetProperty("scenarios", out var s) && s.ValueKind == JsonValueKind.Object ? s.EnumerateObject() : []))
                {
                    var v = scenario.Value;
                    if (!v.TryGetProperty("leaderboard_id", out var lb) || !lb.TryGetInt32(out var lbId) || lbId <= 0 ||
                        !v.TryGetProperty("leaderboard_rank", out var rk) || !rk.TryGetInt32(out var rank) || rank <= 0) continue;
                    using var board = JsonDocument.Parse(await _http.GetStringAsync(
                        $"https://kovaaks.com/webapp-backend/leaderboard/scores/global?leaderboardId={lbId}&page={rank - 1}&max=1", ct));
                    if (board.RootElement.ValueKind == JsonValueKind.Object && board.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        foreach (var e in data.EnumerateArray())
                            if (e.TryGetProperty("steamId", out var sid) && sid.ToString() == steamId && e.TryGetProperty("webappUsername", out var name) && name.GetString() is { Length: > 0 } user)
                                return user;
                    if (++tried == 3) break; // the entry moved (a rank changed meanwhile): another benchmark is a better bet than more scenarios of this one
                }
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException or TaskCanceledException && !ct.IsCancellationRequested) { }
        }
        return null;
    }

    /// <summary>The player's best score of every scenario they have played (a few paged requests).</summary>
    public async Task<Dictionary<string, double>> GetBestScoresAsync(string username, CancellationToken ct = default)
    {
        var res = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var page = 0; page < 20; page++)
        {
            var json = await _http.GetStringAsync(
                $"https://kovaaks.com/webapp-backend/user/scenario/total-play?username={Uri.EscapeDataString(username)}&page={page}&max=100", ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0) break;
            foreach (var e in data.EnumerateArray())
            {
                if (!e.TryGetProperty("scenarioName", out var n) || n.GetString() is not { Length: > 0 } name) continue;
                if (e.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number && s.GetDouble() is > 0 and var score) res[name] = score;
            }
            if (data.GetArrayLength() < 100) break;
        }
        return res;
    }

    /// <summary>Last ~10 server-side runs of a scenario for the user, oldest first.</summary>
    public async Task<List<CloudScore>> GetLastScoresAsync(string username, string scenario, CancellationToken ct = default) =>
        CloudScores.ParseLastScores(await _http.GetStringAsync(
            $"https://kovaaks.com/webapp-backend/user/scenario/last-scores/by-name?username={Uri.EscapeDataString(username)}&scenarioName={Uri.EscapeDataString(scenario)}", ct));

    /// <summary>The higher of the local and cloud scores; a zero cloud score counts as none.</summary>
    public static double? Reconcile(double? local, double server) =>
        server > 0 && (local == null || server > local.Value) ? server : local;

}
