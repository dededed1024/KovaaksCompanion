using System.Globalization;
using System.Text.Json;

namespace KovaaksCompanion.Core.Benchmarks;

/// <summary>One run from the server's last-scores list. Score is a real value (same unit as the local CSV).</summary>
public sealed record CloudScore(DateTime Time, double Score);

/// <summary>Own leaderboard entry: resolved account name and the best score's date.</summary>
public sealed record LeaderboardEntry(string SteamId, string Username, double Score, DateTime? Time);

/// <summary>Parsers for KovaaK's leaderboard and last-scores JSON (no network).</summary>
public static class CloudScores
{
    /// <summary>First entry of a leaderboard page whose steamId matches; null when absent or someone else's.</summary>
    public static LeaderboardEntry? ParseEntry(string json, string steamId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var e in data.EnumerateArray())
        {
            var id = Str(e, "steamId");
            var name = Str(e, "webappUsername");
            if (id != steamId || string.IsNullOrEmpty(name)) continue;
            var attrs = e.TryGetProperty("attributes", out var a) ? a : default;
            return new(id, name, Num(e, "score") ?? 0, attrs.ValueKind == JsonValueKind.Object ? Epoch(attrs) : null);
        }
        return null;
    }

    /// <summary>Parses the last-scores array, oldest first; entries without a usable date or score are skipped.</summary>
    public static List<CloudScore> ParseLastScores(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var res = new List<CloudScore>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return res;
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var attrs = e.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            var isObj = attrs.ValueKind == JsonValueKind.Object;
            var time = isObj ? Epoch(attrs) : null;
            var score = (isObj ? Num(attrs, "score") : null) ?? Num(e, "score");
            if (time != null && score != null) res.Add(new(time.Value, score.Value));
        }
        return res.OrderBy(s => s.Time).ToList();
    }

    static DateTime? Epoch(JsonElement attrs)
    {
        var ms = Num(attrs, "epoch");
        if (ms == null || ms <= 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms.Value).LocalDateTime;
    }

    static string? Str(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
    }

    static double? Num(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }
}
