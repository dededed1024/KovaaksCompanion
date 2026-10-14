using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>Where a score sits on a benchmark's tier ladder. <see cref="Value"/> is the continuous progress (Tier + Fraction).</summary>
public readonly record struct TierPosition(int Tier, double Fraction, double? NextThreshold, double? ToNext)
{
    public double Value => Tier + Fraction;
}

public static class TierLadder
{
    /// <summary>
    /// <paramref name="maxes"/>[i] is the score needed to reach tier i+1. Tier counts the thresholds reached;
    /// Fraction is the progress from the last threshold (0 below the first) to the next one, 1 at the top tier.
    /// </summary>
    public static TierPosition TierOf(double score, IReadOnlyList<double> maxes)
    {
        var rank = 0;
        while (rank < maxes.Count && score >= maxes[rank]) rank++;
        if (rank >= maxes.Count) return new(rank, 1, null, null);
        var prev = rank == 0 ? 0 : maxes[rank - 1];
        var next = maxes[rank];
        var span = next - prev;
        var f = span <= 0 ? 0 : Math.Clamp((score - prev) / span, 0, 1);
        return new(rank, f, next, next - score);
    }

    /// <summary>Continuous tier progress of a score, clamped to [0, maxes.Count].</summary>
    public static double ValueOf(double score, IReadOnlyList<double> maxes) =>
        maxes.Count == 0 ? 0 : Math.Clamp(TierOf(score, maxes).Value, 0, maxes.Count);

    /// <summary>Average tier progress (<see cref="ValueOf"/>) over the given scenarios after each day with a record (local or server) of any of them; scenarios not yet played count as 0.</summary>
    public static List<(DateTime Day, double Value)> History(IEnumerable<(ScenarioHistory Stats, IReadOnlyList<double> Maxes)> scenarios)
    {
        var included = scenarios.Where(s => s.Maxes.Count > 0).ToList();
        var days = included.SelectMany(s => s.Stats.Records).Select(r => r.Time.Date).Distinct().Order().ToList();
        if (included.Count == 0 || days.Count == 0) return [];
        return days.Select(day =>
        {
            var cutoff = day.AddDays(1).AddTicks(-1);
            return (day, included.Average(s => ValueOf(s.Stats.BestAsOf(cutoff) ?? 0, s.Maxes)));
        }).ToList();
    }
}
