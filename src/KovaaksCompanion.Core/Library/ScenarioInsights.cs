namespace KovaaksCompanion.Core.Library;

/// <summary>One column of the summary table: the figures over a set of records (and, for accuracy, the local runs among them).</summary>
public sealed record ScoreSummary(int Plays, double Best, double Average, double Median, double? Consistency, double? Accuracy, double? Drift, TimeSpan TimePlayed);

/// <summary>A histogram bin: scores from <see cref="From"/> up to (excluding) <see cref="To"/>, except the last bin which includes its upper end.</summary>
public sealed record ScoreBin(double From, double To, int Count);

/// <summary>Average score of the local runs started in an hour block of the day (<see cref="Hour"/> is the first hour).</summary>
public sealed record DayBlock(int Hour, int Runs, double Average);

/// <summary>Average score of the n-th play of the scenario within a session (the last row is "n or later").</summary>
public sealed record SessionSlot(int Position, bool OrLater, int Plays, double Average);

/// <summary>
/// Statistics of one scenario that go beyond the server's: they need every score (distribution, consistency, personal-best
/// history) or the local runs' accuracy, start times and session membership.
/// </summary>
public static class ScenarioInsights
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.Order().ToList();
        var mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2;
    }

    /// <summary>Coefficient of variation (population standard deviation over the mean): lower is steadier. Null with fewer than 3 scores or a non-positive mean.</summary>
    public static double? Consistency(IReadOnlyList<double> values)
    {
        if (values.Count < 3) return null;
        var mean = values.Average();
        if (mean <= 0) return null;
        var variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return Math.Sqrt(variance) / mean;
    }

    /// <summary>Figures over the given records; the local runs among them supply accuracy and time played.</summary>
    public static ScoreSummary? Summarize(IReadOnlyList<ScoreRecord> records)
    {
        if (records.Count == 0) return null;
        var scores = records.Select(r => r.Score).ToList();
        var runs = records.Select(r => r.Run).OfType<RunRecord>().ToList();
        return new ScoreSummary(records.Count, scores.Max(), scores.Average(), Median(scores), Consistency(scores),
            runs.Count == 0 ? null : runs.Average(r => r.Accuracy),
            runs.Select(r => r.Perf?.Drift).OfType<double>().ToList() is { Count: > 0 } d ? d.Average() : null, TimeSpan.FromTicks(runs.Sum(r => r.Duration.Ticks)));
    }

    /// <summary>Scores bucketed into about <paramref name="bins"/> equal-width bins between the lowest and highest score (fewer when the range is tiny). Empty for fewer than 5 scores.</summary>
    public static List<ScoreBin> Distribution(IReadOnlyList<double> scores, int bins = 12)
    {
        if (scores.Count < 5) return [];
        double lo = scores.Min(), hi = scores.Max();
        if (hi - lo < 1e-9) return [];
        var n = Math.Clamp(Math.Min(bins, (int)Math.Ceiling(Math.Sqrt(scores.Count) * 1.5)), 4, bins);
        var width = (hi - lo) / n;
        var counts = new int[n];
        foreach (var s in scores) counts[Math.Min(n - 1, (int)((s - lo) / width))]++;
        return Enumerable.Range(0, n).Select(i => new ScoreBin(lo + i * width, lo + (i + 1) * width, counts[i])).ToList();
    }

    /// <summary>Local runs by the time of day they started, in blocks of <paramref name="blockHours"/> hours; only blocks with runs. Empty for fewer than 5 runs.</summary>
    public static List<DayBlock> ByTimeOfDay(IReadOnlyList<RunRecord> runs, int blockHours = 3)
    {
        if (runs.Count < 5) return [];
        return runs.GroupBy(r => r.Start.Hour / blockHours * blockHours)
            .OrderBy(g => g.Key)
            .Select(g => new DayBlock(g.Key, g.Count(), g.Average(r => r.Score)))
            .ToList();
    }

    /// <summary>
    /// Average score of the first, second and later plays of the scenario within a play session. Sessions come from
    /// <see cref="PlaySession.Group"/> over every scenario's runs. Empty unless some session has several plays of it.
    /// </summary>
    public static List<SessionSlot> BySessionPosition(IReadOnlyList<PlaySession> sessions, string scenario)
    {
        var first = new List<double>(); var second = new List<double>(); var later = new List<double>();
        foreach (var s in sessions)
        {
            var mine = s.Runs.Where(r => r.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)).OrderBy(r => r.Start).ToList();
            for (var i = 0; i < mine.Count; i++) (i == 0 ? first : i == 1 ? second : later).Add(mine[i].Score);
        }
        if (second.Count + later.Count < 3) return [];
        var slots = new List<SessionSlot>();
        if (first.Count > 0) slots.Add(new(1, false, first.Count, first.Average()));
        if (second.Count > 0) slots.Add(new(2, false, second.Count, second.Average()));
        if (later.Count > 0) slots.Add(new(3, true, later.Count, later.Average()));
        return slots;
    }
}
