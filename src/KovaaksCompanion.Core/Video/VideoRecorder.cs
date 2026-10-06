using System.Diagnostics;
using System.Globalization;

namespace KovaaksCompanion.Core.Video;

/// <summary>Runs one ffmpeg ddagrab -> HW H.264 -> segment-muxer process as a rolling buffer and tracks segment wall-clock times.</summary>
public sealed class VideoRecorder : IAsyncDisposable
{
    private readonly VideoOptions _o;
    private readonly object _lock = new();
    private readonly List<SegmentInfo> _segments = [];
    private readonly List<(string File, TimeSpan Start, TimeSpan End)> _raw = [];
    private StreamAnchor _anchor = new();
    private Process? _proc;
    private Task? _readTask;

    public VideoRecorder(VideoOptions options) => _o = options;

    public bool IsRunning => _proc is { HasExited: false };
    public string? Encoder { get; private set; }
    public DxgiOutput? Output { get; private set; }
    public QpcClock Clock { get; } = new();
    public string LastError { get; private set; } = "";
    public event Action<SegmentInfo>? SegmentClosed;

    public IReadOnlyList<SegmentInfo> Segments { get { lock (_lock) return _segments.ToList(); } }

    /// <summary>Starts recording; tries each encoder of the fallback chain until one stays alive. Returns false if none works.</summary>
    public async Task<bool> StartAsync()
    {
        Directory.CreateDirectory(_o.BufferDir);
        foreach (var f in Directory.GetFiles(_o.BufferDir, "seg_*.mp4")) TryDelete(f);

        var primary = _o.OutputIndex is null ? MonitorResolver.FindPrimary() : null;
        Output = primary;
        int outIdx = _o.OutputIndex ?? primary?.OutputIndex ?? 0;
        int adapter = _o.OutputIndex is null ? primary?.AdapterIndex ?? _o.AdapterIndex : _o.AdapterIndex;

        var encoders = FfmpegCommand.ChooseEncoders(await RunCaptureAsync(["-hide_banner", "-encoders"]), _o.Encoder);
        foreach (var enc in encoders)
            if (await TryLaunchAsync(enc, outIdx, adapter)) { Encoder = enc; return true; }
        return false;
    }

    private async Task<bool> TryLaunchAsync(string encoder, int outIdx, int adapter)
    {
        lock (_lock) { _segments.Clear(); _raw.Clear(); _anchor = new StreamAnchor(); }
        var psi = new ProcessStartInfo(_o.FfmpegPath)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in FfmpegCommand.BuildRecord(_o, encoder, outIdx, adapter, _o.BufferDir)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        _proc = p;
        var err = new System.Text.StringBuilder();
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
        p.BeginErrorReadLine();
        _readTask = Task.Run(() => ReadSegmentList(p));
        // an unusable encoder/GPU makes ffmpeg exit within a second or two
        await Task.WhenAny(p.WaitForExitAsync(), Task.Delay(2500));
        if (p.HasExited)
        {
            lock (err) LastError = $"{encoder}: {err}";
            _proc = null;
            return false;
        }
        return true;
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
