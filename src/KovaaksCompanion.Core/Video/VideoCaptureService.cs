using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Glue for the App: records while the game runs, cuts clips on demand.</summary>
public sealed class VideoCaptureService : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly GameProcessWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VideoRecorder? _rec;
    private CancellationTokenSource? _startCts;

    public VideoCaptureService(VideoOptions? options = null)
    {
        _o = options ?? new VideoOptions();
        _watcher = new GameProcessWatcher(_o.GameProcessName);
        _watcher.Started += () => _ = StartRecordingAsync();
        _watcher.Exited += () => _ = StopRecordingAsync();
    }

    public bool IsRecording => _rec?.IsRunning == true;
    public VideoRecorder? Recorder => _rec;
    public event Action<string>? Error;
    /// <summary>Raised (thread-pool thread) when <see cref="IsRecording"/> may have changed.</summary>
    public event Action? StateChanged;

    public void Start() => _watcher.Start();

    private async Task StartRecordingAsync()
    {
        var cts = new CancellationTokenSource();
        _startCts = cts;
        await _gate.WaitAsync();
        try
        {
            var rec = new VideoRecorder(_o);
            if (await rec.StartAsync(cts.Token))
            {
                _rec = rec;
                if (rec.AudioError.Length > 0) Error?.Invoke("audio off: " + rec.AudioError);
            }
            else if (!cts.IsCancellationRequested) Error?.Invoke(rec.LastError);
        }
        catch (Exception e) { AppLog.Write("video", "start failed: " + e); Error?.Invoke(e.Message); }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    private async Task StopRecordingAsync()
    {
        _startCts?.Cancel();   // abort a pending wait for the game window
        await _gate.WaitAsync();
        try { if (_rec != null) await _rec.StopAsync(); }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    /// <summary>Null when there is no recorder / no footage for that window. The recorder (and its segments) stays usable after the game exits.</summary>
    public Task<ClipResult?> ExtractClipAsync(DateTime startUtc, DateTime endUtc, string outputPath)
        => _rec is null ? Task.FromResult<ClipResult?>(null) : _rec.ExtractClipAsync(startUtc, endUtc, outputPath);

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        await StopRecordingAsync();
    }
}
