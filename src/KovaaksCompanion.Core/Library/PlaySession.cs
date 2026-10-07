namespace KovaaksCompanion.Core.Library;

/// <summary>Consecutive runs with short gaps between them. <see cref="Runs"/> is chronological.</summary>
public sealed record PlaySession(IReadOnlyList<RunRecord> Runs, int PersonalBests)
{
    /// <summary>1-based ordinal by start among all sessions of the <c>all</c> set given to <see cref="Group"/> (the oldest is 1).</summary>
    public int Number { get; init; }
    public DateTime Start => Runs[0].Start;
    public DateTime End => Runs.Max(r => r.End);
    /// <summary>Wall-clock span from first start to last end.</summary>
    public TimeSpan Span => End - Start;
    /// <summary>Sum of run durations (time actually playing).</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Runs.Sum(r => r.Duration.Ticks));
    public int RunCount => Runs.Count;
    public IReadOnlyList<string> Scenarios => Runs.Select(r => r.Scenario).Distinct().ToList();
    public int ScenarioCount => Scenarios.Count;
    public double AverageAccuracy => Runs.Average(r => r.Accuracy);

    public static readonly TimeSpan DefaultGap = TimeSpan.FromMinutes(20);

    /// <summary>
    /// The candidate whose scenario set covers the most runs of this session (a scenario in several sets counts for each);
    /// ties go to the higher <c>Hard</c>, then the higher <c>Weight</c>. Null when no candidate has a run here.
    /// </summary>
    public int? MostPlayed(IEnumerable<(int Key, IReadOnlyCollection<string> Scenarios, int Hard, double Weight)> candidates)
    {
        int? best = null;
        (int Count, int Hard, double Weight) top = default;
        foreach (var (key, scenarios, hard, weight) in candidates)
        {
            var set = scenarios as ISet<string> ?? scenarios.ToHashSet();
            var n = Runs.Count(r => set.Contains(r.Scenario));
            if (n == 0) continue;
            if (best == null || (n, hard, weight).CompareTo(top) > 0) { best = key; top = (n, hard, weight); }
        }
        return best;
    }

    /// <summary>True while the session can still take a run: its last run ended within <paramref name="maxGap"/> of <paramref name="now"/>.</summary>
    public bool IsLive(DateTime now, TimeSpan? maxGap = null) => now - End <= (maxGap ?? DefaultGap);

    /// <summary>
    /// Groups runs into sessions (a new one starts when next.Start - previous.End exceeds <paramref name="maxGap"/>),
    /// newest session first. A run is a personal best when it beat every earlier run of its scenario in
    /// <paramref name="all"/> (defaults to <paramref name="runs"/>).
    /// </summary>
    public static List<PlaySession> Group(IEnumerable<RunRecord> runs, TimeSpan? maxGap = null, IEnumerable<RunRecord>? all = null)
    {
        var gap = maxGap ?? DefaultGap;
        var ordered = runs.OrderBy(r => r.Start).ToList();
        var pbs = new HashSet<RunRecord>(ReferenceEqualityComparer.Instance);
        foreach (var g in (all ?? ordered).GroupBy(r => r.Scenario))
        {
            var stats = new ScenarioStats(g.Key, g.OrderBy(r => r.Start).ToList());
            foreach (var i in stats.PersonalBestIndexes()) pbs.Add(stats.Runs[i]);
        }
        var sessions = Split(ordered, gap).Select(c => new PlaySession(c, c.Count(pbs.Contains))).ToList();
        // Number by the full set's chronological grouping so a session keeps its number whichever subset is shown.
        var starts = Split((all ?? ordered).OrderBy(r => r.Start).ToList(), gap).Select(c => c[0].Start).ToList();
        for (var i = 0; i < sessions.Count; i++)
            sessions[i] = sessions[i] with { Number = Math.Max(1, starts.Count(t => t <= sessions[i].Start)) };
        sessions.Reverse();
        return sessions;
    }

    static List<List<RunRecord>> Split(List<RunRecord> ordered, TimeSpan gap)
    {
        var result = new List<List<RunRecord>>();
        var cur = new List<RunRecord>();
        var end = DateTime.MinValue;
        foreach (var r in ordered)
        {
            if (cur.Count > 0 && r.Start - end > gap) { result.Add(cur); cur = []; }
            end = cur.Count == 0 || r.End > end ? r.End : end;
            cur.Add(r);
        }
        if (cur.Count > 0) result.Add(cur);
        return result;
    }
}
