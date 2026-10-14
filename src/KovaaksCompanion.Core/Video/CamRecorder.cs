using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Video.Mp4;
using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>Records one webcam into its own rolling <see cref="PacketRing"/> on libobs's clock so a clip of it can be cut to the game clip's span.</summary>
public sealed class CamRecorder : IAsyncDisposable
{
    readonly VideoOptions _o;
    readonly string _dir;
    ObsCamCapture? _capture;
    PacketRing? _ring;
    AvcInfo? _avc;

    /// <param name="index">Position in the cam list; names the buffer folder (cam0, cam1, ...).</param>
    public CamRecorder(VideoOptions options, int index) => (_o, _dir) = (options, Path.Combine(options.BufferDir, "cam" + index));

    public bool IsRunning => _capture?.Output.IsActive == true;
    public string LastError { get; private set; } = "";

    public async Task<bool> StartAsync(string deviceId, string encoder, int rotation = 0, WebcamCut? cut = null)
    {
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.GetFiles(_dir, PacketRing.ChunkPrefix + "*" + PacketRing.ChunkExt)) { try { File.Delete(f); } catch (Exception e) { Diagnostics.ErrorReporter.Report("cam chunk delete", e); } }
        var ring = new PacketRing(_dir, TimeSpan.FromMinutes(_o.BufferMinutes));
        var cap = new ObsCamCapture();
        if (!await cap.StartAsync(deviceId, encoder, _o.Quality, rotation, cut, ring.Append))
        {
            LastError = cap.Error;
            cap.Dispose(); ring.Dispose();
            return false;
        }
        (_ring, _capture) = (ring, cap);
        return true;
    }

    /// <summary>
    /// Writes the cam footage for the span of <paramref name="clip"/> (same start and length; the file's time 0 is the game clip's first frame).
    /// False when the cam has nothing buffered for it.
    /// </summary>
    public async Task<bool> ExtractAsync(ClipResult clip, string outputPath)
    {
        var ring = _ring;
        if (ring == null || !ring.HasOrigin) { AppLog.Write("webcam", $"cam ring has no time origin (ring={(ring != null)}); no capture timestamps yet?"); return false; }
        var origin = ring.ToRingSec(VideoRecorder.QpcToNs(clip.ClipStartQpc));
        var end = origin + clip.Duration.TotalSeconds;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (ring.LastVideoSec < end && IsRunning && DateTime.UtcNow < deadline) await Task.Delay(100);
        var avc = Parameters();
        if (avc == null) { AppLog.Write("webcam", "cam has no SPS/PPS"); return false; }
        var span = await Task.Run(() => ring.WriteClip(outputPath, origin, end, avc, null, origin));
        if (span == null) AppLog.Write("webcam", $"cam ring holds no video for {origin:F2}-{end:F2} s (last {ring.LastVideoSec:F2}, first key {ring.FirstKeySec})");
        return span != null;
    }

    AvcInfo? Parameters()
    {
        if (_avc == null && _capture != null && Avc.ParameterSets(_capture.Output.VideoExtraData() ?? []) is { } ps)
            _avc = new AvcInfo(_capture.Width, _capture.Height, ps.Sps, ps.Pps);
        return _avc;
    }

    public async Task StopAsync()
    {
        var cap = _capture;
        if (cap == null) return;
        await Task.Run(() =>
        {
            try { Parameters(); cap.Output.Stop(); var sw = System.Diagnostics.Stopwatch.StartNew(); while (cap.Output.IsActive && sw.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(50); }
            catch (Exception e) { Diagnostics.ErrorReporter.Report("handcam stop", e); }
            _capture = null;
            cap.Dispose();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        var ring = _ring;
        _ring = null;
        await Task.Run(() => ring?.Dispose());
    }
}
