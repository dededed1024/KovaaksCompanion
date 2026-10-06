using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Library;

/// <summary>Lightweight summary of one past run, enough for history and statistics.</summary>
public sealed record RunRecord(string Scenario, DateTime Start, DateTime End, double Score, double Accuracy, int Kills, PerfSummary? Perf = null)
{
    public TimeSpan Duration => End - Start;

    public static RunRecord From(RunStats r, PerfSummary? perf = null)
    {
        var shots = r.HitCount + r.MissCount;
        return new(r.Scenario, r.Start, r.End, r.Score, shots > 0 ? (double)r.HitCount / shots : 0, r.Kills, perf);
    }
}
