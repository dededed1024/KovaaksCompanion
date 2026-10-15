using System.Text.Json;
using System.Text.Json.Serialization;

namespace KovaaksCompanion.Core;

/// <summary>APP-003 settings, stored as JSON in %LOCALAPPDATA%\KovaaksCompanion\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>Empty = auto-detect (Steam libraries).</summary>
    public string KovaaksPath { get; init; } = "";
    /// <summary>Parent location of the data root: replays and buffer live in its "KovaaksCompanion" subfolder. Empty = the root of the disk holding the KovaaK's folder.</summary>
    public string DataFolder { get; init; } = "";
    /// <summary>SteamID64 used for cloud scores. Empty = detect from Steam's loginusers.vdf.</summary>
    public string SteamId { get; init; } = "";
    /// <summary>The player's KovaaK's webapp username: the Steam display name of <paramref name="steamId"/> from Steam's loginusers.vdf. Null when unknown.</summary>
    public string? UsernameFor(string steamId) => Benchmarks.SteamAccount.Profile(KovaaksPath, steamId).Name;
    public bool StartWithWindows { get; init; } = true;
    /// <summary>UI language code ("en", "ko"); empty = follow Windows.</summary>
    public string Language { get; init; } = "";
    /// <summary>Color theme name (surfaces and accent together): Dark, Light, Black, Violet, Rose, Forest, Ember or Ocean.</summary>
    public string Theme { get; init; } = "Dark";
    /// <summary>UI size in percent of the design size.</summary>
    public int UiScale { get; init; } = 100;
    public const int MinUiScale = 75, MaxUiScale = 150;
    /// <summary>Posts unhandled errors (no personal data) to the developer.</summary>
    public bool SendErrorReports { get; init; } = true;
    public Video.VideoQuality VideoQuality { get; init; } = Video.VideoQuality.High;
    /// <summary>Maximum recording frame rate.</summary>
    public int VideoFps { get; init; } = Video.VideoOptions.DefaultFps;

    /// <summary>Keep the video only of runs that set a personal best; the clip of any other run is not saved (its stats and mouse trail still are).</summary>
    public bool VideoPbOnly { get; init; }

    /// <summary>Maximum size of the replays folder in GB; 0 = unlimited.</summary>
    public int StorageLimitGb { get; init; }
    /// <summary>Maximum age of a replay in days; older ones are deleted. 0 = unlimited.</summary>
    public int MaxAgeDays { get; init; }
    /// <summary>Delete the replay of an earlier run when a later run of the same scenario in the same play session scores higher.</summary>
    public bool DeleteBeatenRuns { get; init; }
    /// <summary>Never delete the replay of a personal best, whatever the cleanup rules say.</summary>
    public bool KeepPersonalBests { get; init; } = true;

    public bool WebcamEnabled { get; init; }
    [JsonConverter(typeof(Video.WebcamSaveConverter))]
    public Video.WebcamSave WebcamSave { get; init; } = Video.WebcamSave.Composite;
    /// <summary>Up to <see cref="Video.WebcamSlot.Max"/> webcams recorded alongside the game.</summary>
    public Video.WebcamSlots Webcams { get; init; } = [];
    /// <summary>Size of the game window at the last recording (settings preview shape); 0 = never recorded.</summary>
    public int GameWidth { get; init; }
    public int GameHeight { get; init; }

    public static string DefaultDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KovaaksCompanion");

    public static string DefaultFile => Path.Combine(DefaultDataFolder, "settings.json");

    [JsonIgnore] public string StatsFolder => Path.Combine(KovaaksPath, "FPSAimTrainer", "stats");
    [JsonIgnore] public string EffectiveSteamId => SteamId.Trim().Length > 0 ? SteamId.Trim() : Benchmarks.SteamAccount.Detect(KovaaksPath) ?? "";
    /// <summary>Disk root of <see cref="KovaaksPath"/> (e.g. D:\), else the LocalAppData folder when KovaaK's is unknown.</summary>
    string DefaultDataParent
    {
        get
        {
            var root = KovaaksPath.Trim().Length > 0 ? Path.GetPathRoot(KovaaksPath.Trim()) : null;
            return string.IsNullOrEmpty(root) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : root;
        }
    }

    /// <summary><see cref="DataFolder"/>\KovaaksCompanion; a value already ending in that name (legacy files) is used as-is, empty = default.</summary>
    [JsonIgnore] public string DataRoot
    {
        get
        {
            var d = string.IsNullOrWhiteSpace(DataFolder) ? DefaultDataParent : DataFolder.Trim();
            var last = Path.GetFileName(d.TrimEnd((char)92, '/'));
            return string.Equals(last, "KovaaksCompanion", StringComparison.OrdinalIgnoreCase) ? d : Path.Combine(d, "KovaaksCompanion");
        }
    }
    [JsonIgnore] public string ReplaysFolder => Path.Combine(DataRoot, "sessions");
    [JsonIgnore] public string UiFile => Path.Combine(DefaultDataFolder, "ui.json");
    /// <summary>The archive database: run statistics and cloud scores that are kept for good, and the session id of every run.</summary>
    [JsonIgnore] public string ArchiveDbFile => Path.Combine(ArchiveFolder, Benchmarks.StatsStore.ArchiveDbFile);
    /// <summary>The earlier session id file, imported into <see cref="ArchiveDbFile"/> once.</summary>
    string LegacySessionsFile => Path.Combine(ArchiveFolder, "sessions.dat");
    /// <summary>
    /// Cache: data the server gives again (server bests). Deleting it loses nothing. Lives in LocalAppData, not in the user-chosen data root.
    /// </summary>
    [JsonIgnore] public string CacheFolder => Path.Combine(DefaultDataFolder, "cache");
    /// <summary>
    /// Archive: what only this PC knows (run statistics, session ids, the kept data of deleted replays). Deleting it loses runs for good. Lives in LocalAppData next to <see cref="CacheFolder"/>.
    /// </summary>
    [JsonIgnore] public string ArchiveFolder => Path.Combine(DefaultDataFolder, "archive");
    /// <summary>Where earlier versions kept the replay data of deleted videos, one folder each; <see cref="Replays.ReplayArchive.MigrateLegacy"/> imports them.</summary>
    [JsonIgnore] public string ArchiveReplaysFolder => Path.Combine(ArchiveFolder, "sessions");
    // CacheFolder and ArchiveFolder do not depend on the settings, so one store and one speed cache serve every caller (and every copy of the settings).
    static readonly Lazy<Benchmarks.StatsStore> SharedStore = new(() => new(Path.Combine(DefaultDataFolder, "archive")));
    static readonly Lazy<Trajectory.SpeedCache> SharedSpeedCache = new(() => new(Path.Combine(DefaultDataFolder, "cache", "speed" + CacheFile.Extension)));
    public Benchmarks.StatsStore OpenStore() => SharedStore.Value;
    /// <summary>The replays: their data in the archive folder, their videos in <see cref="ReplaysFolder"/>. It holds nothing but the folders (the database handle is shared), so a new one per call is cheap and follows a changed <see cref="ReplaysFolder"/>.</summary>
    public Replays.ReplayArchive OpenReplays() => new(ArchiveFolder, ReplaysFolder);
    public Trajectory.SpeedCache OpenSpeedCache() => SharedSpeedCache.Value;
    static readonly Lazy<Perf.SeriesCache> SharedSeriesCache = new(() => new(Path.Combine(DefaultDataFolder, "cache", "series" + CacheFile.Extension)));
    public Perf.SeriesCache OpenSeriesCache() => SharedSeriesCache.Value;
    [JsonIgnore] public string BufferFolder => Path.Combine(DataRoot, "buffer");
    /// <summary>Diagnostic log; lives in the data root next to the replays.</summary>
    [JsonIgnore] public string LogFile => Path.Combine(DataRoot, "logs", "app.log");
    [JsonIgnore] public string ScreenshotsFolder => Path.Combine(DataRoot, "screenshots");
    /// <summary><see cref="ScreenshotsFolder"/>, created (with a UAC grant when the location needs one).</summary>
    public string EnsureScreenshotsFolder()
    {
        var dir = ScreenshotsFolder;
        AccessGuard.Run(dir, true, () => Directory.CreateDirectory(dir));
        return dir;
    }

    /// <summary>
    /// Moves files earlier versions kept elsewhere to where they live now: ui.json out of the data root; runs.dat and the session ids (sessions.dat, once live-runs.json)
    /// from the data root's history folder and from LocalAppData\history to LocalAppData\archive; the kept replay data from cache\sessions and history\sessions to archive\sessions.
    /// Never overwrites; never throws.
    /// </summary>
    public void MigrateLocalState()
    {
        try
        {
            var oldDirs = new[] { Path.Combine(DataRoot, "history"), Path.Combine(DefaultDataFolder, "history") };
            var moves = new List<(string From, string To)> { (Path.Combine(DefaultDataFolder, "live-runs.json"), LegacySessionsFile) };
            foreach (var old in oldDirs)
                moves.AddRange([(Path.Combine(old, "live-runs.json"), LegacySessionsFile), (Path.Combine(old, "sessions.dat"), LegacySessionsFile), (Path.Combine(old, Benchmarks.StatsStore.RunsFile), Path.Combine(ArchiveFolder, Benchmarks.StatsStore.RunsFile))]);
            if (!string.Equals(DataRoot, DefaultDataFolder, StringComparison.OrdinalIgnoreCase))
                moves.AddRange([(Path.Combine(DataRoot, "ui.json"), UiFile), (Path.Combine(DataRoot, "live-runs.json"), LegacySessionsFile)]);
            foreach (var (from, to) in moves)
            {
                if (!File.Exists(from) || File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
            }
            foreach (var oldReplays in new[] { Path.Combine(CacheFolder, "sessions"), Path.Combine(DefaultDataFolder, "history", "sessions") })
            {
                if (!Directory.Exists(oldReplays)) continue;
                foreach (var dir in Directory.GetDirectories(oldReplays))
                {
                    var to = Path.Combine(ArchiveReplaysFolder, Path.GetFileName(dir));
                    if (Directory.Exists(to)) continue;
                    Directory.CreateDirectory(ArchiveReplaysFolder);
                    Directory.Move(dir, to);
                }
            }
            if (Path.Combine(DataRoot, "captures") is var oldShots && Directory.Exists(oldShots) && !Directory.Exists(ScreenshotsFolder)) // the screenshots folder was called captures
                Directory.Move(oldShots, ScreenshotsFolder);
            foreach (var name in new[] { "serverbest.dat", "serverbest" + CacheFile.Extension, "totals" + CacheFile.Extension, "scan" + CacheFile.Extension, "sync" + CacheFile.Extension }) // server bests moved into the archive database; these were only a refetchable cache
                if (Path.Combine(CacheFolder, name) is var stale && File.Exists(stale)) File.Delete(stale);
            foreach (var dir in new[] { Path.Combine(CacheFolder, "sessions"), Path.Combine(DefaultDataFolder, "history", "sessions"), oldDirs[0], oldDirs[1] })
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Diagnostics.AppLog.Write("migrate", "local state move failed: " + e.Message); }
    }

    /// <summary>Fills KovaaksPath from detection when it is empty or missing; unchanged when nothing is found.</summary>
    public AppSettings WithDetectedPaths(Func<string?>? findKovaaks = null, Func<string, bool>? dirExists = null)
    {
        if (KovaaksPath.Length > 0 && (dirExists ?? Directory.Exists)(KovaaksPath)) return this;
        return (findKovaaks ?? PathDetector.FindKovaaks)() is { } k ? this with { KovaaksPath = k } : this;
    }

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Settings files of older versions keep the webcam settings under their former "HandCam" names; they are read under the new names and saved that way.</summary>
    static string RenameLegacyKeys(string json)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(json) is not System.Text.Json.Nodes.JsonObject o) return json;
        foreach (var (old, now) in new[] { ("HandCamEnabled", nameof(WebcamEnabled)), ("HandCamSave", nameof(WebcamSave)), ("HandCams", nameof(Webcams)) })
            if (o[old] is { } v && !o.ContainsKey(now)) { o.Remove(old); o[now] = v; }
        return o.ToJsonString();
    }

    /// <summary>Defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultFile;
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(RenameLegacyKeys(File.ReadAllText(path)), Options) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            if (File.Exists(path)) Diagnostics.ErrorReporter.Report("settings load " + path, e); // unreadable or corrupt, not just first run
            return new();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFile;
        var dir = Path.GetDirectoryName(path)!;
        AccessGuard.Run(dir, true, () => { Directory.CreateDirectory(dir); File.WriteAllText(path, JsonSerializer.Serialize(this, Options)); });
    }
}
