using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace KovaaksCompanion.Core.Video;

/// <summary>Runs one ffmpeg ddagrab (game window) + process audio -> HW H.264/AAC -> segment-muxer process as a rolling buffer and tracks segment wall-clock times.</summary>
public sealed class VideoRecorder : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly object _lock = new();
    private readonly List<SegmentInfo> _segments = [];
    private readonly List<(string File, TimeSpan Start, TimeSpan End)> _raw = [];
    private StreamAnchor _anchor = new();
    private Process? _proc;
    private Task? _readTask;
    private ProcessAudioCapture? _audio;
    private NamedPipeServerStream? _audioPipe;
    private CancellationTokenSource? _audioCts;
    private Task? _audioTask;

    public VideoRecorder(VideoOptions options) => _o = options;

    public bool IsRunning => _proc is { HasExited: false };
    public string? Encoder { get; private set; }
    public DxgiOutput? Output { get; private set; }
    public QpcClock Clock { get; } = new();
    public string LastError { get; private set; } = "";
    /// <summary>stderr (warnings/errors) of the current ffmpeg process.</summary>
    public string FfmpegLog { get; private set; } = "";
    /// <summary>Why the clip has no audio (empty = audio recorded or not requested).</summary>
    public string AudioError { get; private set; } = "";
    /// <summary>Resolved frame rate and the captured window region (null = whole monitor).</summary>
    public int Fps { get; private set; }
    public CaptureTarget? Target { get; private set; }
    public event Action<SegmentInfo>? SegmentClosed;

    public IReadOnlyList<SegmentInfo> Segments { get { lock (_lock) return _segments.ToList(); } }

    /// <summary>Starts recording; tries each encoder of the fallback chain until one stays alive. Returns false if none works.</summary>
    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.BufferDir);
        foreach (var f in Directory.GetFiles(_o.BufferDir, "seg_*.mp4")) TryDelete(f);

        int outIdx, adapter;
        if (_o.CaptureWindow)
        {
            Target = await WaitForWindowAsync(ct);
            if (Target is null) { LastError = ct.IsCancellationRequested ? "cancelled" : "game window not found"; return false; }
            Output = Target.Output; outIdx = Target.Output.OutputIndex; adapter = Target.Output.AdapterIndex;
            Fps = _o.Fps > 0 ? _o.Fps : Target.RefreshHz;
        }
        else
        {
            var primary = _o.OutputIndex is null ? MonitorResolver.FindPrimary() : null;
            Output = primary;
            outIdx = _o.OutputIndex ?? primary?.OutputIndex ?? 0;
            adapter = _o.OutputIndex is null ? primary?.AdapterIndex ?? _o.AdapterIndex : _o.AdapterIndex;
            Fps = _o.Fps > 0 ? _o.Fps : Output is null ? 60 : GameWindow.RefreshRate(Output);
        }

        var encoders = FfmpegCommand.ChooseEncoders(await RunCaptureAsync(["-hide_banner", "-encoders"]), _o.Encoder);
        foreach (var enc in encoders)
            if (await TryLaunchAsync(enc, outIdx, adapter)) { Encoder = enc; return true; }
        return false;
    }

    private async Task<CaptureTarget?> WaitForWindowAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + _o.WindowWait;
        while (!ct.IsCancellationRequested)
        {
            var t = GameWindow.Find(_o.GameProcessName);
            if (t != null) return t;
            if (DateTime.UtcNow > deadline) break;
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { break; }
        }
        return null;
    }

    private async Task<bool> TryLaunchAsync(string encoder, int outIdx, int adapter)
    {
        lock (_lock) { _segments.Clear(); _raw.Clear(); _anchor = new StreamAnchor(); }
        string? pipePath = null;
        AudioError = "";
        if (_o.CaptureAudio && Target != null)
        {
            var audio = new ProcessAudioCapture(Target.ProcessId);
            if (audio.TryInitialize())
            {
                var name = "kc_audio_" + Guid.NewGuid().ToString("N");
                _audio = audio;
                _audioPipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte);
                pipePath = @"\\.\pipe\" + name;
            }
            else { AudioError = audio.Error; audio.Dispose(); }
        }
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in FfmpegCommand.BuildRecord(_o with { Fps = Fps }, encoder, outIdx, adapter, _o.BufferDir, Target, pipePath)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        _proc = p;
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); FfmpegLog = err.ToString(); } };
        p.BeginErrorReadLine();
        _readTask = Task.Run(() => ReadSegmentList(p));
        StartAudioPump();
        // an unusable encoder/GPU makes ffmpeg exit within a second or two
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(2500));
        if (p.HasExited)
        {
            lock (err) LastError = $"{encoder}: {err}";
            await StopAudioAsync();
            _proc = null;
            return false;
        }
        return true;
    }

    private void StartAudioPump()
    {
        if (_audio is null || _audioPipe is null) return;
        var (audio, pipe) = (_audio, _audioPipe);
        var cts = _audioCts = new CancellationTokenSource();
        _audioTask = Task.Run(async () =>
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(10));
                await pipe.WaitForConnectionAsync(connect.Token);
                audio.Pump(pipe, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { AudioError = e.Message; }
        });
    }

    private async Task StopAudioAsync()
    {
        _audioCts?.Cancel();
        if (_audioTask != null) await Task.WhenAny(_audioTask, Task.Delay(2000));
        _audioPipe?.Dispose(); _audio?.Dispose(); _audioCts?.Dispose();
        _audioPipe = null; _audio = null; _audioCts = null; _audioTask = null;
    }

    private void ReadSegmentList(Process p)
    {
        string? line;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            var arrival = DateTime.UtcNow;
            var parts = line.Split(',');
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) continue;
            SegmentInfo info;
            lock (_lock)
            {
                _anchor.AddSegmentClosed(arrival, TimeSpan.FromSeconds(e));
                _raw.RemoveAll(r => r.File == parts[0]);   // slot wrapped: its older content is gone
                _raw.Add((parts[0], TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e)));
                _segments.Clear();                          // re-derive all with the refreshed anchor
                foreach (var r in _raw)
                    _segments.Add(new SegmentInfo(Path.Combine(_o.BufferDir, r.File), _anchor.ToUtc(r.Start), r.End - r.Start));
                info = _segments[^1];
            }
            SegmentClosed?.Invoke(info);
        }
    }

    /// <summary>Graceful stop ('q' on stdin so the open segment is finalized); kills after the timeout.</summary>
    public async Task StopAsync(TimeSpan? timeout = null)
    {
        var p = _proc;
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { await p.StandardInput.WriteAsync("q"); await p.StandardInput.FlushAsync(); } catch { }
                await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(timeout ?? TimeSpan.FromSeconds(5)));
                if (!p.HasExited) { try { p.Kill(true); } catch { } await p.WaitForExitAsync(); }
            }
            if (_readTask != null) await Task.WhenAny(_readTask, Task.Delay(2000));
            await StopAudioAsync();
        }
        finally { p.Dispose(); _proc = null; }
    }

    /// <summary>
    /// Cuts [start, end] +/- margin out of the buffer with -c copy. Waits (up to maxWait) for the segment
    /// containing end+margin to close; if recording already stopped, uses whatever was finalized.
    /// </summary>
    public async Task<ClipResult?> ExtractClipAsync(DateTime startUtc, DateTime endUtc, string outputPath, TimeSpan? maxWait = null)
    {
        startUtc = startUtc.ToUniversalTime(); endUtc = endUtc.ToUniversalTime();
        var deadline = DateTime.UtcNow + (maxWait ?? TimeSpan.FromSeconds(_o.SegmentSeconds + 5));
        List<SegmentInfo> sel;
        while (true)
        {
            sel = SegmentSelector.Select(Segments, startUtc, endUtc, _o.ClipMargin);
            if (sel.Count > 0 && sel[^1].EndUtc >= endUtc + _o.ClipMargin) break;
            if (!IsRunning || DateTime.UtcNow > deadline) break;
            await Task.Delay(200);
        }
        if (sel.Count == 0) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var list = outputPath + ".txt";
        await File.WriteAllTextAsync(list, FfmpegCommand.BuildConcatList(sel.Select(s => s.File)));
        try { await RunCaptureAsync(FfmpegCommand.BuildConcat(list, outputPath), throwOnFail: true); }
        finally { TryDelete(list); }
        var first = sel[0].StartUtc;
        return new ClipResult(outputPath, first, Clock.ToQpc(first), sel[^1].EndUtc - first);
    }

    private async Task<string> RunCaptureAsync(IEnumerable<string> args, bool throwOnFail = false)
    {
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (throwOnFail && p.ExitCode != 0) throw new IOException("ffmpeg failed: " + await se);
        return await so;
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    public async ValueTask DisposeAsync() => await StopAsync();
}
