using System.Text.Json;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.Core.Replays;

/// <summary>
/// The replays: each one's info is a row of the archive database, its trajectory, perf and CSV are one <c>&lt;Id&gt;.rep</c> file in the archive folder
/// (see <see cref="RepFile"/>), and its video, when it has one, is a file in the videos folder named by <see cref="ReplayInfo.VideoFile"/>.
/// Deleting a video leaves everything else. A video file renamed or moved by hand counts as deleted.
/// </summary>
public sealed class ReplayArchive(string archiveFolder, string videoFolder)
{
    public const string VideoExtension = ".mp4";

    readonly ArchiveDb _db = ArchiveDb.At(Path.Combine(archiveFolder, ArchiveDb.FileName));

    string RepPath(Guid id) => Path.Combine(archiveFolder, id.ToString("N") + RepFile.Extension);

    /// <summary>Full path of the replay's video, or null when it has none or the file is gone.</summary>
    public string? VideoPath(ReplayInfo info) =>
        info.VideoFile is { Length: > 0 } f && Path.Combine(videoFolder, f) is var p && File.Exists(p) ? p : null;

    // ---- reading -------------------------------------------------------------------------------------------------------

    /// <summary>Every replay, newest first. A replay whose video file is gone is listed without video.</summary>
    public List<ReplayInfo> List()
    {
        var list = new List<ReplayInfo>();
        foreach (var json in _db.ReplayInfos())
        {
            try
            {
                var info = ReplayInfo.FromJson(json);
                list.Add(info.VideoFile is not null && VideoPath(info) is null ? info with { VideoFile = null } : info);
            }
            catch (Exception e) when (e is ReplayFormatException or JsonException) { }
        }
        return list;
    }

    /// <summary>The replay that ended last at or before <paramref name="time"/>; null when there is none.</summary>
    public ReplayInfo? LastEndedBy(DateTime time)
    {
        try { return _db.LastReplayEndedBy(time) is { } json ? ReplayInfo.FromJson(json) : null; }
        catch (Exception e) when (e is ReplayFormatException or JsonException) { return null; }
    }

    /// <summary>Size and write time of the replay's data file, to tell whether something derived from it is current. Throws <see cref="IOException"/> when the file is missing.</summary>
    public FileStamp DataStamp(Guid id) => FileStamp.Of(RepPath(id));

    /// <summary>Throws <see cref="ReplayFormatException"/> when the replay's data file is missing or damaged.</summary>
    public RepParts ReadParts(Guid id)
    {
        try { return RepFile.Unpack(File.ReadAllBytes(RepPath(id))); }
        catch (FileNotFoundException) { throw new ReplayFormatException("The replay data file is missing."); }
    }

    /// <summary>Throws <see cref="ReplayFormatException"/> / <see cref="Trajectory.TrajectoryFormatException"/> with the reason when the replay cannot be read.</summary>
    public LoadedReplay Load(ReplayInfo info) => ReplayStore.Open(info, ReadParts(info.Id), VideoPath(info));

    /// <summary>The statistics of the replay's data; null when it has none or its file cannot be read.</summary>
    public Library.RunRecord? ReadRecord(ReplayInfo info)
    {
        try { return ReplayStore.ReadRecord(info, ReadParts(info.Id)); }
        catch (Exception e) when (e is ReplayFormatException or IOException or UnauthorizedAccessException) { return null; }
    }

    // ---- writing -------------------------------------------------------------------------------------------------------

    /// <summary>Keeps a new replay. The data file is written complete first (a temporary name, then renamed), the row after it: a row always has its file.</summary>
    public void Add(ReplayInfo info, RepParts parts)
    {
        if (info.Id == Guid.Empty) throw new ArgumentException("The replay has no id.", nameof(info));
        var path = RepPath(info.Id);
        AccessGuard.Run(archiveFolder, true, () =>
        {
            Directory.CreateDirectory(archiveFolder);
            AtomicFile.Write(path, RepFile.Pack(parts));
        });
        Update(info);
    }

    /// <summary>Changes the info of a kept replay (its video, the sync nudge).</summary>
    public void Update(ReplayInfo info) => _db.PutReplay(info.Id, info.Scenario, info.Start, info.End, info.ToJson());

    /// <summary>The relative path of a new video: <paramref name="sessionFolder"/> (empty = the videos folder itself), then the run's name. No file has it yet.</summary>
    public string NewVideoFile(string sessionFolder, DateTime start, string scenario, double score, bool personalBest, string? tier = null)
        => FreeVideoFile(Path.Combine(sessionFolder, ReplayFolder.Name(start, scenario, score, personalBest, tier)));

    /// <summary><paramref name="stem"/> (relative, no extension) plus the extension, numbered <c>_2</c>, <c>_3</c>... until no file has the name.</summary>
    string FreeVideoFile(string stem)
    {
        var name = stem + VideoExtension;
        for (var i = 2; File.Exists(VideoPathOf(name)); i++) name = $"{stem}_{i}{VideoExtension}";
        return name;
    }

    /// <summary>Folder name of a play session: <c>yyMMdd_HH-mm-ss_&lt;session id&gt;</c>, the time it began and its id (32 hex digits).</summary>
    public static string SessionFolder(DateTime since, Guid sessionId) => $"{since:yyMMdd_HH-mm-ss}_{sessionId:N}";

    /// <summary>Full path of a video's relative path in the videos folder.</summary>
    public string VideoPathOf(string videoFile) => Path.Combine(videoFolder, videoFile);

    /// <summary>Creates the folder the video file goes in.</summary>
    public void EnsureVideoFolder(string videoFile) => AccessGuard.Run(videoFolder, true, () => Directory.CreateDirectory(Path.GetDirectoryName(VideoPathOf(videoFile))!));

    /// <summary>Deletes the replay's video file and keeps the rest. False when the file is in use; the replay is then unchanged.</summary>
    public bool DeleteVideo(ReplayInfo info)
    {
        if (VideoPath(info) is { } p)
        {
            try { File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        }
        Update(info with { VideoFile = null });
        RemoveEmptyFolder(info.VideoFile);
        return true;
    }

    /// <summary>Removes the session folder of a deleted video when nothing else is in it.</summary>
    void RemoveEmptyFolder(string? videoFile)
    {
        if (Path.GetDirectoryName(videoFile) is not { Length: > 0 } dir) return;
        var path = Path.Combine(videoFolder, dir);
        try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Moves the videos that lie directly in the videos folder into a folder per play session. <paramref name="folderOf"/> names a replay's folder, or null to leave its video where it is.
    /// A video that cannot be moved stays and is tried again next time. Returns how many were moved.
    /// </summary>
    public int MoveVideosIntoSessions(Func<ReplayInfo, string?> folderOf)
    {
        var n = 0;
        foreach (var info in List())
        {
            if (info.VideoFile is not { } file || Path.GetDirectoryName(file) is { Length: > 0 }) continue;
            try
            {
                if (folderOf(info) is not { } folder) continue;
                var to = FreeVideoFile(Path.Combine(folder, Path.GetFileNameWithoutExtension(file))); // keeps the name it has
                EnsureVideoFolder(to);
                File.Move(VideoPathOf(file), VideoPathOf(to));
                Update(info with { VideoFile = to });
                n++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Diagnostics.AppLog.Write("archive", $"moving video {file} into its session folder failed: {e.Message}");
            }
        }
        return n;
    }

    // ---- earlier folders -----------------------------------------------------------------------------------------------

    /// <summary>The replay folders of earlier versions in <paramref name="roots"/> that are not imported yet.</summary>
    static IEnumerable<string> LegacyDirs(string[] roots) =>
        roots.Where(Directory.Exists).SelectMany(Directory.GetDirectories)
            .Where(d => !d.EndsWith(".migrated", StringComparison.OrdinalIgnoreCase) && LegacyReplays.IsReplay(d));

    /// <summary>The infos of the replay folders of earlier versions in <paramref name="roots"/> that <see cref="MigrateLegacy"/> would import; unreadable ones are left out.</summary>
    public List<ReplayInfo> LegacyInfos(params string[] roots)
    {
        var list = new List<ReplayInfo>();
        foreach (var dir in LegacyDirs(roots))
        {
            try { list.Add(LegacyReplays.ReadInfo(dir)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ReplayFormatException or JsonException) { }
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

        Directory.Move(dir, ArchiveDb.MigratedName(dir));
    }
}
