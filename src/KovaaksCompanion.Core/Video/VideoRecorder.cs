using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video;

/// <summary>Runs one ffmpeg gfxcapture (game window) + process audio -> HW H.264/AAC -> segment-muxer process as a rolling buffer and tracks segment wall-clock times.</summary>
public sealed class VideoRecorder : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly object _lock = new();
    private readonly List<SegmentInfo> _segments = [];
    private readonly List<(string File, TimeSpan Start, TimeSpan End)> _raw = [];
    private DateTime _streamEpochUtc;
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
    /// <summary>Captured window region (null = whole monitor).</summary>
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
            if (Target is null) { LastError = ct.IsCancellationRequested ? "cancelled" : "game exited before its window appeared"; return false; }
            AppLog.Write("video", $"target: game window process={Target.ProcessId} hwnd={Target.Hwnd} {Target.Width}x{Target.Height}");
            Output = Target.Output; outIdx = Target.Output?.OutputIndex ?? 0; adapter = Target.Output?.AdapterIndex ?? _o.AdapterIndex;
        }
        else
        {
            var primary = _o.OutputIndex is null ? MonitorResolver.FindPrimary() : null;
            Output = primary;
            outIdx = _o.OutputIndex ?? primary?.OutputIndex ?? 0;
            adapter = _o.OutputIndex is null ? primary?.AdapterIndex ?? _o.AdapterIndex : _o.AdapterIndex;
            AppLog.Write("video", $"target: whole monitor adapter {adapter} output {outIdx}");
        }

        var probe = await RunCaptureAsync(["-hide_banner", "-encoders"]);
        var encoders = FfmpegCommand.ChooseEncoders(probe, _o.Encoder);
        AppLog.Write("video", $"encoder probe: {probe.Length} chars, candidates [{string.Join(", ", encoders)}] (requested '{_o.Encoder}')");
        foreach (var enc in encoders)
            if (await TryLaunchAsync(enc, outIdx, adapter)) { Encoder = enc; AppLog.Write("video", "recording with encoder " + enc); return true; }
        if (encoders.Count == 0) LastError = "no hardware H.264 encoder (NVENC/AMF/QSV) found";
        AppLog.Write("video", "no encoder worked: " + LastError);
        return false;
    }

    private Task<CaptureTarget?> WaitForWindowAsync(CancellationToken ct)
        => GameWindow.WaitAsync(() => GameWindow.Find(_o.GameProcessName), () => GameWindow.IsProcessAlive(_o.GameProcessName), TimeSpan.FromMilliseconds(500), ct);

    private async Task<bool> TryLaunchAsync(string encoder, int outIdx, int adapter)
    {
        lock (_lock) { _segments.Clear(); _raw.Clear(); }
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
            else { AudioError = audio.Error; audio.Dispose(); AppLog.Write("audio", "capture init failed: " + AudioError); }
        }
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        var epoch = DateTime.UtcNow;
        _streamEpochUtc = epoch;
        foreach (var a in FfmpegCommand.BuildRecord(_o, encoder, outIdx, adapter, _o.BufferDir, (epoch - DateTime.UnixEpoch).Ticks / 10, Target, pipePath)) psi.ArgumentList.Add(a);
        AppLog.Write("ffmpeg", $"start ({encoder}): {psi.FileName} {string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        _proc = p;
        p.EnableRaisingEvents = true;
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) { err.AppendLine(e.Data); FfmpegLog = err.ToString(); } };
        p.Exited += (_, _) =>
        {
            string log; lock (err) log = err.ToString();
            AppLog.Write("ffmpeg", $"exited code={TryExitCode(p)}; stderr tail:{Environment.NewLine}{AppLog.Tail(log)}");
        };
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
        var first = true;
        while ((line = p.StandardOutput.ReadLine()) != null)
        {
            if (first) { first = false; continue; } // the muxer reports 0 as the first segment's start, not its real pts
            var parts = line.Split(',');
            if (parts.Length < 3 ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var e)) continue;
            SegmentInfo info;
            lock (_lock)
            {
                _raw.RemoveAll(r => r.File == parts[0]);   // slot wrapped: its older content is gone
                _raw.Add((parts[0], TimeSpan.FromSeconds(s), TimeSpan.FromSeconds(e)));
                _segments.Clear();
                foreach (var r in _raw)
                    _segments.Add(new SegmentInfo(Path.Combine(_o.BufferDir, r.File), _streamEpochUtc + r.Start, r.End - r.Start));
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
        await File.WriteAllTextAsync(list, FfmpegCommand.BuildConcatList(sel));
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
        if (throwOnFail && p.ExitCode != 0)
        {
            var msg = await se;
            AppLog.Write("ffmpeg", $"command failed code={p.ExitCode}: {string.Join(' ', psi.ArgumentList)}{Environment.NewLine}{AppLog.Tail(msg)}");
            throw new IOException("ffmpeg failed: " + msg);
        }
        return await so;
    }

    private static string TryExitCode(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    private static void TryDelete(string f) { try { File.Delete(f); } catch { } }

    public async ValueTask DisposeAsync() => await StopAsync();
}
