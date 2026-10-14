using System.Text.Json;

namespace KovaaksCompanion.Core.Update;

/// <summary>Newest published release: version without the leading "v" and its page URL; AssetUrl and Sha256 describe the attached exe (empty when the release lacks a digest, so no in-app update).</summary>
public sealed record ReleaseInfo(Version Version, string Url, string Notes = "", string AssetUrl = "", string Sha256 = "");

/// <summary>GitHub latest-release lookup: JSON parsing and version comparison are pure; Fetch does the one HTTP call.</summary>
public static class UpdateCheck
{
    public const string Repo = "dededed1024/KovaaksCompanion";
    public const string LatestPage = "https://github.com/" + Repo + "/releases/latest";
    /// <summary>Always resolves to the exe attached to the newest release, so the browser starts the download directly.</summary>
    public const string LatestDownload = "https://github.com/" + Repo + "/releases/latest/download/KovaaksCompanion.exe";
    public const string AssetName = "KovaaksCompanion.exe";
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
            var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            var v = ParseVersion(tag);
            if (v == null || string.IsNullOrEmpty(url)) return null;
            var (assetUrl, sha) = FindAsset(root);
            return new(v, url, notes?.Trim() ?? "", assetUrl, sha);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The release's KovaaksCompanion.exe asset: download URL and lowercase SHA-256 from GitHub's "sha256:..." digest; empty strings when either is missing.</summary>
    static (string Url, string Sha256) FindAsset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return ("", "");
        foreach (var a in assets.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object || (a.TryGetProperty("name", out var n) ? n.GetString() : null) != AssetName) continue;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (string.IsNullOrEmpty(url) || digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) return ("", "");
            var hex = digest[7..].ToLowerInvariant();
            return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? (url, hex) : ("", "");
        }
        return ("", "");
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
