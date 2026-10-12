using System.Text;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.Core.Session;

public sealed record LoadedSession(string Folder, SessionInfo Info, TrajectoryData? Trajectory, RunStats? Stats, PerfData? Perf = null)
{
    public string? VideoPath => Info.HasVideo && File.Exists(Path.Combine(Folder, SessionFolder.VideoFile))
        ? Path.Combine(Folder, SessionFolder.VideoFile) : null;
}

public static class SessionStore
{
    public static void WriteInfo(string folder, SessionInfo info) =>
        SignedFile.Write(Path.Combine(folder, SessionFolder.InfoFile), Encoding.UTF8.GetBytes(info.ToJson()));

    /// <summary>Reads the signed session.dat; a tampered one throws <see cref="SessionFormatException"/>. A pre-1.3.0 plain session.json is read once and upgraded in place.</summary>
    public static SessionInfo ReadInfo(string folder)
    {
        var path = Path.Combine(folder, SessionFolder.InfoFile);
        if (File.Exists(path))
            return SignedFile.TryRead(path) is { } b ? SessionInfo.FromJson(Encoding.UTF8.GetString(b))
                : throw new SessionFormatException("session.dat is damaged or was modified.");
        var legacy = Path.Combine(folder, SessionFolder.LegacyInfoFile);
        var info = SessionInfo.FromJson(File.ReadAllText(legacy));
        WriteInfo(folder, info);
        File.Delete(legacy);
        return info;
    }

    /// <summary>Complete session folders (those with session.dat), newest first; unreadable ones are skipped.</summary>
    public static List<(string Folder, SessionInfo Info)> List(string root)
    {
        var list = new List<(string, SessionInfo)>();
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try { list.Add((dir, ReadInfo(dir))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SessionFormatException or System.Text.Json.JsonException) { }
        }
        return list.OrderByDescending(s => s.Item2.Start).ToList();
    }

    /// <summary>Max gap between a stats-folder run's end and a session's end for both to be the same run.</summary>
    public static readonly TimeSpan RunMatchTolerance = TimeSpan.FromSeconds(60);

    /// <summary>Index of the <paramref name="scenario"/> session ending nearest <paramref name="end"/> within <see cref="RunMatchTolerance"/>, else -1.</summary>
    public static int FindRun(IReadOnlyList<SessionInfo> sessions, string scenario, DateTime end)
    {
        var best = -1;
        var bestGap = RunMatchTolerance;
        for (var i = 0; i < sessions.Count; i++)
        {
            if (!sessions[i].Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)) continue;
            var gap = (sessions[i].End - end).Duration();
            if (gap < bestGap) { best = i; bestGap = gap; }
        }
        return best;
    }

    /// <summary>
    /// Permanently deletes the session folder (videos, trajectory, session.dat, CSV and perf copies) of the run; false when it has none.
    /// <paramref name="record"/> is the run's statistics read from the folder before it is gone (null when unreadable).
    /// </summary>
    public static bool Delete(string root, string scenario, DateTime end, out RunRecord? record)
    {
        record = null;
        var all = List(root);
        var i = FindRun(all.Select(s => s.Info).ToList(), scenario, end);
        if (i < 0) return false;
        record = ReadRecord(all[i].Folder);
        Directory.Delete(all[i].Folder, true);
        return true;
    }

    /// <summary>The statistics of the session's CSV copy, perf and trajectory (a stats-folder scan yields all but the speed), or null when the folder has no readable CSV.</summary>
    public static RunRecord? ReadRecord(string folder)
    {
        try
        {
            var csv = Directory.GetFiles(folder, "*Stats.csv").FirstOrDefault();
            if (csv is null) return null;
            PerfSummary? perf = null;
            if (Directory.GetFiles(folder, SessionFolder.PerfPattern).FirstOrDefault() is { } p)
            {
                try { perf = PerfSummary.From(PerfParser.Parse(File.ReadAllBytes(p))); }
                catch (PerfFormatException) { }
            }
            RunSpeed? speed = null;
            var trajPath = Path.Combine(folder, SessionFolder.TrajectoryFile);
            if (File.Exists(trajPath))
            {
                try { speed = MouseSpeedStats.Analyze(TrajectorySerializer.Deserialize(File.ReadAllBytes(trajPath))); }
                catch (TrajectoryFormatException) { }
            }
            return RunRecord.From(StatsCsvParser.Parse(csv, File.ReadAllText(csv)), perf) with { Speed = speed };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>Throws <see cref="SessionFormatException"/> / <see cref="TrajectoryFormatException"/> with the reason on unsupported versions.</summary>
    public static LoadedSession Load(string folder)
    {
        var info = ReadInfo(folder);
        var trajPath = Path.Combine(folder, SessionFolder.TrajectoryFile);
        var traj = File.Exists(trajPath) ? TrajectorySerializer.Deserialize(File.ReadAllBytes(trajPath)) : null;
        var csv = Directory.GetFiles(folder, "*Stats.csv").FirstOrDefault();
        var stats = csv is null ? null : StatsCsvParser.Parse(csv, File.ReadAllText(csv));
        PerfData? perf = null;
        if (Directory.GetFiles(folder, SessionFolder.PerfPattern).FirstOrDefault() is { } perfPath)
        {
            try { perf = PerfParser.Parse(File.ReadAllBytes(perfPath)); }
            catch (PerfFormatException) { } // optional data: the session still opens without it
        }
        return new LoadedSession(folder, info, traj, stats, perf);
    }
}
