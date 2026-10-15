namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>The one place every error goes through: every error is written to the log, then posted to the webhook when the settings switch is on, at most <see cref="MaxPerProcess"/> posts per process. No other filtering, no dedup.</summary>
public static class ErrorReporter
{
    public const int MaxPerProcess = 30;

    static readonly object Gate = new();
    static int _sent;
    static Func<bool> _enabled = () => false;
    static Func<string, Exception, ErrorSeverity, Task> _send = (_, _, _) => Task.CompletedTask;

    /// <summary>Wires the real webhook; <paramref name="enabled"/> is read on every report (the settings switch).</summary>
    public static void Configure(HttpClient http, string version, Func<bool> enabled) =>
        Configure(enabled, (title, ex, severity) => BugReport.SendError(http, title, ex, version, severity));

    /// <summary>Wires a custom sender (tests).</summary>
    public static void Configure(Func<bool> enabled, Func<string, Exception, ErrorSeverity, Task> send)
    {
        lock (Gate) { _enabled = enabled; _send = send; _sent = 0; }
    }

    /// <summary>Logs the error, then posts it fire-and-forget; never throws.</summary>
    public static void Report(string title, Exception ex, ErrorSeverity severity = ErrorSeverity.Medium)
    {
        try
        {
            AppLog.Write("error", $"[{(int)severity}] {title}: {ex}");
            Func<string, Exception, ErrorSeverity, Task> send;
            lock (Gate)
            {
                if (!BugReport.ErrorEnabled || !_enabled() || _sent >= MaxPerProcess) return;
                _sent++;
                send = _send;
            }
            _ = Task.Run(async () => { try { await send(title, ex, severity); } catch { } });
        }
        catch { }
    }
}
