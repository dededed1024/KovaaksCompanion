using System.Text;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Replays;

/// <summary>Reads the replay folders of earlier versions (session.dat, trajectory.bin, the CSV and perf copies), for <see cref="ReplayArchive.MigrateLegacy"/>.</summary>
static class LegacyReplays
{
    public const string TrajectoryFile = "trajectory.bin", InfoFile = "session.dat", PlainInfoFile = "session.json", PerfPattern = "*Performance.perf";

    public static bool IsReplay(string folder) => File.Exists(Path.Combine(folder, InfoFile)) || File.Exists(Path.Combine(folder, PlainInfoFile));

    /// <summary>The signed session.dat; a tampered one throws <see cref="ReplayFormatException"/>. A plain session.json of a version before 1.3.0 is read as it is.</summary>
    public static ReplayInfo ReadInfo(string folder)
    {
        var path = Path.Combine(folder, InfoFile);
        if (File.Exists(path))
            return SignedFile.TryRead(path) is { } b ? ReplayInfo.FromJson(Encoding.UTF8.GetString(b))
                : throw new ReplayFormatException("session.dat is damaged or was modified.");
        return ReplayInfo.FromJson(File.ReadAllText(Path.Combine(folder, PlainInfoFile)));
    }

    public static RepParts ReadParts(string folder)
    {
        var traj = Path.Combine(folder, TrajectoryFile);
        var csv = Directory.GetFiles(folder, "*Stats.csv").FirstOrDefault();
        var perf = Directory.GetFiles(folder, PerfPattern).FirstOrDefault();
        return new(File.Exists(traj) ? File.ReadAllBytes(traj) : null, perf is null ? null : File.ReadAllBytes(perf), csv is null ? null : ReadStats(csv));
    }

    static RepStats? ReadStats(string csv)
    {
        try { return RepStats.From(StatsCsvParser.Parse(csv, File.ReadAllText(csv))); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; } // a CSV that cannot be read is left out
    }
}
