using System.Text.RegularExpressions;

namespace KovaaksCompanion.Core.Video;

/// <summary>Removes the rolling-buffer chunks a crashed or killed recording leaves behind. Only files that match the app's own chunk names are deleted.</summary>
public static partial class BufferCleaner
{
    [GeneratedRegex(@"^kc_[0-9a-f]+_\d+\.chunk$", RegexOptions.IgnoreCase)] private static partial Regex GameSegment();
    [GeneratedRegex(@"^kc_[0-9a-f]+_\d+\.chunk$", RegexOptions.IgnoreCase)] private static partial Regex CamSegment();
    [GeneratedRegex(@"^cam\d+$", RegexOptions.IgnoreCase)] private static partial Regex CamFolder();

    /// <summary>Call only while nothing records into <paramref name="bufferDir"/>. Returns the number of files deleted; files in use are skipped.</summary>
    public static int Clean(string bufferDir)
    {
        var n = 0;
        try
        {
            if (!Directory.Exists(bufferDir)) return 0;
            n += DeleteMatching(bufferDir, GameSegment());
            foreach (var d in Directory.GetDirectories(bufferDir).Where(d => CamFolder().IsMatch(Path.GetFileName(d))))
            {
                n += DeleteMatching(d, CamSegment());
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.ErrorReporter.Report("buffer dir delete", e, Diagnostics.ErrorSeverity.Low); }
            }
            if (!Directory.EnumerateFileSystemEntries(bufferDir).Any()) Directory.Delete(bufferDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.ErrorReporter.Report("buffer cleanup", e, Diagnostics.ErrorSeverity.Low); }
        return n;
    }

    static int DeleteMatching(string dir, Regex name)
    {
        var n = 0;
        foreach (var f in Directory.GetFiles(dir).Where(f => name.IsMatch(Path.GetFileName(f))))
        {
            try { File.Delete(f); n++; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.ErrorReporter.Report("buffer file delete", e, Diagnostics.ErrorSeverity.Low); }
        }
        return n;
    }
}
