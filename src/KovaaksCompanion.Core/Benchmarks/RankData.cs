using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

public sealed record BundledScenario(string Name, long LeaderboardId, IReadOnlyList<double> RankMaxes);

public sealed record BundledCategory(string Name, IReadOnlyList<BundledScenario> Scenarios);

/// <summary>One difficulty's tier names (index 0 is the unranked label) and its scenarios with their tier thresholds.</summary>
public sealed record BundledDifficulty(IReadOnlyList<string> RankNames, IReadOnlyList<BundledCategory> Categories);

/// <summary>
/// Scenario lists and tier thresholds (rank_maxes) of every benchmark difficulty, embedded as rank-data.json so tiers need no server
/// round trip. Regenerate it with scripts/bundle-rank-data.mjs.
/// </summary>
public static class RankData
{
    static readonly Lazy<IReadOnlyDictionary<int, BundledDifficulty>> Embedded = new(() =>
    {
        using var s = typeof(RankData).Assembly.GetManifestResourceStream("rank-data.json")
            ?? throw new InvalidOperationException("rank-data.json is not embedded");
        return Parse(new StreamReader(s).ReadToEnd());
    });

    public static BundledDifficulty? Get(int benchmarkId) => Embedded.Value.GetValueOrDefault(benchmarkId);

    public static IReadOnlyDictionary<int, BundledDifficulty> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = new Dictionary<int, BundledDifficulty>();
        foreach (var d in doc.RootElement.GetProperty("difficulties").EnumerateObject())
        {
            if (!int.TryParse(d.Name, out var id)) continue;
            var ranks = d.Value.GetProperty("r").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
            var cats = d.Value.GetProperty("c").EnumerateArray().Select(c => new BundledCategory(
                c.GetProperty("n").GetString() ?? "",
                c.GetProperty("s").EnumerateArray().Select(s => new BundledScenario(
                    s[0].GetString() ?? "", s[1].GetInt64(), s[2].EnumerateArray().Select(x => x.GetDouble()).ToList())).ToList())).ToList();
            res[id] = new BundledDifficulty(ranks, cats);
        }
        return res;
    }
}
