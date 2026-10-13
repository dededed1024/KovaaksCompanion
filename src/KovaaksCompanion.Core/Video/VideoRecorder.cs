using System.Diagnostics;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Video.Mp4;
using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>
/// Records the game window + its audio (+ composited hand cams) through libobs into a rolling <see cref="PacketRing"/> and cuts clips out of it on demand.
/// </summary>
public sealed class VideoRecorder : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private ObsCapture? _capture;
    private PacketRing? _ring;
    private AvcInfo? _avc;
    private AacInfo? _aac;

    public VideoRecorder(VideoOptions options) => _o = options;

    public bool IsRunning => _capture?.Output.IsActive == true;
    /// <summary>libobs id of the encoder in use.</summary>
    public string? Encoder { get; private set; }
    public string LastError { get; private set; } = "";
    /// <summary>Why the clip has no audio (empty = audio recorded or not requested).</summary>
    public string AudioError { get; private set; } = "";
    /// <summary>Raised on a thread-pool thread when a running recording turns out not to capture the game.</summary>
    public event Action<string>? Problem;
    /// <summary>Captured window.</summary>
    public CaptureTarget? Target { get; private set; }
    /// <summary>Size of the recorded video (the capped output size, not the raw window size); 0x0 before start.</summary>
    public (int Width, int Height) VideoSize { get; private set; }
    /// <summary>Cam sources composited into the video, in the order they were given.</summary>
    public IReadOnlyList<nint> CamSources => _capture?.CamSources ?? [];

    /// <summary>Starts recording; tries each encoder of the fallback chain until one starts. Returns false if none works.</summary>
    public async Task<bool> StartAsync(IReadOnlyList<ObsCamSpec> cams, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.BufferDir);
        foreach (var f in Directory.GetFiles(_o.BufferDir, PacketRing.ChunkPrefix + "*" + PacketRing.ChunkExt)) TryDelete(f);

        Target = await GameWindow.WaitAsync(() => GameWindow.Find(_o.GameProcessName), () => GameWindow.IsProcessAlive(_o.GameProcessName), TimeSpan.FromMilliseconds(500), ct);
        if (Target is null) { LastError = ct.IsCancellationRequested ? "cancelled" : "game exited before its window appeared"; return false; }
        AppLog.Write("video", $"target: game window process={Target.ProcessId} hwnd={Target.Hwnd} {Target.Width}x{Target.Height}");

        // libobs wants an MTA thread for its COM use; the UI thread is STA
        if (!await Task.Run(() => ObsRuntime.Start(_o.ObsRoot))) { LastError = "libobs: " + ObsRuntime.LastError; return false; }
        var encoders = await Task.Run(() => ObsCapture.AvailableEncoders(_o.Encoder));
        AppLog.Write("video", $"encoder candidates [{string.Join(", ", encoders)}] (requested '{_o.Encoder}')");
        if (encoders.Count == 0) { LastError = "no hardware H.264 encoder (NVENC/AMF/QSV) found"; return false; }

        foreach (var enc in encoders)
        {
            var ring = new PacketRing(_o.BufferDir, TimeSpan.FromMinutes(_o.BufferMinutes));
            var cap = new ObsCapture(new ObsCaptureSpec(Target, _o.GameProcessName, _o.Fps, _o.Quality, _o.MaxHeight, enc, _o.CaptureAudio, cams));
            var ok = await Task.Run(() => cap.Start(ring.Append));
            if (ok)
            {
                (_ring, _capture, Encoder, VideoSize) = (ring, cap, enc, (cap.OutputWidth, cap.OutputHeight));
                AudioError = _o.CaptureAudio && !cap.HasAudio ? "process audio capture unavailable" : "";
                AppLog.Write("video", $"recording with encoder {enc} at {cap.OutputWidth}x{cap.OutputHeight}");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    if (_capture != cap) return;
                    var (w, h) = cap.SourceSize();
                    AppLog.Write("video", $"game source size {w}x{h}");
                    if (w == 0 || h == 0) { AppLog.Write("video", "game capture delivers no frames"); Problem?.Invoke("game capture delivers no frames (window not captured)"); }
                });
                if (cap.CamSources.Count > 0) _ = Task.Run(async () => { await Task.Delay(2000); if (_capture == cap) cap.FixCamModes(); });
                _ = Task.Run(async () => { for (int i = 0; i < 40 && _avc == null && _capture == cap; i++) { Parameters(cap); if (_avc == null) await Task.Delay(250); } });
                return true;
            }
            LastError = $"{enc}: {cap.Error}";
            AppLog.Write("video", "encoder failed: " + LastError);
            cap.Dispose(); ring.Dispose();
        }
        return false;
    }

    /// <summary>
    /// Cuts [start, end] +/- margin out of the buffer. Waits (up to maxWait) for the buffer to reach end+margin; if recording already stopped, uses whatever was captured.
    /// The clip starts at the keyframe at or before start - margin; <see cref="ClipResult.ClipStartUtc"/> is that frame's time.
    /// </summary>
    public async Task<ClipResult?> ExtractClipAsync(DateTime startUtc, DateTime endUtc, string outputPath, TimeSpan? maxWait = null)
    {
        var ring = _ring;
        if (ring == null || !ring.HasOrigin) return null;
        startUtc = startUtc.ToUniversalTime(); endUtc = endUtc.ToUniversalTime();
        // only picks the span to cut (the margin absorbs any error); the clip's own start stays on the QPC clock
        var clock = new QpcClock();
        double from = ToRingSec(ring, clock, startUtc - _o.ClipMargin), to = ToRingSec(ring, clock, endUtc + _o.ClipMargin);
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(5));
        while (ring.LastVideoSec < to && IsRunning && DateTime.UtcNow < deadline) await Task.Delay(100);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var (avc, aac) = Parameters(_capture);
        if (avc == null) return null;
        var span = await Task.Run(() => ring.WriteClip(outputPath, from, to, avc, aac));
        if (span is not { } s) return null;
        var startQpc = NsToQpc(ring.ToQpcNs(s.Start));
        return new ClipResult(outputPath, clock.ToUtc(startQpc), startQpc, TimeSpan.FromSeconds(s.End - s.Start + 1.0 / Math.Max(1, _o.Fps)));
    }

    /// <summary>Stream parameters (SPS/PPS, AAC config) read from the running encoders once, then cached.</summary>
    (AvcInfo? Avc, AacInfo? Aac) Parameters(ObsCapture? cap)
    {
        if (cap == null) return (_avc, _aac);
        if (_avc == null && Avc.ParameterSets(cap.Output.VideoExtraData() ?? []) is { } ps) _avc = new AvcInfo(cap.OutputWidth, cap.OutputHeight, ps.Sps, ps.Pps);
        if (_aac == null && cap.HasAudio && cap.Output.AudioExtraData() is { Length: >= 2 } asc) _aac = new AacInfo(48000, 2, asc[..2]); // AAC-LC; libobs appends SBR signalling that MP4 readers do not want
        return (_avc, _aac);
    }

    /// <summary>The ring's time (seconds on the encoder clock) of a wall-clock instant.</summary>
    static double ToRingSec(PacketRing ring, QpcClock clock, DateTime utc) => ring.ToRingSec(QpcToNs(clock.ToQpc(utc)));

    internal static long QpcToNs(long ticks) => (long)(ticks * 1e9 / Stopwatch.Frequency);
    static long NsToQpc(long ns) => (long)(ns * (double)Stopwatch.Frequency / 1e9);

    /// <summary>Stops the encoders and releases the capture; the buffered footage stays cuttable until <see cref="DisposeAsync"/>.</summary>
    public async Task StopAsync()
    {
        var cap = _capture;
        if (cap == null) return;
        await Task.Run(() =>
        {
            try
            {
                Parameters(cap);
                cap.Output.Stop();
                var sw = Stopwatch.StartNew();
                while (cap.Output.IsActive && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50);
            }
            catch (Exception e) { AppLog.Write("video", "stop failed: " + e.Message); }
            _capture = null;
            cap.Dispose();
        });
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        var ring = _ring;
        _ring = null;
        await Task.Run(() => ring?.Dispose());
    }
}
