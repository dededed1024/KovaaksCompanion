namespace KovaaksCompanion.Core.Library;

/// <summary>A playlist seen through the player's history, local and server: per-scenario stats and progress over time.</summary>
public sealed record PlaylistReport(string Name, IReadOnlyList<ScenarioHistory> Scenarios)
{
    public int ScenarioCount => Scenarios.Count;
    public int PlayedCount => Scenarios.Count(s => s.Plays > 0);
    public int TotalPlays => Scenarios.Sum(s => s.Plays);
    public TimeSpan TimePlayed => TimeSpan.FromTicks(Scenarios.Sum(s => s.TimePlayed.Ticks));

    public DateTime? LastPlayed => Scenarios.Select(s => s.LastPlayed).OfType<DateTime>().Select(d => (DateTime?)d).Max();

    /// <summary>
    /// Progress after each record in the playlist, oldest first: mean over the scenarios played so far of
    /// (best score so far / all-time best). 1 = every scenario played so far is at its personal best.
    /// </summary>
    public IReadOnlyList<(DateTime When, double Progress)> ProgressOverTime()
    {
        var events = Scenarios
            .Where(s => s.Plays > 0 && s.Best > 0)
            .SelectMany(s => s.Records.Select(r => (Scenario: s, Record: r)))
            .OrderBy(e => e.Record.Time)
            .ToList();
        var bestNow = new Dictionary<ScenarioHistory, double>();
        var points = new List<(DateTime, double)>(events.Count);
        foreach (var e in events)
        {
            bestNow[e.Scenario] = Math.Max(bestNow.GetValueOrDefault(e.Scenario), e.Record.Score);
            points.Add((e.Record.Time, bestNow.Average(kv => kv.Value / kv.Key.Best)));
        }
        return points;
    }

    /// <summary>Plays per day across the playlist, oldest first.</summary>
    public IReadOnlyList<(DateTime Day, int Plays)> PlaysPerDay() =>
        Scenarios.SelectMany(s => s.Records).GroupBy(r => r.Time.Date).OrderBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();

    /// <summary>Days with a personal best in any scenario of the playlist.</summary>
    public IReadOnlySet<DateTime> PersonalBestDays() =>
        Scenarios.SelectMany(s => s.PersonalBests).Select(r => r.Time.Date).ToHashSet();

    public int PlaysSince(DateTime since) => Scenarios.Sum(s => s.Records.Count(r => r.Time >= since));

    public static PlaylistReport Build(string name, IEnumerable<string> scenarios, ScoreBook book) =>
        new(name, scenarios.Distinct(StringComparer.OrdinalIgnoreCase).Select(book.For).ToList());
}
