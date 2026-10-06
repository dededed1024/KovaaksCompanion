namespace KovaaksCompanion.Core.Perf;

/// <summary>Per-run figures that need the per-second data: accuracy drift over the run and the best stretch.</summary>
/// <param name="BestWindowScore">Highest score earned in any <see cref="WindowSec"/> consecutive seconds.</param>
public sealed record PerfSummary(double FirstHalfAccuracy, double SecondHalfAccuracy, double BestWindowScore)
{
    public const int WindowSec = 10;

    /// <summary>Second half minus first half; negative when accuracy fell during the run.</summary>
    public double Drift => SecondHalfAccuracy - FirstHalfAccuracy;

    /// <summary>Null when there are no shots at all. Halves split the buckets by count.</summary>
    public static PerfSummary? From(PerfData perf)
    {
        var b = perf.Buckets;
        if (b.Count == 0 || perf.TotalShots == 0) return null;

        var mid = b.Count / 2;
        static double Acc(IEnumerable<PerfBucket> x)
        {
            int shots = 0, hits = 0;
            foreach (var k in x) { shots += k.Shots; hits += k.Hits; }
            return shots == 0 ? 0 : (double)hits / shots;
        }

        double best = 0, window = 0;
        for (var i = 0; i < b.Count; i++)
        {
            window += b[i].Score;
            if (i >= WindowSec) window -= b[i - WindowSec].Score;
            best = Math.Max(best, window);
        }
        return new PerfSummary(Acc(b.Take(mid)), Acc(b.Skip(mid)), best);
    }
}

/// <param name="Accuracy">Running accuracy from the run start.</param>
/// <param name="RecentAccuracy">Accuracy over the last <see cref="PerfSeries.RecentSec"/> seconds.</param>
/// <param name="Score">Running score.</param>
public readonly record struct PerfPoint(double TSec, double Accuracy, double RecentAccuracy, double Score);

public static class PerfSeries
{
    public const int RecentSec = 10;

    /// <summary>One point per bucket. A recent window with no shots repeats the previous recent accuracy.</summary>
    public static IReadOnlyList<PerfPoint> Build(PerfData perf)
    {
        var b = perf.Buckets;
        var res = new List<PerfPoint>(b.Count);
        int shots = 0, hits = 0, wShots = 0, wHits = 0;
        double score = 0, recent = 0;
        for (var i = 0; i < b.Count; i++)
        {
            shots += b[i].Shots; hits += b[i].Hits; score += b[i].Score;
            wShots += b[i].Shots; wHits += b[i].Hits;
            if (i >= RecentSec) { wShots -= b[i - RecentSec].Shots; wHits -= b[i - RecentSec].Hits; }
            if (wShots > 0) recent = (double)wHits / wShots;
            res.Add(new PerfPoint(b[i].TSec, shots == 0 ? 0 : (double)hits / shots, recent, score));
        }
        return res;
    }
}
