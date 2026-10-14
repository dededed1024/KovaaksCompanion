namespace KovaaksCompanion.Core.Replays;

public static class ReplayFolder
{
    /// <summary>The video's file name inside the replay folders of earlier versions.</summary>
    public const string VideoFile = "video.mp4";

    /// <summary>"yyyyMMdd-HHmmss_Score_[PB_]Scenario" with characters illegal in file names replaced by '_'. The name of a new video file (plus the extension); nothing parses it.</summary>
    public static string Name(DateTime start, string scenario, double score, bool personalBest)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(scenario.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        return $"{start:yyyyMMdd-HHmmss}_{score.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}_{(personalBest ? "PB_" : "")}{safe}";
    }
}
