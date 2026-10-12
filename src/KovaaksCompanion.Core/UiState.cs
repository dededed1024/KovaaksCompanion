using System.Text.Json;

namespace KovaaksCompanion.Core;

/// <summary>Small per-user UI state (favorite playlists, visible run-chart series) in ui.json next to settings.json. Kept apart from <see cref="AppSettings"/> so saving settings never overwrites it.</summary>
public sealed class UiState
{
    public static readonly string[] DefaultSeries = ["Accuracy", "Score"];
    /// <summary>Voltaic S5 Novice.</summary>
    public static readonly string[] DefaultFavorites = ["459"];

    public static string DefaultFile => Path.Combine(AppSettings.DefaultDataFolder, "ui.json");

    readonly List<string> _favs = [];
    /// <summary>Favorites in the order they were added (oldest first).</summary>
    public IReadOnlyList<string> FavoritePlaylists => _favs;
    public IReadOnlyList<string> VisibleSeries { get; set; } = DefaultSeries;
    public bool ShowTrail { get; set; } = true;
    /// <summary>Run chart: overlay the previous local run of the scenario.</summary>
    public bool ComparePrev { get; set; } = true;
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

    /// <summary>Profile name chosen by the user; empty = the Steam name.</summary>
    public string ProfileName { get; set; } = "";
    /// <summary>Profile picture file chosen by the user; empty = the Steam avatar.</summary>
    public string ProfileImage { get; set; } = "";

    /// <summary>Profile popup accent as #RRGGBB; empty = the app accent.</summary>
    public string ProfileAccent { get; set; } = "";

    /// <summary>Profile card shows the moving watermark; off = the static look a capture has.</summary>
    public bool ProfileAnimated { get; set; } = true;

    public const int MaxProfileItems = 6;

    List<string> _profile = [];
    /// <summary>What the profile card shows, top first: <see cref="PlaylistKey"/>, <see cref="SubcategoryKey"/> and <see cref="ScenarioKey"/> values.</summary>
    public IReadOnlyList<string> ProfileItems => _profile;

    public static string PlaylistKey(string playlist) => $"p:{playlist}";
    public static string SubcategoryKey(string playlist, string parent, string sub) => $"c:{playlist}|{parent}|{sub}";
    public static string ScenarioKey(string scenario) => $"s:{scenario}";

    /// <summary>Splits a <see cref="SubcategoryKey"/>; null when malformed.</summary>
    public static (string Playlist, string Parent, string Sub)? ParseSubcategoryKey(string key)
    {
        if (!key.StartsWith("c:", StringComparison.Ordinal)) return null;
        var p = key[2..].Split('|', 3);
        return p.Length == 3 ? (p[0], p[1], p[2]) : null;
    }

    /// <summary>Replaces the profile card's items (duplicates dropped, capped at <see cref="MaxProfileItems"/>).</summary>
    public void SetProfileItems(IEnumerable<string> keys) =>
        _profile = keys.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxProfileItems).ToList();

    public bool IsFavorite(string playlist) => _favs.Contains(playlist, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stack position of a favorite: higher = favorited later; -1 when not a favorite. Show the highest first.</summary>
    public int FavoriteIndex(string playlist) => _favs.FindIndex(f => string.Equals(f, playlist, StringComparison.OrdinalIgnoreCase));

    readonly List<string> _favScenarios = [];
    readonly List<string> _favRuns = [];
    /// <summary>Favorite scenario names, oldest first.</summary>
    public IReadOnlyList<string> FavoriteScenarios => _favScenarios;
    /// <summary>Favorite runs as <see cref="RunKey"/> values, oldest first.</summary>
    public IReadOnlyList<string> FavoriteRuns => _favRuns;

    /// <summary>Identity of a run: scenario plus end time.</summary>
    public static string RunKey(string scenario, DateTime end) => $"{scenario}|{end.Ticks}";

    /// <summary>Splits a <see cref="RunKey"/>; null when malformed.</summary>
    public static (string Scenario, DateTime End)? ParseRunKey(string key)
    {
        var i = key.LastIndexOf('|');
        return i > 0 && long.TryParse(key.AsSpan(i + 1), out var t) && t > 0 && t <= DateTime.MaxValue.Ticks ? (key[..i], new DateTime(t)) : null;
    }

    public bool IsFavoriteScenario(string scenario) => _favScenarios.Contains(scenario, StringComparer.OrdinalIgnoreCase);
    public bool IsFavoriteRun(string scenario, DateTime end) => _favRuns.Contains(RunKey(scenario, end));

    /// <summary>Flips the scenario favorite and returns the new state.</summary>
    public bool ToggleFavoriteScenario(string scenario)
    {
        if (_favScenarios.RemoveAll(f => string.Equals(f, scenario, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _profile.RemoveAll(k => string.Equals(k, ScenarioKey(scenario), StringComparison.OrdinalIgnoreCase));
            return false;
        }
        _favScenarios.Add(scenario);
        return true;
    }

    /// <summary>Flips the run favorite and returns the new state.</summary>
    public bool ToggleFavoriteRun(string scenario, DateTime end)
    {
        var key = RunKey(scenario, end);
        if (_favRuns.Remove(key)) return false;
        _favRuns.Add(key);
        return true;
    }

    public bool IsPinned(string playlist) => _tiers.Contains(playlist, StringComparer.OrdinalIgnoreCase);

    /// <summary>Flips the Home hero pin (appended when pinned) and returns the new state.</summary>
    public bool TogglePinned(string playlist)
    {
        if (_tiers.RemoveAll(t => string.Equals(t, playlist, StringComparison.OrdinalIgnoreCase)) > 0) return false;
        _tiers.Add(playlist);
        return true;
    }

    public const int MaxFavorites = 6;

    public bool FavoritesFull => _favs.Count >= MaxFavorites;

    /// <summary>Flips the favorite flag and returns the new state. Adding past <see cref="MaxFavorites"/> does nothing and returns false.</summary>
    public bool ToggleFavorite(string playlist)
    {
        if (_favs.RemoveAll(f => string.Equals(f, playlist, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _profile.RemoveAll(k => string.Equals(k, PlaylistKey(playlist), StringComparison.OrdinalIgnoreCase) || k.StartsWith($"c:{playlist}|", StringComparison.OrdinalIgnoreCase));
            return false;
        }
        if (FavoritesFull) return false;
        _favs.Add(playlist);
        // A new favorite shows on the profile card (top) while there is room.
        if (_profile.Count < MaxProfileItems) _profile.Insert(0, PlaylistKey(playlist));
        return true;
    }

    sealed record Dto(string[]? FavoritePlaylists, string[]? VisibleSeries, bool? ShowTrail = null, string? StatsChart = null, string? RadarAxes = null, bool? RadarCompare = null, string? TierPlaylist = null, string[]? TierPlaylists = null, string? ProfileName = null, string? ProfileImage = null, string? ProfileAccent = null, string[]? FavoriteScenarios = null, string[]? FavoriteRuns = null, bool? ComparePrev = null, bool? ProfileAnimated = null, string[]? ProfileItems = null);

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
                foreach (var n in f) if (!s.IsFavorite(n) && !s.FavoritesFull) s._favs.Add(n);
            }
            else
            {
                // Use defaults when file is missing or FavoritePlaylists is null
                foreach (var n in DefaultFavorites) s._favs.Add(n);
            }
            if (d?.VisibleSeries is { } v) s.VisibleSeries = v;
            if (d?.ShowTrail is { } t) s.ShowTrail = t;
            if (d?.ComparePrev is { } cp) s.ComparePrev = cp;
            if (d?.StatsChart is { } sc) s.StatsChart = sc;
            if (d?.RadarAxes is { } ra) s.RadarAxes = ra;
            if (d?.RadarCompare is { } rc) s.RadarCompare = rc;
            if (d?.ProfileName is { } pn) s.ProfileName = pn;
            if (d?.ProfileImage is { } pi) s.ProfileImage = pi;
            if (d?.ProfileAccent is { } pa) s.ProfileAccent = pa;
            if (d?.ProfileAnimated is { } pm) s.ProfileAnimated = pm;
            if (d?.FavoriteScenarios is { } fs) s._favScenarios.AddRange(fs.Distinct(StringComparer.OrdinalIgnoreCase));
            if (d?.FavoriteRuns is { } fr) s._favRuns.AddRange(fr.Distinct());
            if (d?.TierPlaylists is { } tl) s._tiers = tl.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            else if (d?.TierPlaylist is { Length: > 0 } tp && !s.IsPinned(tp)) s._tiers.Add(tp);
            // Files from before the editable profile card: show the favorite playlists, newest first.
            if (d?.ProfileItems is { } pi2) s.SetProfileItems(pi2);
            else s.SetProfileItems(s._favs.AsEnumerable().Reverse().Select(PlaylistKey));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Use defaults when file is missing or malformed
            foreach (var n in DefaultFavorites) s._favs.Add(n);
            s.SetProfileItems(s._favs.AsEnumerable().Reverse().Select(PlaylistKey));
        }
        return s;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new Dto(_favs.ToArray(), VisibleSeries.ToArray(), ShowTrail, StatsChart, RadarAxes, RadarCompare, null, _tiers.ToArray(), ProfileName, ProfileImage, ProfileAccent, _favScenarios.ToArray(), _favRuns.ToArray(), ComparePrev, ProfileAnimated, _profile.ToArray()), Options));
    }
}
