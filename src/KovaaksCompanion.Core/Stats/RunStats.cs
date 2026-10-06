namespace KovaaksCompanion.Core.Stats;

/// <summary>One finished KovaaK's run, read from its stats CSV.</summary>
public sealed record RunStats
{
    public string Scenario { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public double Score { get; init; }
    public int Kills { get; init; }
    public int HitCount { get; init; }
    public int MissCount { get; init; }
    public string Hash { get; init; } = "";
    public string GameVersion { get; init; } = "";
    public int PauseCount { get; init; }
    public TimeSpan PauseDuration { get; init; }
    public InputSettings Settings { get; init; } = new();
    public IReadOnlyList<KillEvent> KillEvents { get; init; } = [];
}

public sealed record KillEvent(
    int Index, DateTime Time, string Bot, string Weapon, TimeSpan Ttk, int Shots, int Hits, int OverShots);

public sealed record InputSettings
{
    public string SensScale { get; init; } = "";
    public double HorizSens { get; init; }
    public double VertSens { get; init; }
    public int Dpi { get; init; }
    public double Fov { get; init; }
    public string FovScale { get; init; } = "";
    public string Resolution { get; init; } = "";
}
