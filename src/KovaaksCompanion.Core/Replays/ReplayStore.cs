using System.Text;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Replays;

public sealed record LoadedReplay(string Folder, ReplayInfo Info, TrajectoryData? Trajectory, RunStats? Stats, PerfData? Perf = null)
{
    public string? VideoPath => Info.HasVideo && File.Exists(Path.Combine(Folder, ReplayFolder.VideoFile))
        ? Path.Combine(Folder, ReplayFolder.VideoFile) : null;
}

public static class ReplayStore
{
    public static void WriteInfo(string folder, ReplayInfo info) =>
        SignedFile.Write(Path.Combine(folder, ReplayFolder.InfoFile), Encoding.UTF8.GetBytes(info.ToJson()));

    /// <summary>Reads the signed session.dat; a tampered one throws <see cref="ReplayFormatException"/>. A pre-1.3.0 plain session.json is read once and upgraded in place.</summary>
    public static ReplayInfo ReadInfo(string folder)
    {
        var path = Path.Combine(folder, ReplayFolder.InfoFile);
        if (File.Exists(path))
            return SignedFile.TryRead(path) is { } b ? ReplayInfo.FromJson(Encoding.UTF8.GetString(b))
                : throw new ReplayFormatException("session.dat is damaged or was modified.");
        var legacy = Path.Combine(folder, ReplayFolder.LegacyInfoFile);
        var info = ReplayInfo.FromJson(File.ReadAllText(legacy));
        WriteInfo(folder, info);
        File.Delete(legacy);
        return info;
    }

    /// <summary>Complete replay folders (those with session.dat) of the given roots, newest first; unreadable ones are skipped.</summary>
    public static List<(string Folder, ReplayInfo Info)> List(params string[] roots)
    {
        var list = new List<(string, ReplayInfo)>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.GetDirectories(root))
            {
                try
                {
                    var info = ReadInfo(dir);
                    // a deleted or missing video file counts as no video
                    list.Add((dir, info.HasVideo && !File.Exists(Path.Combine(dir, ReplayFolder.VideoFile)) ? info with { HasVideo = false } : info));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReplayFormatException or System.Text.Json.JsonException) { }
            }
        }
        return list.OrderByDescending(s => s.Item2.Start).ToList();
    }

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

    /// <summary>Whether <see cref="FindRun"/> finds a replay of the run that has a video (a replay kept in the archive has only its graphs).</summary>
    public static bool HasVideo(IReadOnlyList<ReplayInfo> replays, string scenario, DateTime end) =>
        FindRun(replays, scenario, end) is var i && i >= 0 && replays[i].HasVideo;

    /// <summary>
    /// Moves a replay folder to <paramref name="archiveRoot"/> without its video: trajectory, perf, CSV and replay info stay, so the run's graphs still show.
    /// Returns the new folder, or null when the move failed (a file in use); the original is then left as it was.
    /// </summary>
    public static string? Retire(string folder, string archiveRoot)
    {
        var target = Path.Combine(archiveRoot, Path.GetFileName(folder));
        try
        {
            Directory.CreateDirectory(archiveRoot);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            foreach (var f in Directory.GetFiles(folder))
                if (!Path.GetFileName(f).Equals(ReplayFolder.VideoFile, StringComparison.OrdinalIgnoreCase))
                    File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
            if (File.Exists(Path.Combine(target, ReplayFolder.InfoFile))) WriteInfo(target, ReadInfo(target) with { HasVideo = false });
            Directory.Delete(folder, true);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>
    /// Retires the replay of the run (see <see cref="Retire"/>) after its statistics are stored in <paramref name="store"/>; when storing fails (<see cref="IOException"/>)
    /// the replay stays where it is. False when the run has no replay or it could not be moved.
    /// </summary>
    public static bool Delete(string root, string archiveRoot, string scenario, DateTime end, StatsStore store)
    {
        var all = List(root);
        var i = FindRun(all.Select(s => s.Info).ToList(), scenario, end);
        if (i < 0) return false;
        store.Archive(ReadRecord(all[i].Folder));
        return Retire(all[i].Folder, archiveRoot) != null;
    }

    /// <summary>The statistics of the replay's CSV copy, perf and trajectory (a stats-folder scan yields all but the speed), or null when the folder has no readable CSV.</summary>
    public static RunRecord? ReadRecord(string folder)
    {
        try
        {
            var csv = Directory.GetFiles(folder, "*Stats.csv").FirstOrDefault();
            if (csv is null) return null;
            PerfSummary? perf = null;
            if (Directory.GetFiles(folder, ReplayFolder.PerfPattern).FirstOrDefault() is { } p)
            {
                try { perf = PerfSummary.From(PerfParser.Parse(File.ReadAllBytes(p))); }
                catch (PerfFormatException) { }
            }
            var trajPath = Path.Combine(folder, ReplayFolder.TrajectoryFile);
            var speed = File.Exists(trajPath) ? ReadSpeed(trajPath) : null;
            return RunRecord.From(StatsCsvParser.Parse(csv, File.ReadAllText(csv)), perf) with { Speed = speed };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>The mouse-speed figures of a trajectory file; null when it is unreadable or too short to analyse.</summary>
    public static RunSpeed? ReadSpeed(string trajectoryPath)
    {
        try { return MouseSpeedStats.Analyze(TrajectorySerializer.Deserialize(File.ReadAllBytes(trajectoryPath))); }
        catch (TrajectoryFormatException) { return null; }
    }

    /// <summary>Throws <see cref="ReplayFormatException"/> / <see cref="TrajectoryFormatException"/> with the reason on unsupported versions.</summary>
    public static LoadedReplay Load(string folder)
    {
        var info = ReadInfo(folder);
        var trajPath = Path.Combine(folder, ReplayFolder.TrajectoryFile);
        var traj = File.Exists(trajPath) ? TrajectorySerializer.Deserialize(File.ReadAllBytes(trajPath)) : null;
        var csv = Directory.GetFiles(folder, "*Stats.csv").FirstOrDefault();
        var stats = csv is null ? null : StatsCsvParser.Parse(csv, File.ReadAllText(csv));
        PerfData? perf = null;
        if (Directory.GetFiles(folder, ReplayFolder.PerfPattern).FirstOrDefault() is { } perfPath)
        {
            try { perf = PerfParser.Parse(File.ReadAllBytes(perfPath)); }
            catch (PerfFormatException) { } // optional data: the replay still opens without it
        }
        return new LoadedReplay(folder, info, traj, stats, perf);
    }
}
