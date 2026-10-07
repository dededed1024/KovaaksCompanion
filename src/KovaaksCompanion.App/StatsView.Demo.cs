using System.IO;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>Filming build (<see cref="App.Demo"/>): Home opens on fixed fake data instead of the player's stats folder and the server.</summary>
public partial class StatsView
{
    const int DemoDays = 692, DemoStreak = 47;
    static readonly int[] DemoFavorites = [2338, 460]; // Viscose S2 Expert (Ascension), Voltaic S5 Advanced (Astra)
    static readonly int[] DemoComplete = [459, 458, 237, 265, 2335, 2336, 2337, 823, 2835];

    static readonly string DemoIndexFile = Path.Combine(Path.GetTempPath(), "KovaaksCompanionDemo", "playlist-scenarios.dat");

    sealed record DemoScenario(string Name, string Category, string Part, double[] Maxes);

    internal static UiState DemoUi()
    {
        var ui = new UiState();
        foreach (var id in DemoFavorites) ui.ToggleFavorite($"{id}");
        return ui;
    }

    /// <summary>Score that sits at tier position <paramref name="v"/> (rank + fraction) of <paramref name="maxes"/>.</summary>
    static double DemoScoreAt(double[] maxes, double v)
    {
        var r = Math.Min((int)v, maxes.Length - 1);
        var f = Math.Min(v - r, 1);
        var prev = r == 0 ? 0 : maxes[r - 1];
        return prev + (maxes[r] - prev) * f;
    }

    List<DemoScenario> DemoScenarios(Difficulty d, string prefix, Random rng)
    {
        var tiers = d.RankColors.Count - 1;
        var list = new List<DemoScenario>();
        foreach (var c in d.Categories)
            foreach (var part in c.Subcategories)
                for (var i = 1; i <= part.ScenarioCount; i++)
                {
                    var b = Math.Round(900 + rng.NextDouble() * 2300);
                    list.Add(new($"{prefix} {c.CategoryName} {part.SubcategoryName.Trim()} {i}", c.CategoryName, part.SubcategoryName.Trim(), Enumerable.Range(0, tiers).Select(k => Math.Round(b * (0.6 + 0.16 * k))).ToArray()));
                }
        return list;
    }

    /// <summary>Days with runs: the last <see cref="DemoStreak"/> days up to yesterday without a break, then streaks of at most 11 days with 1-3 day breaks, <see cref="DemoDays"/> in all.</summary>
    static List<DateTime> DemoPlayDays(Random rng)
    {
        var last = DateTime.Today.AddDays(-1);
        var days = Enumerable.Range(0, DemoStreak).Select(i => last.AddDays(-i)).ToList();
        var cursor = last.AddDays(-DemoStreak - 1); // two-day break after the streak
        while (days.Count < DemoDays)
        {
            for (var k = rng.Next(2, 12); k > 0 && days.Count < DemoDays; k--) days.Add(cursor = cursor.AddDays(-1));
            cursor = cursor.AddDays(-rng.Next(1, 4));
        }
        days.Sort();
        return days;
    }

    void LoadDemo()
    {
        var rng = new Random(20260508);
        var all = AllDifficulties().ToDictionary(x => x.D.KovaaksBenchmarkId);
        var viscose = all[DemoFavorites[0]].D;
        var voltaic = all[DemoFavorites[1]].D;
        var vScn = DemoScenarios(viscose, "VSC", rng);
        var tScn = DemoScenarios(voltaic, "VT", rng);
        var scenarios = vScn.Concat(tScn).ToList();
        var final = scenarios.ToDictionary(s => s.Name, s =>
        {
            var tiers = s.Maxes.Length;
            var rank = tiers == 5 ? 4 : 2; // Ascension of 6 tiers, Astra of 4
            return DemoScoreAt(s.Maxes, Math.Min(rank + rng.NextDouble() * 1.1 - 0.15, tiers - 0.02));
        });

        var days = DemoPlayDays(rng);
        var runs = new List<RunRecord>();
        var best = new Dictionary<string, double>();
        foreach (var day in days)
        {
            var t = (day - days[0]).TotalDays / (days[^1] - days[0]).TotalDays;
            var q = 0.5 + 0.5 * (1 - Math.Exp(-3 * t)) / (1 - Math.Exp(-3));
            var pick = day == days[^1]
                ? tScn.OrderBy(_ => rng.Next()).Take(2).Concat(vScn.OrderBy(_ => rng.Next()).Take(2)).ToList()
                : scenarios.OrderBy(_ => rng.Next()).Take(rng.Next(3, 6)).ToList();
            var sessionStart = day.AddHours(12 + rng.NextDouble() * 6);
            var cursor = sessionStart;
            var second = pick.Count > 3 && rng.NextDouble() < 0.3 ? pick.Count / 2 : -1;
            for (var i = 0; i < pick.Count; i++)
            {
                if (i == second) cursor = cursor.AddHours(1.5 + rng.NextDouble() * 1.5);
                var s = pick[i];
                for (var n = rng.Next(6, 13); n > 0; n--)
                {
                    var score = Math.Round(final[s.Name] * q * (1 + (rng.NextDouble() - 0.5) * 0.08), 1);
                    var end = cursor.AddSeconds(60);
                    runs.Add(new(s.Name, cursor, end, score, 0.45 + 0.3 * q + (rng.NextDouble() - 0.5) * 0.08, rng.Next(20, 60)));
                    best[s.Name] = Math.Max(best.GetValueOrDefault(s.Name), score);
                    cursor = end.AddSeconds(10 + rng.Next(25));
                }
            }
        }
        foreach (var s in scenarios) if (!best.ContainsKey(s.Name)) best[s.Name] = final[s.Name];

        _lib = new RunLibrary(runs);
        _sessions = [];
        _rawProgress.Clear();
        _progress.Clear();
        void Put(Difficulty d, BenchmarkProgress p) { _rawProgress[d.KovaaksBenchmarkId] = p; _progress[d.KovaaksBenchmarkId] = p; }
        Put(viscose, DemoProgress(viscose, 4, vScn, best));
        Put(voltaic, DemoProgress(voltaic, 2, tScn, best));
        _index.Set(viscose.KovaaksBenchmarkId, vScn.Select(s => s.Name));
        _index.Set(voltaic.KovaaksBenchmarkId, tScn.Select(s => s.Name));
        foreach (var id in DemoComplete)
        {
            var d = all[id].D;
            var names = d.RankColors.Keys.ToList();
            Put(d, new BenchmarkProgress(100, names.Count - 1, names, []));
        }
        ShowHome();
    }

    static BenchmarkProgress DemoProgress(Difficulty d, int rank, List<DemoScenario> scenarios, Dictionary<string, double> best)
    {
        var names = d.RankColors.Keys.ToList();
        var tiers = names.Count - 1;
        var caps = Enumerable.Range(1, tiers).Select(i => 100.0 * i / tiers).ToList();
        var subs = scenarios.GroupBy(s => (s.Category, s.Part)).Select(g =>
        {
            var list = g.Select(s => new ScenarioProgress(s.Name, best[s.Name], Tier.TierOf(best[s.Name], s.Maxes).Rank, s.Maxes, 0, null)).ToList();
            var avg = list.Average(x => Tier.ValueOf(x.Score, x.RankMaxes));
            return new SubcategoryProgress(g.Key.Category, g.Key.Part, 100 * avg / tiers, (int)avg, caps, list);
        }).ToList();
        return new BenchmarkProgress(subs.Sum(s => s.Progress), rank, names, subs);
    }
}
