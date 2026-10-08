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

    /// <summary>Size of the recorded game clip (the capped output size, not the raw window size); 0x0 without a target.</summary>
    private (int Width, int Height) ClipSize(CaptureTarget? t) => t == null ? (0, 0) : FfmpegCommand.OutputSize(t.Width, t.Height, _o.MaxHeight);

    private async Task StartHandCamAsync(string encoder)
    {
        await DisposeCamsAsync();
        var cfg = _handCam?.Invoke();
        var cams = cfg?.Cams ?? [];
        var (gameWidth, gameHeight) = ClipSize(_rec?.Target);
        for (int i = 0; i < cams.Count; i++)
        {
            try
            {
                // Composite only shows the cam in its box, so record it cropped to that size (Separate keeps the cam's own resolution)
                var (width, height) = cfg!.Save == HandCamSave.Composite && gameWidth > 0 ? FfmpegCommand.CamBox(gameWidth, gameHeight, cams[i].Layout) : (0, 0);
                var cam = new HandCamRecorder(_o.FfmpegPath, cams[i], i, _o.BufferDir, encoder, _o.Quality, _o.BufferMinutes, width, height);
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
    public async Task<ClipResult?> ExtractClipAsync(DateTime startUtc, DateTime endUtc, string outputPath, Action<double>? progress = null)
    {
        if (_rec is null) return null;
        progress?.Invoke(0.02);
        var clip = await _rec.ExtractClipAsync(startUtc, endUtc, outputPath);
        if (clip != null && _cams.Count > 0 && _rec.Encoder is { } enc)
        {
            progress?.Invoke(0.15);
            try { await ComposeAsync(clip, outputPath, enc, progress); }
            catch (Exception e) { AppLog.Write("handcam", "hand cam failed, keeping plain clip: " + e.Message); Error?.Invoke("hand cam not added: " + e.Message); }
        }
        progress?.Invoke(1);
        return clip;
    }

    /// <summary>Per the save mode: video.mp4 = composite (Composite) or plain game (Separate); handcamN.mp4 = cam N aligned to the game clip's timeline (Separate).</summary>
    private async Task ComposeAsync(ClipResult clip, string clipPath, string encoder, Action<double>? progress = null)
    {
        var dur = clip.Duration.TotalSeconds;
        // 0.15..0.45 cam extraction, 0.45..1 composite (the long encode)
        void Report(double from, double to, double f) => progress?.Invoke(from + (to - from) * f);
        var cfg = _handCam!.Invoke();
        var dir = Path.GetDirectoryName(clipPath)!;
        var merged = clipPath + ".merged.mp4";
        var temps = new List<string> { merged };
        try
        {
            var inputs = new List<FfmpegCommand.CamInput>();
            var camList = _cams.ToList();
            var separate = cfg.Save != HandCamSave.Composite;
            var span = cfg.Save == HandCamSave.Separate ? 0.85 : 0.30;
            for (var ci = 0; ci < camList.Count; ci++)
            {
                var (slot, config, recorder) = camList[ci];
                double lo = 0.15 + span * ci / camList.Count, hi = 0.15 + span * (ci + 1) / camList.Count;
                var raw = $"{clipPath}.cam{slot}.mp4";
                temps.Add(raw);
                var camStart = await recorder.ExtractAsync(clip.ClipStartUtc, clip.ClipStartUtc + clip.Duration, raw, a => RunFfmpegAsync(a));
                if (camStart is null) { AppLog.Write("handcam", $"no footage from cam {slot + 1} for this clip"); continue; }
                var shift = (camStart.Value - clip.ClipStartUtc).TotalSeconds;
                inputs.Add(new FfmpegCommand.CamInput(raw, shift, config.Layout));
                if (separate)
                {
                    var mid = (lo + hi) / 2;
                    Report(lo, mid, 1);
                    await RunFfmpegAsync(FfmpegCommand.BuildCamAlign(raw, shift, dur, encoder, _o.Quality, Path.Combine(dir, $"handcam{slot + 1}.mp4")), dur, f => Report(mid, hi, f));
                }
                else Report(lo, hi, 1);
            }
            if (inputs.Count == 0 || cfg.Save == HandCamSave.Separate) return;
            void Composite(double f) => Report(0.45, 1, f);
            var (clipW, clipH) = ClipSize(_rec?.Target);
            if (clipW == 0) (clipW, clipH) = (1920, 1080);
            if (encoder == "h264_nvenc" && _rec?.Target is { Width: > 0 })
            {
                try { await RunFfmpegAsync(FfmpegCommand.BuildCompositeCuda(clipPath, clipW, clipH, inputs, _o.Quality, merged), dur, Composite); }
                catch (Exception e) { AppLog.Write("handcam", "GPU composite failed, using CPU overlay: " + e.Message); File.Delete(merged); await RunFfmpegAsync(FfmpegCommand.BuildComposite(clipPath, clipW, clipH, inputs, encoder, _o.Quality, merged), dur, Composite); }
            }
            else await RunFfmpegAsync(FfmpegCommand.BuildComposite(clipPath, clipW, clipH, inputs, encoder, _o.Quality, merged), dur, Composite);
            File.Move(merged, clipPath, true);
        }
        finally
        {
            foreach (var f in temps) { try { File.Delete(f); } catch { } }
        }
    }

    /// <param name="progress">Called with 0..1 of <paramref name="totalSec"/> of output produced (ffmpeg -progress).</param>
    private async Task RunFfmpegAsync(IEnumerable<string> args, double totalSec = 0, Action<double>? progress = null)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var track = progress != null && totalSec > 0;
        if (track) { psi.ArgumentList.Add("-progress"); psi.ArgumentList.Add("pipe:1"); psi.ArgumentList.Add("-nostats"); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                if (track && line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us) && us >= 0)
                    progress!(Math.Clamp(us / 1e6 / totalSec, 0, 1));
        });
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
