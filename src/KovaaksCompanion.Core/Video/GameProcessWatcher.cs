using System.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Polls for the game process; raises Started when it appears and Exited when it is gone.</summary>
public sealed class GameProcessWatcher : IDisposable
{
    private readonly Func<bool> _isRunning;
    private readonly Timer _timer;
    private readonly TimeSpan _poll;
    private bool _running;
    private int _busy;

    public GameProcessWatcher(string processName, TimeSpan? poll = null, Func<bool>? probe = null)
    {
        _isRunning = probe ?? (() =>
        {
            var ps = Process.GetProcessesByName(processName);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        });
        _poll = poll ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsRunning => _running;
    public event Action? Started;
    public event Action? Exited;

    public void Start() => _timer.Change(TimeSpan.Zero, _poll);

    /// <summary>One poll step (public for tests).</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            bool now = _isRunning();
            if (now == _running) return;
            _running = now;
            if (now) Started?.Invoke(); else Exited?.Invoke();
        }
        finally { _busy = 0; }
    }

    public void Dispose() => _timer.Dispose();
}
