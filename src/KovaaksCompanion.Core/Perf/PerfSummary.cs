namespace KovaaksCompanion.Core.Perf;

/// <summary>Per-run figures that need the per-second data: accuracy drift over the run.</summary>
public sealed record PerfSummary(double FirstHalfAccuracy, double SecondHalfAccuracy)
{
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
        return new PerfSummary(Acc(b.Take(mid)), Acc(b.Skip(mid)));
    }
}

/// <summary>Running totals from the run start. <see cref="DamageEff"/> is NaN until some damage was possible.</summary>
public readonly record struct PerfPoint(double TSec, double Accuracy, double Score, int Kills, int Shots, int Hits, double DamageEff);

public static class PerfSeries
{
    /// <summary>One point per bucket.</summary>
    public static IReadOnlyList<PerfPoint> Build(PerfData perf)
    {
        var b = perf.Buckets;
        var res = new List<PerfPoint>(b.Count);
        int shots = 0, hits = 0, kills = 0;
        double score = 0, dmg = 0, possible = 0;
        foreach (var k in b)
        {
            shots += k.Shots; hits += k.Hits; kills += k.Kills; score += k.Score; dmg += k.DamageDone; possible += k.DamagePossible;
            res.Add(new PerfPoint(k.TSec, shots == 0 ? 0 : (double)hits / shots, score, kills, shots, hits, possible > 0 ? dmg / possible : double.NaN));
        }
        return res;
    }
}
