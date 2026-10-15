using System.Reflection;
using System.Text.Json;

namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>Posts a user-written bug report plus the scrubbed log tail to a Discord webhook.</summary>
public static class BugReport
{
    /// <summary>Discord webhook URL, baked in at build time from the KOVAAKS_FEEDBACK_WEBHOOK_URL environment variable (see the Core csproj). Empty hides the report UI.</summary>
    public static readonly string WebhookUrl =
        typeof(BugReport).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WebhookUrl")?.Value ?? "";

    /// <summary>Webhook for automatic error reports, baked in from KOVAAKS_ERROR_WEBHOOK_URL. Empty turns automatic reports off.</summary>
    public static readonly string ErrorWebhookUrl =
        typeof(BugReport).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ErrorWebhookUrl")?.Value ?? "";

    const int MaxContent = 1900;

    public static bool Enabled => WebhookUrl.Length > 0;

    public static bool ErrorEnabled => ErrorWebhookUrl.Length > 0;

    /// <summary>Hides the profile path and the Windows user name so a report does not identify the sender.</summary>
    public static string Scrub(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        var user = Environment.UserName;
        if (user.Length >= 3) text = text.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    /// <summary>Discord <c>payload_json</c> for a manual report: header and environment come first, so only the user text is cut at the content limit.</summary>
    public static string BuildPayloadJson(string message, string version, string env)
    {
        var head = $"**Feedback** v{version}\n{Scrub(env)}\n";
        var content = head + Scrub(message);
        if (content.Length > MaxContent) content = content[..MaxContent];
        return JsonSerializer.Serialize(new { content });
    }

    /// <summary>Embed color per severity: red, orange, yellow, gray.</summary>
    public static int SeverityColor(ErrorSeverity severity) => severity switch
    {
        ErrorSeverity.Critical => 0xE74C3C,
        ErrorSeverity.High => 0xE67E22,
        ErrorSeverity.Medium => 0xF1C40F,
        _ => 0x95A5A6,
    };

    /// <summary>Discord <c>payload_json</c> for an automatic error report: a colored embed titled with the severity number, exception details only, no OS or environment.</summary>
    public static string BuildErrorPayloadJson(string title, Exception ex, string version, ErrorSeverity severity = ErrorSeverity.Medium)
    {
        var description = Scrub($"{ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}");
        if (description.Length > MaxContent) description = description[..MaxContent];
        var embed = new { title = Scrub($"[{(int)severity}] {title}"), description, color = SeverityColor(severity), footer = new { text = $"v{version} · severity {(int)severity} ({severity})" } };
        return JsonSerializer.Serialize(new { embeds = new[] { embed } });
    }

    /// <summary>Posts the message (user text and environment), then the scrubbed log tail as a second message. True when the first post succeeded.</summary>
    public static async Task<bool> Send(HttpClient http, string message, string version, string env, CancellationToken ct = default)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(BuildPayloadJson(message, version, env), System.Text.Encoding.UTF8, "application/json"), "payload_json");
            using var res = await http.PostAsync(WebhookUrl, form, ct);
            if (!res.IsSuccessStatusCode) return false;
            await SendLog(http, ct);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>Lines of the log a report attaches.</summary>
    public const int LogLines = 300;

    /// <summary>The scrubbed log tail a report attaches, exactly as it is sent; null when there is no log.</summary>
    public static string? LogPreview() => ReadLog() is { } log ? Scrub(AppLog.Tail(log, LogLines)) : null;

    // Best effort: the log is a separate post so a failure here never fails the report.
    static async Task SendLog(HttpClient http, CancellationToken ct)
    {
        try
        {
            var log = LogPreview();
            if (log == null) return;
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(JsonSerializer.Serialize(new { content = "Log" }), System.Text.Encoding.UTF8, "application/json"), "payload_json");
            form.Add(new StringContent(log, System.Text.Encoding.UTF8, "text/plain"), "files[0]", "app.log");
            using var res = await http.PostAsync(WebhookUrl, form, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
    }

    /// <summary>Posts one automatic error message; no OS, environment or log.</summary>
    public static async Task<bool> SendError(HttpClient http, string title, Exception ex, string version, ErrorSeverity severity = ErrorSeverity.Medium, CancellationToken ct = default)
    {
        try
        {
            using var content = new StringContent(BuildErrorPayloadJson(title, ex, version, severity), System.Text.Encoding.UTF8, "application/json");
            using var res = await http.PostAsync(ErrorWebhookUrl, content, ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }

    // The log may be open for append, so share it.
    static string? ReadLog()
    {
        try
        {
            using var fs = new FileStream(AppLog.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
