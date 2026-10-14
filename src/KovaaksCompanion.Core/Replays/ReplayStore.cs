using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Replays;

/// <summary>A replay read from the archive. <see cref="VideoPath"/> is null when it has no video or the video file is gone.</summary>
public sealed record LoadedReplay(ReplayInfo Info, TrajectoryData? Trajectory, RunStats? Stats, PerfData? Perf, string? VideoPath);

/// <summary>Functions on replay data that need no disk access; the disk side is <see cref="ReplayArchive"/>.</summary>
public static class ReplayStore
{
    /// <summary>Max gap between a stats-folder run's end and a replay's end for both to be the same run.</summary>
    public static readonly TimeSpan RunMatchTolerance = TimeSpan.FromSeconds(60);

    /// <summary>Index of the <paramref name="scenario"/> replay ending nearest <paramref name="end"/> within <see cref="RunMatchTolerance"/>, else -1.</summary>
    public static int FindRun(IReadOnlyList<ReplayInfo> replays, string scenario, DateTime end)
    {
        var best = -1;
        var bestGap = RunMatchTolerance;
        for (var i = 0; i < replays.Count; i++)
        {
            if (!replays[i].Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)) continue;
            var gap = (replays[i].End - end).Duration();
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        return best;
    }

    /// <summary>Whether <see cref="FindRun"/> finds a replay of the run that has a video (a replay without one has only its graphs).</summary>
    public static bool HasVideo(IReadOnlyList<ReplayInfo> replays, string scenario, DateTime end) =>
        FindRun(replays, scenario, end) is var i && i >= 0 && replays[i].HasVideo;

    /// <summary>The statistics of the replay's stats, perf and trajectory, or null when it has no statistics.</summary>
    public static RunRecord? ReadRecord(ReplayInfo info, RepParts parts)
    {
        try
        {
            if (parts.Stats is null) return null;
            AccuracyDrift? drift = null;
            if (parts.Perf is { } p)
            {
                try { drift = AccuracyDrift.From(PerfParser.Parse(p)); }
                catch (PerfFormatException) { }
            }
            var speed = parts.Trajectory is { } t ? ReadSpeed(t) : null;
            return RunRecord.From(parts.Stats.ToRunStats(info), drift) with { Speed = speed };
        }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }
    }

    /// <summary>The mouse-speed figures of a trajectory; null when it is unreadable or too short to analyse.</summary>
    public static RunSpeed? ReadSpeed(byte[] trajectory)
    {
        try { return MouseSpeedStats.Analyze(TrajectorySerializer.Deserialize(trajectory)); }
        catch (TrajectoryFormatException) { return null; }
    }

    /// <summary>Throws <see cref="TrajectoryFormatException"/> with the reason on an unsupported trajectory version.</summary>
    public static LoadedReplay Open(ReplayInfo info, RepParts parts, string? videoPath)
    {
        var traj = parts.Trajectory is { } t ? TrajectorySerializer.Deserialize(t) : null;
        var stats = parts.Stats?.ToRunStats(info);
        PerfData? perf = null;
        if (parts.Perf is { } p)
        {
            try { perf = PerfParser.Parse(p); }
            catch (PerfFormatException) { } // optional data: the replay still opens without it
        }
        return new LoadedReplay(info, traj, stats, perf, videoPath);
    }
}
