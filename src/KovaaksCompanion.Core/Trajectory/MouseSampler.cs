using KovaaksCompanion.Core.Input;

namespace KovaaksCompanion.Core.Trajectory;

/// <summary>Counts summed over one bucket: <c>Dx/Dy</c> happened in <c>[TSec, TSec + 1/rate)</c>.</summary>
public readonly record struct CountBucket(float TSec, long Dx, long Dy);

public static class MouseSampler
{
    /// <summary>Sums counts into fixed-rate buckets over <c>[startQpc, endQpc)</c>. Nothing is dropped; empty buckets are kept.</summary>
    public static CountBucket[] Bucket(IEnumerable<RawMouseEvent> events, long startQpc, long endQpc, long frequency, double rateHz)
    {
        var n = Math.Max(0, (int)Math.Ceiling((double)(endQpc - startQpc) / frequency * rateHz));
        var dx = new long[n];
        var dy = new long[n];
        foreach (var e in events)
        {
            if (e.Kind != RawMouseKind.Move || e.Qpc < startQpc || e.Qpc >= endQpc) continue;
            var i = Math.Min(n - 1, (int)((double)(e.Qpc - startQpc) / frequency * rateHz));
            dx[i] += e.Dx;
            dy[i] += e.Dy;
        }
        var buckets = new CountBucket[n];
        for (var i = 0; i < n; i++) buckets[i] = new CountBucket((float)(i / rateHz), dx[i], dy[i]);
        return buckets;
    }

    /// <summary>
    /// Integrates buckets into orientation samples. Sample 0 is (0,0) at t=0 (the true start orientation is unknown);
    /// sample i+1 (t=(i+1)/rate) includes bucket i. Pitch is clamped to +-90.
    /// </summary>
    public static CameraSample[] Integrate(IReadOnlyList<CountBucket> buckets, double rateHz, double yawPerCount, double pitchPerCount)
    {
        var samples = new CameraSample[buckets.Count + 1];
        double yaw = 0, pitch = 0;
        samples[0] = new CameraSample(0, 0, 0);
        for (var i = 0; i < buckets.Count; i++)
        {
            yaw += buckets[i].Dx * yawPerCount;
            pitch = Math.Clamp(pitch - buckets[i].Dy * pitchPerCount, -90, 90);
            samples[i + 1] = new CameraSample((float)((i + 1) / rateHz), (float)yaw, (float)pitch);
        }
        return samples;
    }

    /// <summary>Button events in the window with exact times relative to <paramref name="startQpc"/>.</summary>
    public static List<TrajectoryEvent> ButtonEvents(IEnumerable<RawMouseEvent> events, long startQpc, long endQpc, long frequency)
    {
        var list = new List<TrajectoryEvent>();
        foreach (var e in events)
        {
            if (e.Kind == RawMouseKind.Move || e.Qpc < startQpc || e.Qpc >= endQpc) continue;
            var type = e.Kind == RawMouseKind.ButtonDown ? TrajectoryEventType.Fire : TrajectoryEventType.Release;
            list.Add(new TrajectoryEvent((float)((double)(e.Qpc - startQpc) / frequency), type, (byte)e.Button));
        }
        return list;
    }
}
