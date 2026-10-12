namespace KovaaksCompanion.Core.Session;

public static class SessionFolder
{
    public const string TrajectoryFile = "trajectory.bin", InfoFile = "session.dat", LegacyInfoFile = "session.json", VideoFile = "video.mp4", PerfPattern = "*Performance.perf";

    /// <summary>"yyyyMMdd-HHmmss_Score_[PB_]Scenario" with characters illegal in file names replaced by '_'. Older folders are "yyyyMMdd-HHmmss Scenario"; nothing parses the name, so both coexist.</summary>
    public static string Name(DateTime start, string scenario, double score, bool personalBest)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(scenario.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{start:yyyyMMdd-HHmmss}_{score.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}_{(personalBest ? "PB_" : "")}{safe}";
    }
}
