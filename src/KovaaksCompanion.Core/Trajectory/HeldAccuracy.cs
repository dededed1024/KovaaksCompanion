using KovaaksCompanion.Core.Perf;

namespace KovaaksCompanion.Core.Trajectory;

/// <summary>
/// Tracking scenarios hold the left button the whole run and log no per-shot results. While the button is held,
/// accuracy is the Hits/Shots of the 1 s perf buckets, linearly interpolated between bucket centres.
/// </summary>
public sealed class HeldAccuracy
{
    readonly List<(double From, double To)> _held;
    readonly IReadOnlyList<PerfBucket> _buckets;

    public HeldAccuracy(IReadOnlyList<TrajectoryEvent> events, PerfData? perf, double endSec)
    {
        _held = HeldIntervals(events, endSec);
        _buckets = perf?.Buckets ?? [];
    }

    /// <summary>Left-button [press, release] intervals; a press without release lasts to <paramref name="endSec"/>.</summary>
    public static List<(double From, double To)> HeldIntervals(IReadOnlyList<TrajectoryEvent> events, double endSec)
    {
        var result = new List<(double, double)>();
        double? down = null;
        foreach (var e in events.Where(e => e.Button == 0 && e.Type is TrajectoryEventType.Fire or TrajectoryEventType.Release).OrderBy(e => e.TSec))
        {
            if (e.Type == TrajectoryEventType.Fire) down ??= e.TSec;
            else if (down is { } d) { result.Add((d, e.TSec)); down = null; }
        }
        if (down is { } last && endSec > last) result.Add((last, endSec));
        return result;
    }

    /// <summary>
    /// Accuracy (0..1) at <paramref name="tSec"/> while the button is held, linearly interpolated between the centres of
    /// neighbouring buckets (a neighbour with no shots is ignored: the current bucket's value is used). Null when not held,
    /// no perf, or the bucket has no shots.
    /// </summary>
    public double? AccuracyAt(double tSec)
    {
        if (!_held.Any(h => tSec >= h.From && tSec <= h.To)) return null;
        var i = ShotClassifier.BucketOf(_buckets, tSec);
        if (i < 0 || _buckets[i].Shots <= 0) return null;
        var acc = Acc(i);
        var centre = _buckets[i].TSec - 0.5;
        var j = tSec < centre ? i - 1 : i + 1;
        if (j < 0 || j >= _buckets.Count || _buckets[j].Shots <= 0) return acc;
        var w = Math.Abs(tSec - centre) / Math.Abs(_buckets[j].TSec - _buckets[i].TSec);
        return acc + (Acc(j) - acc) * Math.Clamp(w, 0, 1);
    }

    double Acc(int i) => Math.Clamp((double)_buckets[i].Hits / _buckets[i].Shots, 0, 1);
}
