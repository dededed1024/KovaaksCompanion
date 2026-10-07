using System.Text.Json;

namespace KovaaksCompanion.Core;

/// <summary>Small per-user UI state (favorite playlists, visible run-chart series) in ui.json next to settings.json. Kept apart from <see cref="AppSettings"/> so saving settings never overwrites it.</summary>
public sealed class UiState
{
    public static readonly string[] DefaultSeries = ["Accuracy", "Score"];

    public static string DefaultFile => Path.Combine(AppSettings.DefaultDataFolder, "ui.json");

    readonly HashSet<string> _favs = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> FavoritePlaylists => _favs;
    public IReadOnlyList<string> VisibleSeries { get; set; } = DefaultSeries;
    public bool ShowTrail { get; set; } = true;
    /// <summary>Playlist chart view: "Progress" or "Radar".</summary>
    public string StatsChart { get; set; } = "Progress";
    /// <summary>Radar axes: "Categories", "Subcategories" or "Scenarios".</summary>
    public string RadarAxes { get; set; } = "Categories";
    public bool RadarCompare { get; set; }

    public bool IsFavorite(string playlist) => _favs.Contains(playlist);

    /// <summary>Flips the favorite flag and returns the new state.</summary>
    public bool ToggleFavorite(string playlist)
    {
        if (_favs.Remove(playlist)) return false;
        _favs.Add(playlist);
        return true;
    }

    sealed record Dto(string[]? FavoritePlaylists, string[]? VisibleSeries, bool? ShowTrail = null, string? StatsChart = null, string? RadarAxes = null, bool? RadarCompare = null);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Defaults when the file is missing or unreadable.</summary>
    public static UiState Load(string? path = null)
    {
        var s = new UiState();
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path ?? DefaultFile), Options);
            if (d?.FavoritePlaylists is { } f) foreach (var n in f) s._favs.Add(n);
            if (d?.VisibleSeries is { } v) s.VisibleSeries = v;
            if (d?.ShowTrail is { } t) s.ShowTrail = t;
            if (d?.StatsChart is { } sc) s.StatsChart = sc;
            if (d?.RadarAxes is { } ra) s.RadarAxes = ra;
            if (d?.RadarCompare is { } rc) s.RadarCompare = rc;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dto(_favs.Order(StringComparer.OrdinalIgnoreCase).ToArray(), VisibleSeries.ToArray(), ShowTrail, StatsChart, RadarAxes, RadarCompare), Options));
    }
}
