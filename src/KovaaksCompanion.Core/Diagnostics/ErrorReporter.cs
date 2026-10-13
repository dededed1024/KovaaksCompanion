namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>Sends unhandled and video errors to the webhook: each distinct error once per process, at most <see cref="MaxPerProcess"/> in all.</summary>
public static class ErrorReporter
{
    public const int MaxPerProcess = 10;

    static readonly object Gate = new();
    static readonly HashSet<string> Seen = [];
    static int _sent;
    static Func<bool> _enabled = () => false;
    static Func<string, Exception, Task> _send = (_, _) => Task.CompletedTask;

    /// <summary>Wires the real webhook; <paramref name="enabled"/> is read on every report (the settings switch).</summary>
    public static void Configure(HttpClient http, string version, Func<bool> enabled) =>
        Configure(enabled, (title, ex) => BugReport.SendError(http, title, ex, version));

    /// <summary>Wires a custom sender (tests).</summary>
    public static void Configure(Func<bool> enabled, Func<string, Exception, Task> send)
    {
        lock (Gate) { _enabled = enabled; _send = send; Seen.Clear(); _sent = 0; }
    }

    /// <summary>Fire-and-forget; never throws.</summary>
    public static void Report(string title, Exception ex)
    {
        try
        {
            Func<string, Exception, Task> send;
            lock (Gate)
            {
                if (!BugReport.Enabled || !_enabled() || _sent >= MaxPerProcess || !Seen.Add(Key(ex))) return;
                _sent++;
                send = _send;
            }
            _ = Task.Run(async () => { try { await send(title, ex); } catch { } });
        }
        catch { }
    }

    /// <summary>Exception type plus the first stack frame.</summary>
    public static string Key(Exception ex)
    {
        var first = ex.StackTrace?.Split('\n', 2)[0].Trim() ?? "";
        return ex.GetType().FullName + "|" + first;
    }
}
