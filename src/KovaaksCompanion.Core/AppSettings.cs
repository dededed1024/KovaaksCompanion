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
    /// <summary>SteamID64 used for server scores. Empty = detect from Steam's loginusers.vdf.</summary>
    public string SteamId { get; init; } = "";
    /// <summary>Webapp username of <see cref="WebUsernameSteamId"/>, resolved once from the server (finding it takes several requests).</summary>
    public string WebUsername { get; init; } = "";
    public string WebUsernameSteamId { get; init; } = "";
    /// <summary>The resolved webapp username when it belongs to <paramref name="steamId"/>, else null.</summary>
    public string? UsernameFor(string steamId) => WebUsername.Length > 0 && WebUsernameSteamId == steamId ? WebUsername : null;
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

    public bool HandCamEnabled { get; init; }
    [JsonConverter(typeof(Video.HandCamSaveConverter))]
    public Video.HandCamSave HandCamSave { get; init; } = Video.HandCamSave.Composite;
    /// <summary>Up to <see cref="Video.HandCamSlot.Max"/> hand-cams recorded alongside the game.</summary>
    public Video.HandCamSlots HandCams { get; init; } = [];

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
    /// <summary>Session ids per run, signed like runs.dat and kept next to it.</summary>
    [JsonIgnore] public string LiveRunFile => Path.Combine(RunsCacheFolder, "sessions.dat");
    /// <summary>Cache data lives in LocalAppData, not in the user-chosen data root.</summary>
    [JsonIgnore] public string CacheFolder => Path.Combine(DefaultDataFolder, "cache");
    /// <summary>Deleted runs keep their replay data here (everything but the video), so their graphs still show.</summary>
    [JsonIgnore] public string CachedReplaysFolder => Path.Combine(CacheFolder, "sessions");
    /// <summary>The run history cache (runs.dat) lives with the other caches in LocalAppData; the data root only holds output.</summary>
    [JsonIgnore] public string RunsCacheFolder => Path.Combine(DefaultDataFolder, "history");
    public Benchmarks.StatsCache OpenCache() => new(CacheFolder, RunsCacheFolder);
    [JsonIgnore] public string BufferFolder => Path.Combine(DataRoot, "buffer");
    /// <summary>Diagnostic log; lives in the data root next to the replays.</summary>
    [JsonIgnore] public string LogFile => Path.Combine(DataRoot, "logs", "app.log");
    [JsonIgnore] public string CapturesFolder => Path.Combine(DataRoot, "captures");
    /// <summary><see cref="CapturesFolder"/>, created (with a UAC grant when the location needs one).</summary>
    public string EnsureCapturesFolder()
    {
        var dir = CapturesFolder;
        AccessGuard.Run(dir, true, () => Directory.CreateDirectory(dir));
        return dir;
    }

    /// <summary>
    /// Moves files earlier versions kept elsewhere to where they live now: ui.json, history\runs.dat and the session cache (once live-runs.json, now history\sessions.dat)
    /// out of the data root, and the session cache out of the LocalAppData root. Never overwrites; never throws.
    /// </summary>
    public void MigrateLocalState()
    {
        try
        {
            var history = Path.Combine(DataRoot, "history");
            var moves = new List<(string From, string To)> { (Path.Combine(DefaultDataFolder, "live-runs.json"), LiveRunFile), (Path.Combine(history, "live-runs.json"), LiveRunFile) };
            if (!string.Equals(DataRoot, DefaultDataFolder, StringComparison.OrdinalIgnoreCase))
                moves.AddRange([
                    (Path.Combine(DataRoot, "ui.json"), UiFile),
                    (Path.Combine(DataRoot, "live-runs.json"), LiveRunFile),
                    (Path.Combine(history, Benchmarks.StatsCache.RunsFile), Path.Combine(RunsCacheFolder, Benchmarks.StatsCache.RunsFile)),
                ]);
            foreach (var (from, to) in moves)
            {
                if (!File.Exists(from) || File.Exists(to)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Move(from, to);
            }
            if (!string.Equals(history, RunsCacheFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(history) && !Directory.EnumerateFileSystemEntries(history).Any()) Directory.Delete(history);
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

    /// <summary>Defaults when the file is missing or unreadable.</summary>
    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultFile;
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new();
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
