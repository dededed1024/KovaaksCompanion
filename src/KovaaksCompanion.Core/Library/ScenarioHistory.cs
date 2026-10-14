using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.Core.Library;

/// <summary>One score of a scenario: a local run, or a cloud score the local library lacks (<see cref="Run"/> is null).</summary>
public sealed record ScoreRecord(DateTime Time, double Score, RunRecord? Run);

/// <summary>
/// Score history of one scenario: the local runs plus the cloud scores the library lacks (a cloud score within <see cref="SameRunWindow"/>
/// of a local run is that same run), oldest first. Everything score-based works on <see cref="Records"/>; accuracy, duration and
/// drift exist only for the local <see cref="Runs"/>. Built and cached by <see cref="ScoreBook"/>.
/// </summary>
public sealed class ScenarioHistory
{
    /// <summary>A cloud score this close to a local run's end is that run.</summary>
    public static readonly TimeSpan SameRunWindow = TimeSpan.FromSeconds(10);

    readonly double[] _bestSoFar;
    readonly HashSet<RunRecord> _pbRuns = new(ReferenceEqualityComparer.Instance);

    /// <param name="runs">Local runs, oldest first.</param>
    /// <param name="cloud">Cloud scores of the scenario.</param>
    /// <param name="knownBest">The best score known beyond the records: the server's all-time best or the archive's best, which can be older than the scores in <paramref name="cloud"/>.</param>
    public ScenarioHistory(string scenario, IReadOnlyList<RunRecord> runs, IReadOnlyList<CloudScore>? cloud = null, double knownBest = 0)
    {
        Scenario = scenario;
        Runs = runs;
        KnownBest = knownBest;
        Records = Merge(runs, cloud ?? []);
        _bestSoFar = new double[Records.Count];
        var best = double.MinValue;
        for (var i = 0; i < Records.Count; i++)
        {
            if (Records[i].Score > best) { PersonalBests.Add(Records[i]); if (Records[i].Run is { } r) _pbRuns.Add(r); }
            best = Math.Max(best, Records[i].Score);
            _bestSoFar[i] = best;
        }
    }

    public string Scenario { get; }
    public IReadOnlyList<RunRecord> Runs { get; }
    public IReadOnlyList<ScoreRecord> Records { get; }
    public double KnownBest { get; }

    /// <summary>Records that beat every earlier record (the first counts), local or from the server.</summary>
    public List<ScoreRecord> PersonalBests { get; } = [];

    public int Plays => Records.Count;
    /// <summary>The best of all records and the server's all-time best.</summary>
    public double Best => Math.Max(Records.Count == 0 ? 0 : _bestSoFar[^1], KnownBest);
    /// <summary>The best score of a local run; 0 without any.</summary>
    public double LocalBest => Runs.Count == 0 ? 0 : Runs.Max(r => r.Score);
    public double Average => Records.Count == 0 ? 0 : Records.Average(r => r.Score);
    public double? Latest => Records.Count == 0 ? null : Records[^1].Score;
    public DateTime? LastPlayed => Records.Count == 0 ? null : Records[^1].Time;
    public double AverageAccuracy => Runs.Select(r => r.Accuracy).OfType<double>().DefaultIfEmpty(0).Average();
    public TimeSpan TimePlayed => TimeSpan.FromTicks(Runs.Sum(r => r.Duration.Ticks));

    /// <summary>Mean accuracy drift (second half minus first half) over runs that have per-second data. Null without any.</summary>
    public double? AverageDrift
    {
        get
        {
            var drifts = Runs.Select(r => r.Drift?.Change).OfType<double>().ToList();
            return drifts.Count == 0 ? null : drifts.Average();
        }
    }

    /// <summary>Latest score as a fraction of the best (1 = at personal best). Null when never played.</summary>
    public double? LatestVsBest => Best > 0 && Latest is { } l ? l / Best : null;

    /// <summary>
    /// Mean of the last <paramref name="n"/> scores minus the mean of the <paramref name="n"/> before them, as a
    /// fraction of the older mean. Null with fewer than 2n records.
    /// </summary>
    public double? Trend(int n = 5)
    {
        if (n < 1 || Records.Count < 2 * n) return null;
        var recent = Records.Skip(Records.Count - n).Average(r => r.Score);
        var before = Records.Skip(Records.Count - 2 * n).Take(n).Average(r => r.Score);
        return before > 0 ? (recent - before) / before : null;
    }

    /// <summary>Best score so far after each record, oldest first.</summary>
    public IReadOnlyList<double> BestSoFar() => _bestSoFar;

    /// <summary>Indexes into <see cref="Records"/> of the records that beat every earlier one (the first counts).</summary>
    public IReadOnlyList<int> PersonalBestIndexes() =>
        Enumerable.Range(0, Records.Count).Where(i => PersonalBests.Contains(Records[i])).ToList();

    /// <summary>Indexes into <see cref="Runs"/> of the local runs that are personal bests.</summary>
    public IReadOnlyList<int> PersonalBestRunIndexes() =>
        Enumerable.Range(0, Runs.Count).Where(i => _pbRuns.Contains(Runs[i])).ToList();

    /// <summary>True when the local run beat every earlier record, cloud scores included.</summary>
    public bool IsPersonalBest(RunRecord run) => _pbRuns.Contains(run);

    /// <summary>Best score among records at or before the cutoff; null when none is that old.</summary>
    public double? BestAsOf(DateTime cutoff)
    {
        int lo = 0, hi = Records.Count; // first record after the cutoff
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Records[mid].Time <= cutoff) lo = mid + 1; else hi = mid;
        }
        return lo == 0 ? null : _bestSoFar[lo - 1];
    }

    /// <summary>Baseline for comparing with now: the best score as of the cutoff, or, when nothing is that old, the oldest record's
    /// score (so the baseline reaches back at most to the cutoff). Null without records.</summary>
    public double? BaselineAt(DateTime cutoff) => BestAsOf(cutoff) ?? (Records.Count == 0 ? null : Records[0].Score);

    static IReadOnlyList<ScoreRecord> Merge(IReadOnlyList<RunRecord> runs, IReadOnlyList<CloudScore> cloud)
    {
        var all = new List<ScoreRecord>(runs.Count + cloud.Count);
        foreach (var r in runs) all.Add(new(r.End, r.Score, r));
        if (cloud.Count == 0) return all;
        var ends = runs.Select(r => r.End).Order().ToArray();
        var added = false;
        foreach (var c in cloud)
        {
            if (IsSameRun(ends, c.Time)) continue;
            all.Add(new(c.Time, c.Score, null));
            added = true;
        }
        return added ? all.OrderBy(r => r.Time).ToList() : all;
    }

    /// <summary>True when a score at <paramref name="time"/> is one of the runs ending at <paramref name="sortedEnds"/> (within <see cref="SameRunWindow"/>).</summary>
    internal static bool IsSameRun(DateTime[] sortedEnds, DateTime time)
    {
        var i = Array.BinarySearch(sortedEnds, time);
        if (i >= 0) return true;
        i = ~i; // first end after the score
        return (i < sortedEnds.Length && sortedEnds[i] - time < SameRunWindow) || (i > 0 && time - sortedEnds[i - 1] < SameRunWindow);
    }
}
