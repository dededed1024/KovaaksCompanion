using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Library;

/// <summary>Lightweight summary of one past run, enough for history and statistics. <paramref name="Cm360"/> is the sensitivity the run was played at (null when the CSV has no usable sens or DPI).
/// <paramref name="Dpi"/> is the DPI the CSV recorded; <paramref name="FixedDpi"/> is the DPI the user corrected it to (0 = not corrected), already applied to <paramref name="Cm360"/>.
/// <paramref name="Accuracy"/> and <paramref name="Kills"/> are null for a play only the server knows (<see cref="FromServer"/>). <paramref name="Speed"/> holds the mouse-speed figures of the run's trajectory; only set for archived (deleted) runs, whose trajectory file is gone.</summary>
public sealed record RunRecord(string Scenario, DateTime Start, DateTime End, double Score, double? Accuracy, int? Kills, PerfSummary? Perf = null, double? Cm360 = null, RunSpeed? Speed = null, int Dpi = 0, int FixedDpi = 0)
{
    public TimeSpan Duration => End - Start;

    /// <summary>The sensitivity as the CSV recorded it, before any DPI correction.</summary>
    public double? RawCm360 => Cm360 is { } c && FixedDpi > 0 && Dpi > 0 ? c * FixedDpi / Dpi : Cm360;

    /// <summary>Identifies the recorded DPI and sensitivity a DPI correction is stored under; null when the run has no DPI or sensitivity.</summary>
    public string? DpiKey => Dpi > 0 && RawCm360 is { } c ? $"{Dpi}|{Math.Round(c, 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}" : null;

    /// <summary>The DPI the run's sensitivity is computed with.</summary>
    public int EffectiveDpi => FixedDpi > 0 ? FixedDpi : Dpi;

    /// <summary>A server score with no local run: just when it happened and the score, everything else unknown.</summary>
    public static RunRecord FromServer(string scenario, DateTime time, double score) => new(scenario, time, time, score, null, null);

    public static RunRecord From(RunStats r, PerfSummary? perf = null)
    {
        var shots = r.HitCount + r.MissCount;
        return new(r.Scenario, r.Start, r.End, r.Score, shots > 0 ? (double)r.HitCount / shots : 0, r.Kills, perf, Input.SensConversion.TryGetCmPer360(r.Settings, out var cm) ? cm : null, null, r.Settings.Dpi);
    }
}
