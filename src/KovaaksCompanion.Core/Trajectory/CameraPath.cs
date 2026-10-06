namespace KovaaksCompanion.Core.Trajectory;

public readonly record struct ViewDirection(double YawDeg, double PitchDeg);

public readonly record struct TrailPoint(double TSec, ViewDirection Dir, double Opacity);

public readonly record struct Trail(IReadOnlyList<TrailPoint> Points, IReadOnlyList<TrailPoint> FireMarks);

/// <summary>Pure view math over camera samples (VIEW-003/004).</summary>
public static class CameraPath
{
    public const double TrailWindowSec = 1.0;

    /// <summary>Orientation at time t; yaw takes the shortest path between samples. Clamps outside the sample range.</summary>
    public static ViewDirection At(IReadOnlyList<CameraSample> samples, double t)
    {
        if (samples.Count == 0) return default;
        if (t <= samples[0].TSec) return new(samples[0].YawDeg, samples[0].PitchDeg);
        var last = samples[^1];
        if (t >= last.TSec) return new(last.YawDeg, last.PitchDeg);

        int lo = 0, hi = samples.Count - 1; // samples[lo].TSec <= t < samples[hi].TSec
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (samples[mid].TSec <= t) lo = mid; else hi = mid;
        }
        var a = samples[lo];
        var b = samples[hi];
        var f = (t - a.TSec) / (b.TSec - a.TSec);
        return new(a.YawDeg + WrapDelta(b.YawDeg - a.YawDeg) * f, a.PitchDeg + (b.PitchDeg - a.PitchDeg) * f);
    }

    /// <summary>Wraps an angle difference to [-180, 180].</summary>
    public static double WrapDelta(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        else if (deg < -180) deg += 360;
        return deg;
    }

    /// <summary>
    /// Crosshair path over the last <paramref name="window"/> seconds ending at t (nothing from the future).
    /// Opacity is 1 at t and falls linearly to 0 at t - window. Fire marks are the Fire events inside the window.
    /// </summary>
    public static Trail BuildTrail(IReadOnlyList<CameraSample> samples, IReadOnlyList<TrajectoryEvent> events, double t, double window = TrailWindowSec)
    {
        var from = t - window;
        double Opacity(double ts) => Math.Clamp(1 - (t - ts) / window, 0, 1);
        TrailPoint P(double ts) => new(ts, At(samples, ts), Opacity(ts));

        var points = new List<TrailPoint> { P(Math.Max(from, 0)) };
        foreach (var s in samples)
        {
            if (s.TSec > from && s.TSec > points[0].TSec && s.TSec < t) points.Add(new(s.TSec, new(s.YawDeg, s.PitchDeg), Opacity(s.TSec)));
        }
        if (t > points[0].TSec) points.Add(P(t));

        var marks = events.Where(e => e.Type == TrajectoryEventType.Fire && e.TSec >= from && e.TSec <= t)
            .Select(e => P(e.TSec)).ToList();
        return new Trail(points, marks);
    }
}
