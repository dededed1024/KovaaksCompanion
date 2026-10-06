using System.Text.Json;
using System.Text.Json.Serialization;

namespace KovaaksCompanion.Core;

/// <summary>APP-003 settings, stored as JSON in %LOCALAPPDATA%\KovaaksCompanion\settings.json.</summary>
public sealed record AppSettings
{
    public string KovaaksPath { get; init; } = @"F:\Steam\steamapps\common\FPSAimTrainer";
    public string FfmpegPath { get; init; } = @"C:\ffmpeg\bin\ffmpeg.exe";
    public string DataFolder { get; init; } = DefaultDataFolder;
    /// <summary>SteamID64 used for server scores. Empty = detect from Steam's loginusers.vdf.</summary>
    public string SteamId { get; init; } = "";
    /// <summary>0 = match the game monitor's refresh rate.</summary>
    public int Fps { get; init; } = 0;
    public int BufferMinutes { get; init; } = 10;

    public static string DefaultDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KovaaksCompanion");

    public static string DefaultFile => Path.Combine(DefaultDataFolder, "settings.json");

    [JsonIgnore] public string StatsFolder => Path.Combine(KovaaksPath, "FPSAimTrainer", "stats");
    [JsonIgnore] public string EffectiveSteamId => SteamId.Trim().Length > 0 ? SteamId.Trim() : Benchmarks.SteamAccount.Detect(KovaaksPath) ?? "";
    [JsonIgnore] public string SessionsFolder => Path.Combine(DataFolder, "sessions");
    [JsonIgnore] public string BufferFolder => Path.Combine(DataFolder, "buffer");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Defaults when the file is missing or unreadable; invalid numbers are clamped.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path ?? DefaultFile), Options) ?? new();
            return s with { Fps = s.Fps <= 0 ? 0 : Math.Clamp(s.Fps, 10, 240), BufferMinutes = Math.Clamp(s.BufferMinutes, 2, 120) };
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
