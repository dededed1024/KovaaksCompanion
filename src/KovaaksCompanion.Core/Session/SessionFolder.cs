namespace KovaaksCompanion.Core.Session;

public static class SessionFolder
{
    public const string TrajectoryFile = "trajectory.bin", InfoFile = "session.json", VideoFile = "video.mp4";

    /// <summary>"yyyyMMdd-HHmmss Scenario" with characters illegal in file names replaced by '_'.</summary>
    public static string Name(DateTime start, string scenario)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(scenario.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{start:yyyyMMdd-HHmmss} {safe}";
    }
}
