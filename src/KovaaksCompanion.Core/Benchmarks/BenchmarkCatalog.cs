using System.Reflection;
using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>One benchmark group (e.g. "Voltaic S5") with its difficulties. Each difficulty is a KovaaK's playlist.</summary>
public sealed record Benchmark(string BenchmarkName, string Abbreviation, string Color, string SpreadsheetUrl, string DateAdded, IReadOnlyList<Difficulty> Difficulties);

public sealed record Difficulty(string DifficultyName, int KovaaksBenchmarkId, string Sharecode, IReadOnlyDictionary<string, string> RankColors, IReadOnlyList<BenchmarkCategory> Categories)
{
    public int ScenarioCount => Categories.Sum(c => c.Subcategories.Sum(s => s.ScenarioCount));
}

public sealed record BenchmarkCategory(string CategoryName, string Color, IReadOnlyList<Subcategory> Subcategories);

/// <summary>Subcategory name is empty when the category has no subdivision.</summary>
public sealed record Subcategory(string SubcategoryName, int ScenarioCount, string Color);

/// <summary>
/// Snapshot of the benchmark site's benchmark list (playlists and their categories), embedded in the assembly.
/// Refresh with scripts/update-benchmarks.mjs.
/// </summary>
public static class BenchmarkCatalog
{
    static readonly Lazy<IReadOnlyList<Benchmark>> Embedded = new(() =>
    {
        using var s = typeof(BenchmarkCatalog).Assembly.GetManifestResourceStream("benchmarks.json")
            ?? throw new InvalidOperationException("benchmarks.json is not embedded");
        return Parse(new StreamReader(s).ReadToEnd());
    });

    public static IReadOnlyList<Benchmark> All => Embedded.Value;

    public static IReadOnlyList<Benchmark> Parse(string json)
    {
        var raw = JsonSerializer.Deserialize<List<RawBenchmark>>(json, JsonOptions) ?? [];
        return raw.Select(b => new Benchmark(
            b.BenchmarkName ?? "", b.Abbreviation ?? "", b.Color ?? "", b.SpreadsheetURL ?? "", b.DateAdded ?? "",
            (b.Difficulties ?? []).Select(d => new Difficulty(
                d.DifficultyName ?? "", d.KovaaksBenchmarkId, d.Sharecode ?? "",
                d.RankColors ?? new Dictionary<string, string>(),
                (d.Categories ?? []).Select(c => new BenchmarkCategory(
                    c.CategoryName ?? "", c.Color ?? "",
                    (c.Subcategories ?? []).Select(s => new Subcategory(s.SubcategoryName ?? "", s.ScenarioCount, s.Color ?? "")).ToList())).ToList()
            )).ToList())).ToList();
    }

    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    sealed record RawBenchmark(string? BenchmarkName, string? Abbreviation, string? Color, string? SpreadsheetURL, string? DateAdded, List<RawDifficulty>? Difficulties);
    sealed record RawDifficulty(string? DifficultyName, int KovaaksBenchmarkId, string? Sharecode, Dictionary<string, string>? RankColors, List<RawCategory>? Categories);
    sealed record RawCategory(string? CategoryName, string? Color, List<RawSub>? Subcategories);
    sealed record RawSub(string? SubcategoryName, int ScenarioCount, string? Color);
}
