using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Replays;

/// <summary>One kill as a replay keeps it: when (ms after the run's start), how long the target lived, and the shots fired at it.</summary>
public readonly record struct RepKill(int TimeMs, double TtkSec, int Shots, int Hits, int OverShots);

/// <summary>
/// The run statistics a replay keeps in its data file: the input settings, the totals and every kill. Scenario, times and score are in <see cref="ReplayInfo"/>,
/// which <see cref="ToRunStats"/> adds back.
/// </summary>
/// <param name="Reloads">Reloads and the distance the player moved; null in a replay saved before they were kept.</param>
public sealed record RepStats(InputSettings Settings, int Kills, int HitCount, int MissCount, IReadOnlyList<RepKill> KillList, int? Reloads = null, double? Distance = null, string Hash = "", string GameVersion = "")
{
    public static RepStats From(RunStats r) => new(r.Settings, r.Kills, r.HitCount, r.MissCount,
        r.KillEvents.Select(k => new RepKill((int)Math.Round((k.Time - r.Start).TotalMilliseconds), k.Ttk.TotalSeconds, k.Shots, k.Hits, k.OverShots)).ToList(),
        r.Reloads, r.DistanceTraveled, r.Hash, r.GameVersion);

    public RunStats ToRunStats(ReplayInfo info) => new()
    {
        Scenario = info.Scenario, Start = info.Start, End = info.End, Score = info.Score,
        Kills = Kills, HitCount = HitCount, MissCount = MissCount, Settings = Settings,
        Reloads = Reloads, DistanceTraveled = Distance, Hash = Hash, GameVersion = GameVersion,
        KillEvents = KillList.Select((k, i) => new KillEvent(i + 1, info.Start.AddMilliseconds(k.TimeMs), "", "", TimeSpan.FromSeconds(k.TtkSec), k.Shots, k.Hits, k.OverShots)).ToList(),
    };
}
