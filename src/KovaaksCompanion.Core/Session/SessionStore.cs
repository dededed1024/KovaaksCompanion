using System.Text;
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
