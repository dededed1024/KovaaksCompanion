using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.Core.Library;

/// <summary>
/// The one place that joins local runs with server data: <see cref="For"/> gives the merged, cached <see cref="ScenarioStats"/> of a
/// scenario, and the totals (record count, personal bests) are cached until a scenario's server data changes.
/// </summary>
public sealed class ScoreBook
{
    sealed record Summary(int Records, int PersonalBests, HashSet<RunRecord> PersonalBestRuns, HashSet<DateTime> PersonalBestDays, Dictionary<DateTime, int> PlaysPerDay);

    readonly Dictionary<string, IReadOnlyList<CloudScore>> _cloud = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, double> _serverBest = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ScenarioStats> _stats = new(StringComparer.OrdinalIgnoreCase);
    Summary? _summary;

    public ScoreBook(RunLibrary lib) => Lib = lib;

    public RunLibrary Lib { get; }

    /// <summary>The same server data over a new run library (after the library was rescanned).</summary>
    public ScoreBook WithLibrary(RunLibrary lib)
    {
        var book = new ScoreBook(lib);
        foreach (var (n, l) in _cloud) book._cloud[n] = l;
        foreach (var (n, v) in _serverBest) book._serverBest[n] = v;
        return book;
    }

    public ScenarioStats For(string scenario)
    {
        if (_stats.TryGetValue(scenario, out var s)) return s;
        return _stats[scenario] = new(scenario, Lib.Runs(scenario), _cloud.GetValueOrDefault(scenario), _serverBest.GetValueOrDefault(scenario));
    }

    /// <summary>Sets a scenario's server scores. False when they are the same as before.</summary>
    public bool SetCloud(string scenario, IReadOnlyList<CloudScore> scores)
    {
        if (_cloud.TryGetValue(scenario, out var old) && old.SequenceEqual(scores)) return false;
        _cloud[scenario] = scores;
        Invalidate(scenario);
        return true;
    }

    /// <summary>Raises a scenario's server all-time best (it never goes down). False when unchanged.</summary>
    public bool RaiseServerBest(string scenario, double score)
    {
        if (score <= _serverBest.GetValueOrDefault(scenario)) return false;
        _serverBest[scenario] = score;
        Invalidate(scenario);
        return true;
    }

    void Invalidate(string scenario)
    {
        _stats.Remove(scenario);
        _summary = null;
    }

    Summary Totals()
    {
        if (_summary != null) return _summary;
        var runs = new HashSet<RunRecord>(ReferenceEqualityComparer.Instance);
        var days = new HashSet<DateTime>();
        var perDay = new Dictionary<DateTime, int>();
        int records = 0, pbs = 0;
        foreach (var n in Lib.Scenarios.Concat(_cloud.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var s = For(n);
            records += s.Plays;
            pbs += s.PersonalBests.Count;
            foreach (var r in s.Records) perDay[r.Time.Date] = perDay.GetValueOrDefault(r.Time.Date) + 1;
            foreach (var r in s.PersonalBests) { days.Add(r.Time.Date); if (r.Run is { } run) runs.Add(run); }
        }
        return _summary = new(records, pbs, runs, days, perDay);
    }

    /// <summary>Every score of every scenario, local or server.</summary>
    public int RecordCount => Totals().Records;
    /// <summary>Records that beat every earlier one of their scenario.</summary>
    public int PersonalBestCount => Totals().PersonalBests;
    /// <summary>The local runs among the personal bests.</summary>
    public IReadOnlySet<RunRecord> PersonalBestRuns => Totals().PersonalBestRuns;
    public IReadOnlySet<DateTime> PersonalBestDays => Totals().PersonalBestDays;
    /// <summary>Records per calendar day, local runs and server scores together.</summary>
    public IReadOnlyDictionary<DateTime, int> PlaysPerDay => Totals().PlaysPerDay;
}
