namespace KovaaksCompanion.Core.Replays;

public static class ReplayFolder
{
    /// <summary>The video's file name inside the replay folders of earlier versions.</summary>
    public const string VideoFile = "video.mp4";

    /// <summary>"yyyyMMdd-HHmmss_[Tier_]Score_[PB_]Scenario" with characters illegal in file names replaced by '_'. The name of a new video file (plus the extension); nothing parses it.</summary>
    public static string Name(DateTime start, string scenario, double score, bool personalBest, string? tier = null)
    {
        var tierPart = string.IsNullOrWhiteSpace(tier) ? "" : Safe(tier) + "_";
        return $"{start:yyyyMMdd-HHmmss}_{tierPart}{score.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}_{(personalBest ? "PB_" : "")}{Safe(scenario)}";
    }

    static string Safe(string text)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(text.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
