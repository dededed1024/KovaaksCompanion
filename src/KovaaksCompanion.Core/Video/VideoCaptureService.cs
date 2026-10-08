using System.Diagnostics;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Glue for the App: records while the game runs, cuts clips on demand.</summary>
public sealed class VideoCaptureService : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly GameProcessWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<HandCamConfig>? _handCam;
    private VideoRecorder? _rec;
    /// <summary>Running cams with the slot index and layout snapshot taken at start, so a failed slot or a later settings edit cannot shift file numbers or layouts.</summary>
    private readonly List<(int Slot, HandCamSlot Config, HandCamRecorder Recorder)> _cams = [];
    private CancellationTokenSource? _startCts;

    /// <param name="handCam">Read at each recording start (device) and each clip (layout); empty device = no hand-cam.</param>
    public VideoCaptureService(VideoOptions? options = null, Func<HandCamConfig>? handCam = null)
    {
        _o = options ?? new VideoOptions();
        _handCam = handCam;
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
                await StartHandCamAsync(rec.Encoder!);
            }
            else if (!cts.IsCancellationRequested) Error?.Invoke(rec.LastError);
        }
        catch (Exception e) { AppLog.Write("video", "start failed: " + e); Error?.Invoke(e.Message); }
        finally { _gate.Release(); }
        StateChanged?.Invoke();
    }

    private async Task StartHandCamAsync(string encoder)
    {
        await DisposeCamsAsync();
        var cfg = _handCam?.Invoke();
        var cams = cfg?.Cams ?? [];
        int gameWidth = _rec?.Target?.Width ?? 0;
        for (int i = 0; i < cams.Count; i++)
        {
            try
            {
                // Composite only shows the cam at size% of the game width, so record it at that size (Separate keeps the cam's own resolution)
                int width = cfg!.Save == HandCamSave.Composite && gameWidth > 0 ? Math.Max(160, gameWidth * Math.Clamp(cams[i].Size, 5, 60) / 100 / 2 * 2) : 0;
                var cam = new HandCamRecorder(_o.FfmpegPath, cams[i], i, _o.BufferDir, encoder, _o.Quality, _o.BufferMinutes, width);
                if (await cam.StartAsync()) _cams.Add((i, cams[i], cam));
                else { Error?.Invoke($"hand cam {i + 1} off: " + cam.LastError); await cam.DisposeAsync(); }
            }
            catch (Exception e) { AppLog.Write("handcam", "start failed: " + e); Error?.Invoke($"hand cam {i + 1} off: " + e.Message); }
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

    /// <summary>Null when there is no recorder / no footage for that window. The recorder (and its segments) stays usable after the game exits. With a hand-cam the clip is the game footage with the cam composited in.</summary>
    public async Task<ClipResult?> ExtractClipAsync(DateTime startUtc, DateTime endUtc, string outputPath)
    {
        if (_rec is null) return null;
        var clip = await _rec.ExtractClipAsync(startUtc, endUtc, outputPath);
        if (clip != null && _cams.Count > 0 && _rec.Encoder is { } enc)
        {
            try { await ComposeAsync(clip, outputPath, enc); }
            catch (Exception e) { AppLog.Write("handcam", "hand cam failed, keeping plain clip: " + e.Message); Error?.Invoke("hand cam not added: " + e.Message); }
        }
        return clip;
    }

    /// <summary>Per the save mode: video.mp4 = composite (Composite) or plain game (Separate); handcamN.mp4 = cam N aligned to the game clip's timeline (Separate).</summary>
    private async Task ComposeAsync(ClipResult clip, string clipPath, string encoder)
    {
        var cfg = _handCam!.Invoke();
        var dir = Path.GetDirectoryName(clipPath)!;
        var merged = clipPath + ".merged.mp4";
        var temps = new List<string> { merged };
        try
        {
            var inputs = new List<FfmpegCommand.CamInput>();
            foreach (var (slot, config, recorder) in _cams.ToList())
            {
                var raw = $"{clipPath}.cam{slot}.mp4";
                temps.Add(raw);
                var camStart = await recorder.ExtractAsync(clip.ClipStartUtc, clip.ClipStartUtc + clip.Duration, raw, a => RunFfmpegAsync(a));
                if (camStart is null) { AppLog.Write("handcam", $"no footage from cam {slot + 1} for this clip"); continue; }
                var shift = (camStart.Value - clip.ClipStartUtc).TotalSeconds;
                inputs.Add(new FfmpegCommand.CamInput(raw, shift, config.Layout));
                if (cfg.Save != HandCamSave.Composite)
                    await RunFfmpegAsync(FfmpegCommand.BuildCamAlign(raw, shift, clip.Duration.TotalSeconds, encoder, _o.Quality, Path.Combine(dir, $"handcam{slot + 1}.mp4")));
            }
            if (inputs.Count == 0 || cfg.Save == HandCamSave.Separate) return;
            if (encoder == "h264_nvenc" && _rec?.Target is { Width: > 0 } t)
            {
                try { await RunFfmpegAsync(FfmpegCommand.BuildCompositeCuda(clipPath, t.Width, inputs, _o.Quality, merged)); }
                catch (Exception e) { AppLog.Write("handcam", "GPU composite failed, using CPU overlay: " + e.Message); File.Delete(merged); await RunFfmpegAsync(FfmpegCommand.BuildComposite(clipPath, inputs, encoder, _o.Quality, merged)); }
            }
            else await RunFfmpegAsync(FfmpegCommand.BuildComposite(clipPath, inputs, encoder, _o.Quality, merged));
            File.Move(merged, clipPath, true);
        }
        finally
        {
            foreach (var f in temps) { try { File.Delete(f); } catch { } }
        }
    }

    private async Task RunFfmpegAsync(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var msg = await err;
            AppLog.Write("ffmpeg", $"command failed code={p.ExitCode}: {string.Join(' ', psi.ArgumentList)}{Environment.NewLine}{AppLog.Tail(msg)}");
            throw new IOException("ffmpeg failed: " + AppLog.Tail(msg));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        await StopRecordingAsync();
        await DisposeCamsAsync();
    }
}
