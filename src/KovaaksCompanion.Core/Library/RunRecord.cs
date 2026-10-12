using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Library;

/// <summary>Lightweight summary of one past run, enough for history and statistics. <paramref name="Cm360"/> is the sensitivity the run was played at (null when the CSV has no usable sens or DPI).
/// <paramref name="Speed"/> holds the mouse-speed figures of the run's trajectory; only set for archived (deleted) runs, whose trajectory file is gone.</summary>
public sealed record RunRecord(string Scenario, DateTime Start, DateTime End, double Score, double Accuracy, int Kills, PerfSummary? Perf = null, double? Cm360 = null, RunSpeed? Speed = null)
{
    public TimeSpan Duration => End - Start;

    public static RunRecord From(RunStats r, PerfSummary? perf = null)
    {
        var shots = r.HitCount + r.MissCount;
        return new(r.Scenario, r.Start, r.End, r.Score, shots > 0 ? (double)r.HitCount / shots : 0, r.Kills, perf, Input.SensConversion.TryGetCmPer360(r.Settings, out var cm) ? cm : null);
    }
}
