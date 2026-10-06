using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Session;

/// <summary>
/// RUN-001/002: watches the stats folder; raises <see cref="RunFinished"/> for stats CSVs created after
/// <c>startedAt</c> once the file has stopped growing and parses.
/// </summary>
public sealed class StatsWatcher : IDisposable
{
    readonly string _folder;
    readonly DateTime _startedAt;
    readonly TimeSpan _retryDelay;
    readonly int _maxAttempts;
    readonly HashSet<string> _seen = [];
    readonly CancellationTokenSource _cts = new();
    FileSystemWatcher? _fsw;

    public event Action<RunStats, string>? RunFinished;

    public StatsWatcher(string folder, DateTime startedAt, TimeSpan? retryDelay = null, int maxAttempts = 40)
    {
        _folder = folder; _startedAt = startedAt; _maxAttempts = maxAttempts;
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
    }

    public void Start()
    {
        Directory.CreateDirectory(_folder);
        _fsw = new FileSystemWatcher(_folder, "*Stats.csv") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        _fsw.Created += (_, e) => Handle(e.FullPath);
        _fsw.Renamed += (_, e) => Handle(e.FullPath);
        _fsw.Changed += (_, e) => Handle(e.FullPath);
        _fsw.EnableRaisingEvents = true;
    }

    /// <summary>Processes one path (also the entry point for tests). Returns the task of the background read.</summary>
    public Task Handle(string path)
    {
        if (!path.EndsWith("Stats.csv", StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;
        lock (_seen) { if (!_seen.Add(path)) return Task.CompletedTask; }
        return Task.Run(() => ReadWhenReady(path));
    }

    async Task ReadWhenReady(string path)
    {
        try
        {
            if (!File.Exists(path) || File.GetCreationTime(path) < _startedAt) return;
            string? last = null;
            for (var i = 0; i < _maxAttempts && !_cts.IsCancellationRequested; i++)
            {
                var text = TryRead(path);
                if (text != null && text == last)
                {
                    RunStats? run = null;
                    try { run = StatsCsvParser.Parse(path, text); } catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException) { }
                    if (run != null) { RunFinished?.Invoke(run, path); return; }
                }
                last = text;
                await Task.Delay(_retryDelay, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    static string? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public void Dispose() { _cts.Cancel(); _fsw?.Dispose(); }
}
