using System.Text.Json;

namespace KovaaksCompanion.Core.Update;

/// <summary>Newest published release: version without the leading "v" and its page URL.</summary>
public sealed record ReleaseInfo(Version Version, string Url);

/// <summary>GitHub latest-release lookup: JSON parsing and version comparison are pure; Fetch does the one HTTP call.</summary>
public static class UpdateCheck
{
    public const string Repo = "dededed1024/KovaaksCompanion";
    public const string LatestPage = "https://github.com/" + Repo + "/releases/latest";
    const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Parses a releases/latest response; null when the tag or URL is missing or not a version.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            var v = ParseVersion(tag);
            return v == null || string.IsNullOrEmpty(url) ? null : new(v, url);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"v1.2.3", "1.2" or "1.2.3+sha" to a Version; null when not parseable.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['+', '-']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out var v) ? v : null;
    }

    public static bool IsNewer(ReleaseInfo? latest, Version current) => latest != null && latest.Version > current;

    /// <summary>Whether a newly started instance should take over: strictly newer than the running one.</summary>
    public static bool ShouldReplace(Version mine, Version? running) => running != null && mine > running;

    /// <summary>Latest release, or null when offline, rate-limited or no release exists.</summary>
    public static async Task<ReleaseInfo?> Fetch(HttpClient http, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, Api);
            req.Headers.UserAgent.ParseAdd("KovaaksCompanion");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await http.SendAsync(req, ct);
            return res.IsSuccessStatusCode ? Parse(await res.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }
}
