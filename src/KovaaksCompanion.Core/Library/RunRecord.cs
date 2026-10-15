using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Library;

/// <summary>Lightweight summary of one past run, enough for history and statistics. <paramref name="Cm360"/> is the sensitivity the run was played at (null when the CSV has no usable sens or DPI).
/// <paramref name="Dpi"/> is the DPI the CSV recorded; <paramref name="FixedDpi"/> is the DPI the user corrected it to (0 = not corrected), already applied to <paramref name="Cm360"/>.
/// <paramref name="Accuracy"/> and <paramref name="Kills"/> are null for a play only the server knows (<see cref="FromCloud"/>). <paramref name="Speed"/> holds the mouse-speed figures of the run's trajectory; only set for archived (deleted) runs, whose trajectory file is gone.
/// <paramref name="AvgTtk"/> is the mean seconds a target lived and <paramref name="OverShots"/> the shots fired at targets already dead; both null for a play without kills.
/// <paramref name="Reloads"/> and <paramref name="Distance"/> (how far the player moved) are null when the run's source did not keep them.</summary>
public sealed record RunRecord(string Scenario, DateTime Start, DateTime End, double Score, double? Accuracy, int? Kills, AccuracyDrift? Drift = null, double? Cm360 = null, RunSpeed? Speed = null, int Dpi = 0, int FixedDpi = 0, double? AvgTtk = null, int? OverShots = null, int? Reloads = null, double? Distance = null)
{
    public TimeSpan Duration => End - Start;

    /// <summary>The sensitivity as the CSV recorded it, before any DPI correction.</summary>
    public double? RawCm360 => Cm360 is { } c && FixedDpi > 0 && Dpi > 0 ? c * FixedDpi / Dpi : Cm360;

    /// <summary>Identifies the recorded DPI and sensitivity a DPI correction is stored under; null when the run has no DPI or sensitivity.</summary>
    public string? DpiKey => Dpi > 0 && RawCm360 is { } c ? $"{Dpi}|{Math.Round(c, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" : null;

    /// <summary>The DPI the run's sensitivity is computed with.</summary>
    public int EffectiveDpi => FixedDpi > 0 ? FixedDpi : Dpi;

    /// <summary>A cloud score with no local run: just when it happened and the score, everything else unknown.</summary>
    public static RunRecord FromCloud(string scenario, DateTime time, double score) => new(scenario, time, time, score, null, null);

    /// <param name="perf">The run's per-second data; fills the pause, reload and distance totals a source without them (an older replay) lacks.</param>
    public static RunRecord From(RunStats r, AccuracyDrift? drift = null, PerfData? perf = null)
    {
        var shots = r.HitCount + r.MissCount;
        var killed = r.KillEvents.Count > 0;
        return new(r.Scenario, r.Start, r.End, r.Score, shots > 0 ? (double)r.HitCount / shots : 0, r.Kills, drift, Input.SensConversion.TryGetCmPer360(r.Settings, out var cm) ? cm : null, null, r.Settings.Dpi,
            AvgTtk: killed ? r.KillEvents.Average(k => k.Ttk.TotalSeconds) : null, OverShots: killed ? r.KillEvents.Sum(k => k.OverShots) : null,
            Reloads: r.Reloads ?? perf?.TotalReloads, Distance: r.DistanceTraveled ?? perf?.TotalDistance);
    }
}
