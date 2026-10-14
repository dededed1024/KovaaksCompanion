using System.Diagnostics;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>Glue for the App: records while the game runs, cuts clips on demand.</summary>
public sealed class RecordingService : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly GameProcessWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<WebcamConfig>? _handCam;
    private VideoRecorder? _rec;
    /// <summary>Cams recorded on their own (save mode Separate) with the slot index they were configured at.</summary>
    private readonly List<(int Slot, CamRecorder Recorder)> _cams = [];
    private WebcamConfig? _camConfig;
    private CancellationTokenSource? _startCts;

    /// <param name="handCam">Read at each recording start; empty cam list = no webcam.</param>
    public RecordingService(VideoOptions? options = null, Func<WebcamConfig>? handCam = null)
    {
        _o = options ?? new VideoOptions();
        _handCam = handCam;
        _watcher = new GameProcessWatcher(_o.GameProcessName);
        _watcher.Started += () => { ReportLaunch(); _ = StartRecordingAsync(); };
        _watcher.Exited += () => { GameExited?.Invoke(); _ = StopRecordingAsync(); };
    }

    public bool IsRecording => _rec?.IsRunning == true;
    public VideoRecorder? Recorder => _rec;
    public event Action<string>? Error;
    /// <summary>Raised (thread-pool thread) when <see cref="IsRecording"/> may have changed.</summary>
    public event Action? StateChanged;
    /// <summary>Raised (thread-pool thread) with the size of the game window each time recording starts.</summary>
    public event Action<int, int>? GameSized;

    /// <summary>Raised (thread-pool thread) with the start time of the game process each time the game is found running.</summary>
    public event Action<DateTime>? GameLaunched;
    /// <summary>Raised (thread-pool thread) when the game process is gone.</summary>
    public event Action? GameExited;

    public void Start() => _watcher.Start();

    private void ReportLaunch()
    {
        try
        {
            var ps = GameWindow.InSession(_o.GameProcessName);
            DateTime? start = null;
            foreach (var p in ps)
            {
                try { if (start is not { } s || p.StartTime < s) start = p.StartTime; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* elevated or protected game process: start time unreadable, fall back to now */ }
                p.Dispose();
            }
            GameLaunched?.Invoke(start ?? DateTime.Now);
        }
        catch (Exception e) { ErrorReporter.Report("video launch time", e); }
    }

    private async Task StartRecordingAsync()
    {
        var cts = new CancellationTokenSource();
        _startCts = cts;
        await _gate.WaitAsync();
        try
        {
            await DisposePreviousAsync();
            if (!await Task.Run(() => ObsRuntime.Start(_o.ObsRoot))) { Error?.Invoke("libobs: " + ObsRuntime.LastError); return; }
            var cfg = _camConfig = _handCam?.Invoke() ?? new WebcamConfig([], WebcamSave.Composite);
            var devices = cfg.Cams.Count > 0 ? await Task.Run(WebcamDeviceList.List) : [];
            var resolved = new List<(int Slot, WebcamSlot Config, string Id)>();
            for (int i = 0; i < cfg.Cams.Count; i++)
            {
                if (WebcamDeviceList.Resolve(cfg.Cams[i], devices) is { } id) resolved.Add((i, cfg.Cams[i], id));
                else Error?.Invoke($"webcam {i + 1} off: device not found");
            }
            var composite = cfg.Save == WebcamSave.Composite ? resolved.Select(r => new ObsCamSpec(r.Id, r.Config.Layout)).ToList() : [];

            var rec = new VideoRecorder(_o);
            rec.Problem += m => Error?.Invoke(m);
            if (await rec.StartAsync(composite, cts.Token))
            {
                _rec = rec;
                if (rec.Target is { Width: > 0, Height: > 0 } t) GameSized?.Invoke(t.Width, t.Height);
                if (rec.AudioError.Length > 0) Error?.Invoke("audio off: " + rec.AudioError);
                if (cfg.Save == WebcamSave.Separate) await StartSeparateCamsAsync(resolved, rec.Encoder!);
            }
            else if (!cts.IsCancellationRequested) Error?.Invoke(rec.LastError);
        }
        catch (Exception e) { ErrorReporter.Report("video start", e); Error?.Invoke(e.Message); }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    /// <summary>The previous session's footage is no longer needed once the game starts again.</summary>
    private async Task DisposePreviousAsync()
    {
        await DisposeCamsAsync();
        var old = _rec;
        _rec = null;
        if (old != null) await old.DisposeAsync();
    }

    private async Task StartSeparateCamsAsync(List<(int Slot, WebcamSlot Config, string Id)> cams, string encoder)
    {
        foreach (var (slot, config, id) in cams)
        {
            try
            {
                var cam = new CamRecorder(_o, slot);
                if (await cam.StartAsync(id, encoder, config.Rotation, config.Cut)) _cams.Add((slot, cam));
                else { Error?.Invoke($"webcam {slot + 1} off: " + cam.LastError); await cam.DisposeAsync(); }
            }
            catch (Exception e) { ErrorReporter.Report("webcam start", e); Error?.Invoke($"webcam {slot + 1} off: " + e.Message); }
        }
    }

    private async Task DisposeCamsAsync()
    {
        foreach (var c in _cams) await c.Recorder.DisposeAsync();
        _cams.Clear();
    }

    private async Task StopRecordingAsync()
    {
        _startCts?.Cancel();   // abort a pending wait for the game window
        await _gate.WaitAsync();
        try
        {
            foreach (var c in _cams) await c.Recorder.StopAsync();
            if (_rec != null) await _rec.StopAsync();
        }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Null when there is no recorder / no footage for that window. The recorder (and its buffer) stays usable after the game exits.
    /// With webcams in Composite mode they are already in the video; in Separate mode handcamN.mp4 next to the clip holds cam N on the clip's timeline.
    /// </summary>
    public async Task<ClipResult?> ExtractClipAsync(long startQpc, long endQpc, string outputPath, Action<double>? progress = null)
    {
        var rec = _rec;
        if (rec is null) return null;
        progress?.Invoke(0.02);
        var clip = await rec.ExtractClipAsync(startQpc, endQpc, outputPath);
        if (clip != null && _cams.Count > 0)
        {
            progress?.Invoke(0.6);
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            foreach (var (slot, cam) in _cams.ToList())
            {
                try
                {
                    if (!await cam.ExtractAsync(clip, Path.Combine(dir, $"handcam{slot + 1}.mp4")))
                        AppLog.Write("webcam", $"no footage from cam {slot + 1} for this clip");
                }
                catch (Exception e) { ErrorReporter.Report("webcam clip", e); Error?.Invoke("webcam not saved: " + e.Message); }
            }
        }
        progress?.Invoke(1);
        return clip;
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        await StopRecordingAsync();
        await DisposePreviousAsync();
    }
}
