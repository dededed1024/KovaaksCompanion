namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>The one place every error goes through: every error is written to the log, then posted to the webhook when the settings switch is on, at most <see cref="MaxPerProcess"/> posts per process. No other filtering, each post waits <see cref="Delay"/> and same-bug errors inside that window collapse to the last one.</summary>
public static class ErrorReporter
{
    public const int MaxPerProcess = 30;

    static readonly object Gate = new();
    static int _sent;
    static readonly Dictionary<string, int> SameBug = new();
    static readonly Dictionary<string, (string Title, Exception Ex, ErrorSeverity Severity)> Pending = new();

    /// <summary>How long a report waits before posting; same-bug errors inside it replace the waiting one.</summary>
    public static TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(10);
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
        lock (Gate) { _enabled = enabled; _send = send; _sent = 0; SameBug.Clear(); Pending.Clear(); }
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
            var key = BugKey(title, ex);
            lock (Gate)
            {
                if (!BugReport.ErrorEnabled || !_enabled()) return;
                // Same bug inside the window: only the latest one gets posted.
                if (Pending.ContainsKey(key)) { Pending[key] = (title, ex, severity); return; }
                Pending[key] = (title, ex, severity);
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(Delay);
                    Func<string, Exception, ErrorSeverity, ReportCount, Task> send;
                    ReportCount count;
                    (string Title, Exception Ex, ErrorSeverity Severity) last;
                    lock (Gate)
                    {
                        last = Pending[key];
                        Pending.Remove(key);
                        if (_sent >= MaxPerProcess) return;
                        _sent++;
                        SameBug[key] = SameBug.GetValueOrDefault(key) + 1;
                        count = new ReportCount(_sent, MaxPerProcess, SameBug[key]);
                        send = _send;
                    }
                    await send(last.Title, last.Ex, last.Severity, count);
                }
                catch { }
            });
        }
        catch { }
    }
}
