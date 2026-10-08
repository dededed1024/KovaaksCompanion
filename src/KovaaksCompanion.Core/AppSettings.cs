using System.Text.Json;
using System.Text.Json.Serialization;

namespace KovaaksCompanion.Core;

/// <summary>APP-003 settings, stored as JSON in %LOCALAPPDATA%\KovaaksCompanion\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>Empty = auto-detect (Steam libraries).</summary>
    public string KovaaksPath { get; init; } = "";
    /// <summary>Parent location of the data root: sessions and buffer live in its "KovaaksCompanion" subfolder.</summary>
    public string DataFolder { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>SteamID64 used for server scores. Empty = detect from Steam's loginusers.vdf.</summary>
    public string SteamId { get; init; } = "";
    public bool StartWithWindows { get; init; }
    public Video.VideoQuality VideoQuality { get; init; } = Video.VideoQuality.High;
    /// <summary>Maximum recording frame rate.</summary>
    public int VideoFps { get; init; } = Video.VideoOptions.DefaultFps;

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
    /// <summary><see cref="DataFolder"/>\KovaaksCompanion; a value already ending in that name (legacy files) is used as-is, empty = default.</summary>
    [JsonIgnore] public string DataRoot
    {
        get
        {
            var d = string.IsNullOrWhiteSpace(DataFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : DataFolder.Trim();
            var last = Path.GetFileName(d.TrimEnd((char)92, '/'));
            return string.Equals(last, "KovaaksCompanion", StringComparison.OrdinalIgnoreCase) ? d : Path.Combine(d, "KovaaksCompanion");
        }
    }
    [JsonIgnore] public string SessionsFolder => Path.Combine(DataRoot, "sessions");
    [JsonIgnore] public string UiFile => Path.Combine(DataRoot, "ui.json");
    [JsonIgnore] public string PlaylistIndexFile => Path.Combine(DataRoot, "playlist-scenarios.dat");
    [JsonIgnore] public string BufferFolder => Path.Combine(DataRoot, "buffer");

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
