using System.Text.Json;
using System.Text.Json.Serialization;

namespace KovaaksCompanion.Core;

/// <summary>APP-003 settings, stored as JSON in %LOCALAPPDATA%\KovaaksCompanion\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>Empty = auto-detect (Steam libraries).</summary>
    public string KovaaksPath { get; init; } = "";
    /// <summary>Parent location of the data root: sessions and buffer live in its "KovaaksCompanion" subfolder. Empty = the root of the disk holding the KovaaK's folder.</summary>
    public string DataFolder { get; init; } = "";
    /// <summary>SteamID64 used for server scores. Empty = detect from Steam's loginusers.vdf.</summary>
    public string SteamId { get; init; } = "";
    public bool StartWithWindows { get; init; }
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

    /// <summary>Maximum size of the sessions folder in GB; 0 = unlimited.</summary>
    public int StorageLimitGb { get; init; }
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
    [JsonIgnore] public string SessionsFolder => Path.Combine(DataRoot, "sessions");
    [JsonIgnore] public string UiFile => Path.Combine(DataRoot, "ui.json");
    [JsonIgnore] public string LiveRunFile => Path.Combine(DataRoot, "live-runs.json");
    /// <summary>Cache and playlist-scenarios data live in LocalAppData, not in the user-chosen data root.</summary>
    [JsonIgnore] public string CacheFolder => Path.Combine(DefaultDataFolder, "cache");
    [JsonIgnore] public string PlaylistIndexFile => Path.Combine(DefaultDataFolder, "playlist-scenarios.dat");
    /// <summary>Pre-1.5 locations, removed after the first successful rebuild.</summary>
    [JsonIgnore] public string LegacyCacheFolder => Path.Combine(DataRoot, "cache");
    [JsonIgnore] public string LegacyPlaylistIndexFile => Path.Combine(DataRoot, "playlist-scenarios.dat");
    [JsonIgnore] public string BufferFolder => Path.Combine(DataRoot, "buffer");
    [JsonIgnore] public string CapturesFolder => Path.Combine(DataRoot, "captures");

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
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultFile), Options) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }
}
