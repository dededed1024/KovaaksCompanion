using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Trajectory;

/// <param name="Trajectory">Null when degrees are unavailable (unknown sens scale); raw counts are then in <see cref="RawBuckets"/>.</param>
public sealed record BuiltTrajectory(TrajectoryData? Trajectory, IReadOnlyList<CountBucket> RawBuckets, bool Partial, bool DegreesAvailable);

public static class TrajectoryBuilder
{
    public const double DefaultRateHz = 120;

    /// <summary>
    /// Cuts the run window [Start, End] out of the ring buffer. Time 0 = run Start. Kill events come from the CSV.
    /// Partial when the window starts before the buffer coverage or ends after capture stopped.
    /// </summary>
    public static BuiltTrajectory Build(RunStats run, MouseRingBuffer buffer, ClockAnchor anchor, double rateHz = DefaultRateHz)
    {
        var startQpc = anchor.ToQpc(run.Start);
        var endQpc = anchor.ToQpc(run.End);
        var events = buffer.Slice(startQpc, endQpc);
        var partial = startQpc < buffer.CoverageStartQpc || endQpc > buffer.CoverageEndQpc;

        var buckets = MouseSampler.Bucket(events, startQpc, endQpc, anchor.Frequency, rateHz);
        var degrees = SensConversion.TryGetDegreesPerCount(run.Settings, out var yaw, out var pitch);
        if (!degrees) return new BuiltTrajectory(null, buckets, partial, false);

        var samples = MouseSampler.Integrate(buckets, rateHz, yaw, pitch);
        var evs = MouseSampler.ButtonEvents(events, startQpc, endQpc, anchor.Frequency);
        evs.AddRange(KillEvents(run));
        evs.Sort((a, b) => a.TSec.CompareTo(b.TSec));
        return new BuiltTrajectory(new TrajectoryData(rateHz, samples, evs), buckets, partial, true);
    }

    public static IEnumerable<TrajectoryEvent> KillEvents(RunStats run) =>
        run.KillEvents.Select(k => new TrajectoryEvent((float)(k.Time - run.Start).TotalSeconds, TrajectoryEventType.Kill));
}
