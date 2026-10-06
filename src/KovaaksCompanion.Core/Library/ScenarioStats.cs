namespace KovaaksCompanion.Core.Library;

/// <summary>Score history of one scenario.</summary>
public sealed record ScenarioStats(string Scenario, IReadOnlyList<RunRecord> Runs)
{
    public int Plays => Runs.Count;
    public double Best => Runs.Count == 0 ? 0 : Runs.Max(r => r.Score);
    public double Average => Runs.Count == 0 ? 0 : Runs.Average(r => r.Score);
    public double? Latest => Runs.Count == 0 ? null : Runs[^1].Score;
    public DateTime? LastPlayed => Runs.Count == 0 ? null : Runs[^1].End;
    public double AverageAccuracy => Runs.Count == 0 ? 0 : Runs.Average(r => r.Accuracy);
    public TimeSpan TimePlayed => TimeSpan.FromTicks(Runs.Sum(r => r.Duration.Ticks));

    /// <summary>Mean accuracy drift (second half minus first half) over runs that have per-second data. Null without any.</summary>
    public double? AverageDrift
    {
        get
        {
            var drifts = Runs.Select(r => r.Perf?.Drift).OfType<double>().ToList();
            return drifts.Count == 0 ? null : drifts.Average();
        }
    }

    /// <summary>Latest score as a fraction of the best (1 = at personal best). Null when never played.</summary>
    public double? LatestVsBest => Best > 0 && Latest is { } l ? l / Best : null;

    /// <summary>
    /// Mean of the last <paramref name="n"/> scores minus the mean of the <paramref name="n"/> before them, as a
    /// fraction of the older mean. Null with fewer than 2n runs.
    /// </summary>
    public double? Trend(int n = 5)
    {
        if (n < 1 || Runs.Count < 2 * n) return null;
        var recent = Runs.Skip(Runs.Count - n).Average(r => r.Score);
        var before = Runs.Skip(Runs.Count - 2 * n).Take(n).Average(r => r.Score);
        return before > 0 ? (recent - before) / before : null;
    }

    /// <summary>Best score so far after each run, oldest first.</summary>
    public IReadOnlyList<double> BestSoFar()
    {
        var res = new List<double>(Runs.Count);
        var best = double.MinValue;
        foreach (var r in Runs) { best = Math.Max(best, r.Score); res.Add(best); }
        return res;
    }

    /// <summary>Indexes of runs that beat every earlier run (the first run counts).</summary>
    public IReadOnlyList<int> PersonalBestIndexes()
    {
        var res = new List<int>();
        var best = double.MinValue;
        for (var i = 0; i < Runs.Count; i++)
            if (Runs[i].Score > best) { best = Runs[i].Score; res.Add(i); }
        return res;
    }

    public static ScenarioStats For(RunLibrary lib, string scenario) => new(scenario, lib.Runs(scenario));
}
