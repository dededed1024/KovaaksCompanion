namespace KovaaksCompanion.Core.Library;

/// <summary>A playlist seen through the player's history: per-scenario stats and progress over time.</summary>
public sealed record PlaylistReport(string Name, IReadOnlyList<ScenarioStats> Scenarios)
{
    public int ScenarioCount => Scenarios.Count;
    public int PlayedCount => Scenarios.Count(s => s.Plays > 0);
    public int TotalPlays => Scenarios.Sum(s => s.Plays);
    public TimeSpan TimePlayed => TimeSpan.FromTicks(Scenarios.Sum(s => s.TimePlayed.Ticks));

    public DateTime? LastPlayed => Scenarios.Select(s => s.LastPlayed).OfType<DateTime>().Select(d => (DateTime?)d).Max();

    /// <summary>
    /// Progress after each run in the playlist, oldest first: mean over the scenarios played so far of
    /// (best score so far / all-time best). 1 = every scenario played so far is at its personal best.
    /// </summary>
    public IReadOnlyList<(DateTime When, double Progress)> ProgressOverTime()
    {
        var events = Scenarios
            .Where(s => s.Plays > 0 && s.Best > 0)
            .SelectMany(s => s.Runs.Select(r => (Scenario: s, Run: r)))
            .OrderBy(e => e.Run.End)
            .ToList();
        var bestNow = new Dictionary<ScenarioStats, double>();
        var points = new List<(DateTime, double)>(events.Count);
        foreach (var e in events)
        {
            bestNow[e.Scenario] = Math.Max(bestNow.GetValueOrDefault(e.Scenario), e.Run.Score);
            points.Add((e.Run.End, bestNow.Average(kv => kv.Value / kv.Key.Best)));
        }
        return points;
    }

    /// <summary>Plays per day across the playlist, oldest first.</summary>
    public IReadOnlyList<(DateTime Day, int Plays)> PlaysPerDay() =>
        Scenarios.SelectMany(s => s.Runs).GroupBy(r => r.End.Date).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

    public int PlaysSince(DateTime since) => Scenarios.Sum(s => s.Runs.Count(r => r.End >= since));

    public static PlaylistReport Build(string name, IEnumerable<string> scenarios, RunLibrary lib) =>
        new(name, scenarios.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => ScenarioStats.For(lib, n)).ToList());
}
