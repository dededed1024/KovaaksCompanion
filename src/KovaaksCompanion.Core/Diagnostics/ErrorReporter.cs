namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>The one place every error goes through: every error is written to the log, then posted to the webhook when the settings switch is on, at most <see cref="MaxPerProcess"/> posts per process. No other filtering, no dedup.</summary>
public static class ErrorReporter
{
    public const int MaxPerProcess = 30;

    static readonly object Gate = new();
    static int _sent;
    static readonly Dictionary<string, int> SameBug = new();
    static Func<bool> _enabled = () => false;
    static Func<string, Exception, ErrorSeverity, ReportCount, Task> _send = (_, _, _, _) => Task.CompletedTask;

    /// <summary>Where a post stands: <see cref="Sent"/> of <see cref="Max"/> posts this process, <see cref="SameBug"/> posts of this same bug so far (this one included).</summary>
    public readonly record struct ReportCount(int Sent, int Max, int SameBug);

    /// <summary>Wires the real webhook; <paramref name="enabled"/> is read on every report (the settings switch).</summary>
    public static void Configure(HttpClient http, string version, Func<bool> enabled) =>
        Configure(enabled, (title, ex, severity, count) => BugReport.SendError(http, title, ex, version, severity, count));

    /// <summary>Wires a custom sender (tests).</summary>
    public static void Configure(Func<bool> enabled, Func<string, Exception, ErrorSeverity, ReportCount, Task> send)
    {
        lock (Gate) { _enabled = enabled; _send = send; _sent = 0; SameBug.Clear(); }
    }

    // Same bug = same title, exception type and top stack frame.
    static string BugKey(string title, Exception ex)
    {
        var frame = ex.StackTrace?.Split('\n', 2)[0].Trim() ?? "";
        return $"{title}|{ex.GetType().FullName}|{frame}";
    }

    /// <summary>Logs the error, then posts it fire-and-forget; never throws.</summary>
    public static void Report(string title, Exception ex, ErrorSeverity severity = ErrorSeverity.Medium)
    {
        try
        {
            AppLog.Write("error", $"[{(int)severity}] {title}: {ex}");
            Func<string, Exception, ErrorSeverity, ReportCount, Task> send;
            ReportCount count;
            lock (Gate)
            {
                if (!BugReport.ErrorEnabled || !_enabled() || _sent >= MaxPerProcess) return;
                _sent++;
                var key = BugKey(title, ex);
                SameBug[key] = SameBug.GetValueOrDefault(key) + 1;
                count = new ReportCount(_sent, MaxPerProcess, SameBug[key]);
                send = _send;
            }
            _ = Task.Run(async () => { try { await send(title, ex, severity, count); } catch { } });
        }
        catch { }
    }
}
