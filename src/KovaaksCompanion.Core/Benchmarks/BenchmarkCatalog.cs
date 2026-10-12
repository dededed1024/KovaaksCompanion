using System.Reflection;
using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>One benchmark group (e.g. "Voltaic S5") with its difficulties. Each difficulty is a KovaaK's playlist.</summary>
public sealed record Benchmark(string BenchmarkName, string Abbreviation, string Color, IReadOnlyList<Difficulty> Difficulties, string RankCalculation = "basic");

public sealed record Difficulty(string DifficultyName, int KovaaksBenchmarkId, string Sharecode, IReadOnlyDictionary<string, string> RankColors, IReadOnlyList<BenchmarkCategory> Categories, RankSetup? Setup = null)
{
    public string RankCalculation => Setup?.Method ?? "basic";
    public int ScenarioCount => Categories.Sum(c => c.Subcategories.Sum(s => s.ScenarioCount));
}

/// <summary>Tier names of one difficulty; evxl derives energy thresholds from the tier counts of the sibling difficulties.</summary>
public sealed record SiblingTiers(string Name, IReadOnlyList<string> Tiers);

public sealed record ScenarioSelection(int BaseRankScoreCount, int? FullPoolRankScoreCount, int MinPerCategory, int MinPerSubcategory);

/// <summary>Everything evxl's rank calculation reads from the playlist definition.</summary>
public sealed record RankSetup(string Method, string Difficulty, IReadOnlyList<string> Tiers, IReadOnlyList<SiblingTiers> Siblings, ScenarioSelection? Selection);

public sealed record BenchmarkCategory(string CategoryName, string Color, IReadOnlyList<Subcategory> Subcategories);

/// <summary>Subcategory name is empty when the category has no subdivision.</summary>
public sealed record Subcategory(string SubcategoryName, int ScenarioCount, string Color);

/// <summary>
/// The playlist list (benchmarks, difficulties, categories, tier colours), embedded in the assembly as playlists.json.
/// Regenerate it from a source list with scripts/convert-benchmarks.mjs.
/// </summary>
public static class BenchmarkCatalog
{
    static readonly Lazy<IReadOnlyList<Benchmark>> Embedded = new(() =>
    {
        using var s = typeof(BenchmarkCatalog).Assembly.GetManifestResourceStream("playlists.json")
            ?? throw new InvalidOperationException("playlists.json is not embedded");
        return Parse(new StreamReader(s).ReadToEnd());
    });

    public static IReadOnlyList<Benchmark> All => Embedded.Value;

    public static IReadOnlyList<Benchmark> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<RawFile>(json, JsonOptions);
        return (file?.Playlists ?? []).Select(p =>
        {
            var method = p.RankCalculation ?? "basic";
            var levels = p.Levels ?? [];
            var siblings = levels.Select(l => new SiblingTiers(l.Level ?? "", (l.TierColors?.Keys ?? (IEnumerable<string>)[]).ToList())).ToList();
            return new Benchmark(
                p.Name ?? "", p.Abbr ?? "", p.Color ?? "",
                levels.Select(l => new Difficulty(
                    l.Level ?? "", l.Id, l.Code ?? "",
                    l.TierColors ?? new Dictionary<string, string>(),
                    (l.Groups ?? []).Select(g => new BenchmarkCategory(
                        g.Name ?? "", g.Color ?? "",
                        (g.Parts ?? []).Select(x => new Subcategory(x.Name ?? "", x.Count, x.Color ?? "")).ToList())).ToList(),
                    new RankSetup(method, l.Level ?? "", (l.TierColors?.Keys ?? (IEnumerable<string>)[]).ToList(), siblings,
                        l.Selection is { } sel ? new ScenarioSelection(sel.BaseRankScoreCount, sel.FullPoolRankScoreCount, sel.MinPerCategory, sel.MinPerSubcategory) : null)
                )).ToList(), method);
        }).ToList();
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    sealed record RawFile(int Version, List<RawPlaylist>? Playlists);
    sealed record RawPlaylist(string? Name, string? Abbr, string? Color, List<RawLevel>? Levels, string? RankCalculation);
    sealed record RawLevel(string? Level, int Id, string? Code, Dictionary<string, string>? TierColors, List<RawGroup>? Groups, RawSelection? Selection);
    sealed record RawSelection(int BaseRankScoreCount, int? FullPoolRankScoreCount, int MinPerCategory, int MinPerSubcategory);
    sealed record RawGroup(string? Name, string? Color, List<RawPart>? Parts);
    sealed record RawPart(string? Name, int Count, string? Color);
}
