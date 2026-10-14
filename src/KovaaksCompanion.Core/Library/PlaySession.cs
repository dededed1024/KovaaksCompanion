namespace KovaaksCompanion.Core.Library;

/// <summary>Consecutive plays with short gaps between them. <see cref="Runs"/> is chronological; a play only the server knows is a run with no accuracy or kills (<see cref="RunRecord.FromServer"/>).</summary>
public sealed record PlaySession(IReadOnlyList<RunRecord> Runs, int PersonalBests)
{
    /// <summary>1-based ordinal by start among all sessions of the <c>all</c> set given to <see cref="Group"/> (the oldest is 1).</summary>
    public int Number { get; init; }
    /// <summary>Opening time of a session that has no runs yet (an empty live session).</summary>
    public DateTime Opened { get; init; }
    public DateTime Start => Runs.Count > 0 ? Runs[0].Start : Opened;
    public DateTime End => Runs.Count > 0 ? Runs.Max(r => r.End) : Opened;
    /// <summary>Wall-clock span from first start to last end.</summary>
    public TimeSpan Span => End - Start;
    /// <summary>Sum of run durations (time actually playing).</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Runs.Sum(r => r.Duration.Ticks));
    public int RunCount => Runs.Count;
    public IReadOnlyList<string> Scenarios => Runs.Select(r => r.Scenario).Distinct().ToList();
    public int ScenarioCount => Scenarios.Count;
    /// <summary>Mean accuracy of the runs that have one; null when none does.</summary>
    public double? AverageAccuracy => Runs.Where(r => r.Accuracy != null).Select(r => r.Accuracy!.Value).DefaultIfEmpty(double.NaN).Average() is var a && !double.IsNaN(a) ? a : null;

    /// <summary>Gap that splits sessions of runs that were not finished in a live session (legacy runs).</summary>
    public static readonly TimeSpan DefaultGap = TimeSpan.FromMinutes(20);

    /// <summary>Live session id per run key (<see cref="LiveRunLog"/>). Runs with the same id are one session, whatever the gap.</summary>
    public static IReadOnlyDictionary<string, Guid> LiveIds { get; set; } = new Dictionary<string, Guid>();

    static Guid? LiveId(string scenario, DateTime end) => LiveIds.TryGetValue(UiState.RunKey(scenario, end), out var id) ? id : null;

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
    /// Groups runs into sessions (see <see cref="NewSession"/>), newest session first. A run is a personal best when it beat every earlier run
    /// of its scenario in <paramref name="all"/> (defaults to <paramref name="runs"/>); pass <paramref name="personalBests"/>
    /// (<see cref="ScoreBook.PersonalBestRuns"/>) to count against server scores too.
    /// </summary>
    public static List<PlaySession> Group(IEnumerable<RunRecord> runs, TimeSpan? maxGap = null, IEnumerable<RunRecord>? all = null, IReadOnlySet<RunRecord>? personalBests = null)
    {
        var gap = maxGap ?? DefaultGap;
        var ordered = runs.OrderBy(r => r.Start).ToList();
        var pbs = personalBests ?? LocalPersonalBests(all ?? ordered);
        var sessions = Split(ordered, gap).Select(c => new PlaySession(c, c.Count(pbs.Contains))).ToList();
        // Number by the full set's chronological grouping so a session keeps its number whichever subset is shown.
        var starts = Split((all ?? ordered).OrderBy(r => r.Start).ToList(), gap).Select(c => c[0].Start).ToList();
        for (var i = 0; i < sessions.Count; i++)
            sessions[i] = sessions[i] with { Number = Math.Max(1, starts.Count(t => t <= sessions[i].Start)) };
        sessions.Reverse();
        return sessions;
    }

    static HashSet<RunRecord> LocalPersonalBests(IEnumerable<RunRecord> all)
    {
        var pbs = new HashSet<RunRecord>(ReferenceEqualityComparer.Instance);
        foreach (var g in all.GroupBy(r => r.Scenario))
        {
            var stats = new ScenarioStats(g.Key, g.OrderBy(r => r.Start).ToList());
            foreach (var i in stats.PersonalBestRunIndexes()) pbs.Add(stats.Runs[i]);
        }
        return pbs;
    }

    /// <summary>Runs finished in a live session stay with it and never join another; runs outside any live session (legacy) split by the time gap.</summary>
    public static bool NewSession(string prevScenario, DateTime prevEnd, string nextScenario, DateTime nextStart, DateTime nextEnd, TimeSpan gap)
    {
        Guid? prev = LiveId(prevScenario, prevEnd), next = LiveId(nextScenario, nextEnd);
        return prev != null || next != null ? prev != next : nextStart - prevEnd > gap;
    }

    static List<List<RunRecord>> Split(List<RunRecord> ordered, TimeSpan gap)
    {
        var result = new List<List<RunRecord>>();
        var cur = new List<RunRecord>();
        RunRecord? last = null;
        foreach (var r in ordered)
        {
            if (cur.Count > 0 && NewSession(last!.Scenario, last.End, r.Scenario, r.Start, r.End, gap)) { result.Add(cur); cur = []; }
            if (cur.Count == 0 || r.End > last!.End) last = r;
            cur.Add(r);
        }
        if (cur.Count > 0) result.Add(cur);
        return result;
    }
}
