namespace KovaaksCompanion.Core.Video;

/// <summary>Glue for the App: records while the game runs, cuts clips on demand.</summary>
public sealed class VideoCaptureService : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly GameProcessWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VideoRecorder? _rec;

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

    public void Start() => _watcher.Start();

    private async Task StartRecordingAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var rec = new VideoRecorder(_o);
            if (await rec.StartAsync()) _rec = rec; else Error?.Invoke(rec.LastError);
        }
        catch (Exception e) { Error?.Invoke(e.Message); }
        finally { _gate.Release(); }
    }

    private async Task StopRecordingAsync()
    {
        await _gate.WaitAsync();
        try { if (_rec != null) await _rec.StopAsync(); }
        finally { _gate.Release(); }
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
