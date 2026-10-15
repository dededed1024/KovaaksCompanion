using System.Text.Json;

namespace KovaaksCompanion.Core.Replays;

public sealed partial class ReplayArchive
{
    // ---- earlier folders -----------------------------------------------------------------------------------------------

    /// <summary>The replay folders of earlier versions in <paramref name="roots"/> that are not imported yet.</summary>
    static IEnumerable<string> LegacyDirs(string[] roots) =>
        roots.Where(Directory.Exists).SelectMany(Directory.GetDirectories)
            .Where(d => !d.EndsWith(Migrated.Suffix, StringComparison.OrdinalIgnoreCase) && LegacyReplays.IsReplay(d));

    /// <summary>The infos of the replay folders of earlier versions in <paramref name="roots"/> that <see cref="MigrateLegacy"/> would import; unreadable ones are left out.</summary>
    public List<ReplayInfo> LegacyInfos(params string[] roots)
    {
        var list = new List<ReplayInfo>();
        foreach (var dir in LegacyDirs(roots))
        {
            try { list.Add(LegacyReplays.ReadInfo(dir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReplayFormatException or JsonException) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("legacy replay import", e); }
        }
        return list;
    }

    /// <summary>
    /// Imports the replay folders of earlier versions found in <paramref name="roots"/>: info, trajectory, perf and CSV become a data file and a row, the video moves to
    /// the videos folder, into the folder <paramref name="folderOf"/> names for it (null = the videos folder itself). The folder is renamed <c>*.migrated</c> only when all of that worked, never deleted; a folder that fails is tried again
    /// next time. Returns how many folders were imported.
    /// </summary>
    public int MigrateLegacy(Func<ReplayInfo, string?> folderOf, params string[] roots)
    {
        var n = 0;
        foreach (var dir in LegacyDirs(roots))
        {
            try { MigrateFolder(dir, folderOf); n++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReplayFormatException or JsonException)
            {
                Diagnostics.AppLog.Write("archive", $"importing replay folder {Path.GetFileName(dir)} failed: {e.Message}");
            }
        }
        return n;
    }

    void MigrateFolder(string dir, Func<ReplayInfo, string?> folderOf)
    {
        var old = LegacyReplays.ReadInfo(dir);
        var videoSrc = Path.Combine(dir, ReplayFolder.VideoFile);
        var hasVideo = File.Exists(videoSrc);

        ReplayInfo info;
        if (_db.FindReplay(old.Scenario, old.Start, old.End) is { } json) info = ReplayInfo.FromJson(json); // a crash left the row but not the folder rename
        else
        {
            info = old with { Id = Guid.NewGuid(), VideoFile = hasVideo ? NewVideoFile(folderOf(old) ?? "", old.Start, old.Scenario, old.Score, false) : null };
            Add(info, LegacyReplays.ReadParts(dir));
        }
        if (hasVideo && info.VideoFile is { } file && !File.Exists(VideoPathOf(file)))
        {
            EnsureVideoFolder(file);
            File.Move(videoSrc, VideoPathOf(file));
        }

        Directory.Move(dir, Migrated.Name(dir));
    }
}
