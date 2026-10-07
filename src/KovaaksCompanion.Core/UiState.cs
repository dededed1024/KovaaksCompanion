using System.Text.Json;

namespace KovaaksCompanion.Core;

/// <summary>Small per-user UI state (favorite playlists, visible run-chart series) in ui.json next to settings.json. Kept apart from <see cref="AppSettings"/> so saving settings never overwrites it.</summary>
public sealed class UiState
{
    public static readonly string[] DefaultSeries = ["Accuracy", "Score"];
    /// <summary>Voltaic S5 Novice.</summary>
    public static readonly string[] DefaultFavorites = ["459"];

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
    /// <summary>Voltaic S5 Novice.</summary>
    public static readonly string[] DefaultTierPlaylists = ["459"];

    List<string> _tiers = [.. DefaultTierPlaylists];
    /// <summary>Playlists (KovaaksBenchmarkId as text) whose tiers the Home hero shows, in pin order.</summary>
    public IReadOnlyList<string> TierPlaylists => _tiers;

    public bool IsFavorite(string playlist) => _favs.Contains(playlist);

    public bool IsPinned(string playlist) => _tiers.Contains(playlist, StringComparer.OrdinalIgnoreCase);

    /// <summary>Flips the Home hero pin (appended when pinned) and returns the new state.</summary>
    public bool TogglePinned(string playlist)
    {
        if (_tiers.RemoveAll(t => string.Equals(t, playlist, StringComparison.OrdinalIgnoreCase)) > 0) return false;
        _tiers.Add(playlist);
        return true;
    }

    /// <summary>Flips the favorite flag and returns the new state.</summary>
    public bool ToggleFavorite(string playlist)
    {
        if (_favs.Remove(playlist)) return false;
        _favs.Add(playlist);
        return true;
    }

    sealed record Dto(string[]? FavoritePlaylists, string[]? VisibleSeries, bool? ShowTrail = null, string? StatsChart = null, string? RadarAxes = null, bool? RadarCompare = null, string? TierPlaylist = null, string[]? TierPlaylists = null);

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Defaults when the file is missing or unreadable.</summary>
    public static UiState Load(string? path = null)
    {
        var s = new UiState();
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path ?? DefaultFile), Options);
            if (d?.FavoritePlaylists is { } f)
            {
                foreach (var n in f) s._favs.Add(n);
            }
            else
            {
                // Use defaults when file is missing or FavoritePlaylists is null
                foreach (var n in DefaultFavorites) s._favs.Add(n);
            }
            if (d?.VisibleSeries is { } v) s.VisibleSeries = v;
            if (d?.ShowTrail is { } t) s.ShowTrail = t;
            if (d?.StatsChart is { } sc) s.StatsChart = sc;
            if (d?.RadarAxes is { } ra) s.RadarAxes = ra;
            if (d?.RadarCompare is { } rc) s.RadarCompare = rc;
            if (d?.TierPlaylists is { } tl) s._tiers = tl.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            else if (d?.TierPlaylist is { Length: > 0 } tp && !s.IsPinned(tp)) s._tiers.Add(tp);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Use defaults when file is missing or malformed
            foreach (var n in DefaultFavorites) s._favs.Add(n);
        }
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dto(_favs.Order(StringComparer.OrdinalIgnoreCase).ToArray(), VisibleSeries.ToArray(), ShowTrail, StatsChart, RadarAxes, RadarCompare, null, _tiers.ToArray()), Options));
    }
}
