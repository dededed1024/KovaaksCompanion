using System.Globalization;

namespace KovaaksCompanion.Core.Video;

/// <summary>Pure builders for ffmpeg command lines and encoder choice.</summary>
public static class FfmpegCommand
{
    public static readonly string[] EncoderChain = ["h264_nvenc", "h264_amf", "h264_qsv"];

    /// <summary>Chain order filtered by what `ffmpeg -encoders` lists. Hardware encoders only (no software fallback); empty when none is listed.</summary>
    public static IReadOnlyList<string> ChooseEncoders(string ffmpegEncodersOutput, string? forced = null)
    {
        if (!string.IsNullOrWhiteSpace(forced)) return [forced];
        var list = EncoderChain.Where(e => ffmpegEncodersOutput.Contains(" " + e + " ", StringComparison.Ordinal)).ToList();
        return list;
    }

    public const string SegmentFilePattern = "seg_%03d.mp4";

    /// <summary>
    /// Arguments for the rolling-buffer recorder. Segment list (csv) goes to stdout; <paramref name="audioPipe"/> adds raw
    /// 48 kHz s16 stereo PCM read from that named pipe. With a window <paramref name="target"/> (Hwnd set) the source is gfxcapture
    /// (only that window, no overlays); otherwise ddagrab of the whole output (or its crop region). Video/audio pts are wall-clock
    /// seconds since <paramref name="epochUnixUs"/> (UTC µs since Unix epoch, chosen by the caller): per-frame RTCTIME for both
    /// sources, the same wall clock the mouse timeline is anchored to, so the video cannot drift against it. Segment csv start
    /// times are seconds since that epoch (the first segment of a run reports 0 regardless).
    /// </summary>
    public static List<string> BuildRecord(VideoOptions o, string encoder, int outputIdx, int adapterIdx, string bufferDir, long epochUnixUs,
        CaptureTarget? target = null, string? audioPipe = null)
    {
        var inv = CultureInfo.InvariantCulture;
        int fps = Math.Max(1, o.Fps);
        bool hwFrames = encoder is "h264_nvenc" or "h264_amf" or "hevc_nvenc";
        var e = epochUnixUs.ToString(inv);
        bool window = target is { Hwnd: not 0 };
        string src;
        if (window)
        {
            // scale (not crop) to the capped size; without a cap the window's own size is kept
            string size = "width=-2:height=-2";
            if (o.MaxHeight is int cap && target!.Height > cap)
            {
                var (w, h) = OutputSize(target.Width, target.Height, cap);
                size = $"width={w.ToString(inv)}:height={h.ToString(inv)}:resize_mode=scale";
            }
            src = $"gfxcapture=hwnd={target!.Hwnd.ToString(inv)}:max_framerate={fps.ToString(inv)}:capture_cursor=0:{size}," +
                  $"setpts=(RTCTIME-{e})/(TB*1000000)";
        }
        else
        {
            src = $"ddagrab=output_idx={outputIdx.ToString(inv)}:framerate={fps.ToString(inv)}:draw_mouse=0";
            if (target != null)
                src += $":video_size={target.Width.ToString(inv)}x{target.Height.ToString(inv)}:offset_x={target.X.ToString(inv)}:offset_y={target.Y.ToString(inv)}";
            src += $",setpts=(RTCTIME-{e})/(TB*1000000)"; // no commas: safe inside the lavfi graph
        }
        var filter = hwFrames ? src : src + ",hwdownload,format=bgra,format=yuv420p";
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats", "-copyts", // keep the wall-clock pts (otherwise ffmpeg shifts the first frame to 0)
        };
        if (!window) a.AddRange(["-init_hw_device", $"d3d11va=dda:{adapterIdx}", "-filter_hw_device", "dda"]);
        a.AddRange(["-f", "lavfi", "-i", filter]);
        if (audioPipe != null)
            a.AddRange(["-thread_queue_size", "1024", "-f", "s16le", "-ar", ProcessAudioCapture.SampleRate.ToString(inv),
                "-ac", ProcessAudioCapture.Channels.ToString(inv), "-i", audioPipe, "-map", "0:v:0", "-map", "1:a:0",
                "-af", $"asetpts=N/SR/TB+(RTCSTART-{e})/(TB*1000000)", "-c:a", "aac", "-b:a", "192k"]);
        a.AddRange(["-c:v", encoder, "-fps_mode:v", "passthrough"]);
        a.AddRange(EncoderArgs(encoder, o.Quality));
        a.AddRange(["-g", fps.ToString(inv), "-keyint_min", fps.ToString(inv),
            "-f", "segment", "-segment_time", o.SegmentSeconds.ToString(inv),
            "-segment_wrap", o.SlotCount.ToString(inv), "-reset_timestamps", "1",
            "-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_flags", "live",
            "-segment_format", "mp4",
            Path.Combine(bufferDir, SegmentFilePattern)]);
        return a;
    }

    /// <summary>Even output size for a <paramref name="srcWidth"/>x<paramref name="srcHeight"/> source capped to <paramref name="maxHeight"/> (aspect kept, never upscaled).</summary>
    public static (int Width, int Height) OutputSize(int srcWidth, int srcHeight, int? maxHeight)
    {
        if (maxHeight is not int cap || srcHeight <= cap)
            return (Math.Max(2, srcWidth & ~1), Math.Max(2, srcHeight & ~1));
        int h = Math.Max(2, cap & ~1);
        int w = (int)Math.Round((long)srcWidth * h / (double)srcHeight);
        return (Math.Max(2, w & ~1), h);
    }

    public static IEnumerable<string> EncoderArgs(string enc, int q) => enc switch
    {
        "h264_nvenc" or "hevc_nvenc" => ["-preset", "p4", "-tune", "ll", "-rc", "vbr", "-cq", q.ToString(), "-b:v", "0", "-maxrate", "150M", "-bf", "0", "-forced-idr", "1"],
        "h264_amf" => ["-quality", "speed", "-rc", "cqp", "-qp_i", q.ToString(), "-qp_p", q.ToString(), "-bf", "0"],
        "h264_qsv" => ["-preset", "veryfast", "-global_quality", q.ToString(), "-bf", "0"],
        _ => ["-pix_fmt", "yuv420p"],
    };

    /// <summary>
    /// Hand-cam recorder: DirectShow video device -> HW H.264 -> 2 s rolling segments. Pts are wall-clock seconds since <paramref name="epochUnixUs"/>
    /// (per-frame RTCTIME, the same clock as <see cref="BuildRecord"/>). <paramref name="forceMode"/> asks the device for 1280x720@30 first.
    /// <paramref name="targetWidth"/> x <paramref name="targetHeight"/> &gt; 0 scales to cover that box and crops the center (the size the composite will show it at), so the always-on encode stays small.
    /// </summary>
    public static List<string> BuildHandCamRecord(string device, int deviceNumber, string encoder, int quality, string dir, long epochUnixUs, int slots, bool forceMode, int targetWidth = 0, int targetHeight = 0)
    {
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-copyts", "-f", "dshow", "-rtbufsize", "256M" };
        if (forceMode) a.AddRange(["-video_size", "1280x720", "-framerate", HandCamRecorder.Fps.ToString(inv)]);
        if (deviceNumber > 0) a.AddRange(["-video_device_number", deviceNumber.ToString(inv)]);
        a.AddRange(["-i", "video=" + device,
            "-vf", $"setpts=(RTCTIME-{epochUnixUs.ToString(inv)})/(TB*1000000)" + (targetWidth > 0 && targetHeight > 0 ? $",{CoverFilter(targetWidth, targetHeight)}" : "") + ",format=yuv420p",
            "-c:v", encoder, "-fps_mode:v", "passthrough"]);
        a.AddRange(EncoderArgs(encoder, quality));
        a.AddRange(["-g", HandCamRecorder.Fps.ToString(inv), "-keyint_min", HandCamRecorder.Fps.ToString(inv),
            "-f", "segment", "-segment_time", HandCamRecorder.SegmentSeconds.ToString(inv),
            "-segment_wrap", slots.ToString(inv), "-reset_timestamps", "1",
            "-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_flags", "live",
            "-segment_format", "mp4", Path.Combine(dir, HandCamRecorder.SegmentFilePattern)]);
        return a;
    }

    /// <summary>Box the cam occupies in a game video of the given size: width / height percent of the video, even pixels.</summary>
    public static (int Width, int Height) CamBox(int gameWidth, int gameHeight, HandCamLayout l)
        => (Math.Max(16, gameWidth * Math.Clamp(l.SizePercent, 5, 60) / 100 / 2 * 2), Math.Max(16, gameHeight * Math.Clamp(l.HeightPercent, 5, 60) / 100 / 2 * 2));

    /// <summary>Scales to fill w x h (overflow cropped around the center).</summary>
    static string CoverFilter(int w, int h)
    {
        var inv = CultureInfo.InvariantCulture;
        return $"scale={w.ToString(inv)}:{h.ToString(inv)}:force_original_aspect_ratio=increase,crop={w.ToString(inv)}:{h.ToString(inv)}";
    }

    /// <summary>Keyframe interval (frames) for re-encoded clips; without it NVENC leaves one GOP for the whole clip and seeking/playback stutters.</summary>
    const int ReencodeGop = 60;

    static readonly string[] GopArgs = ["-g", ReencodeGop.ToString(), "-keyint_min", ReencodeGop.ToString()];

    /// <summary>One cam clip to overlay: its wall-clock start minus the game clip's start (positive delays it, negative trims its head) and its place.</summary>
    public sealed record CamInput(string Clip, double ShiftSec, HandCamLayout Layout);

    /// <summary>
    /// Overlays each cam clip onto the game clip in order (audio copied, video re-encoded). The last frame of a cam stays until its next one arrives.
    /// </summary>
    public static List<string> BuildComposite(string gameClip, int gameWidth, int gameHeight, IReadOnlyList<CamInput> cams, string encoder, int quality, string output)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        var cur = "0:v";
        for (int i = 0; i < cams.Count; i++)
        {
            var c = cams[i];
            var (camW, camH) = CamBox(gameWidth, gameHeight, c.Layout);
            double right = Math.Clamp(c.Layout.RightPercent, 0, 100) / 100.0, top = Math.Clamp(c.Layout.TopPercent, 0, 100) / 100.0;
            var shift = c.ShiftSec >= 0
                ? $"setpts=PTS-STARTPTS+{c.ShiftSec.ToString("0.######", inv)}/TB"
                : $"trim=start={(-c.ShiftSec).ToString("0.######", inv)},setpts=PTS-STARTPTS";
            var n = i + 1;
            // cam fills a width% x height% box of the game frame (cover, centered crop); position is clamped inside the frame
            sb.Append($"[{n}:v]{shift},{CoverFilter(camW, camH)}[c{n}];");
            sb.Append($"[{cur}][c{n}]overlay=x='max(0,min(main_w-overlay_w,main_w-overlay_w-main_w*{right.ToString("0.####", inv)}))':y='max(0,min(main_h-overlay_h,main_h*{top.ToString("0.####", inv)}))':eof_action=pass:repeatlast=1[o{n}];");
            cur = $"o{n}";
        }
        sb.Append($"[{cur}]format=yuv420p[v]");
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "d3d11va", "-i", gameClip };
        foreach (var c in cams) a.AddRange(["-hwaccel", "d3d11va", "-i", c.Clip]);
        a.AddRange(["-filter_complex", sb.ToString(), "-map", "[v]", "-map", "0:a?", "-c:a", "copy", "-c:v", encoder, "-fps_mode:v", "passthrough"]);
        a.AddRange(EncoderArgs(encoder, quality));
        a.AddRange(GopArgs);
        a.AddRange(["-movflags", "+faststart", output]);
        return a;
    }

    /// <summary>
    /// <see cref="BuildComposite"/> kept on the GPU (NVIDIA only): CUDA decode, scale_cuda + overlay_cuda, NVENC. Frames never reach system memory.
    /// scale_cuda has no "scale to a reference" variant and no crop, so the box comes from <paramref name="gameWidth"/> x <paramref name="gameHeight"/> (the game clip's size) and the cam must already be cropped to it by the recorder.
    /// TODO: same for AMD (h264_amf) and Intel (h264_qsv) so they do not fall back to the CPU path.
    /// </summary>
    public static List<string> BuildCompositeCuda(string gameClip, int gameWidth, int gameHeight, IReadOnlyList<CamInput> cams, int quality, string output)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        var cur = "0:v";
        for (int i = 0; i < cams.Count; i++)
        {
            var c = cams[i];
            var (camW, camH) = CamBox(gameWidth, gameHeight, c.Layout);
            double right = Math.Clamp(c.Layout.RightPercent, 0, 100) / 100.0, top = Math.Clamp(c.Layout.TopPercent, 0, 100) / 100.0;
            var shift = c.ShiftSec >= 0
                ? $"setpts=PTS-STARTPTS+{c.ShiftSec.ToString("0.######", inv)}/TB"
                : $"trim=start={(-c.ShiftSec).ToString("0.######", inv)},setpts=PTS-STARTPTS";
            var n = i + 1;
            // the recorder already cropped the cam to this box, so this only guards a size mismatch
            sb.Append($"[{n}:v]{shift},scale_cuda=w={camW.ToString(inv)}:h={camH.ToString(inv)}[c{n}];");
            sb.Append($"[{cur}][c{n}]overlay_cuda=x='max(0,min(main_w-overlay_w,main_w-overlay_w-main_w*{right.ToString("0.####", inv)}))':y='max(0,min(main_h-overlay_h,main_h*{top.ToString("0.####", inv)}))':eof_action=pass:repeatlast=1[o{n}]{(i + 1 < cams.Count ? ";" : "")}");
            cur = $"o{n}";
        }
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", gameClip };
        foreach (var c in cams) a.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda", "-i", c.Clip]);
        a.AddRange(["-filter_complex", sb.ToString(), "-map", $"[{cur}]", "-map", "0:a?", "-c:a", "copy", "-c:v", "h264_nvenc", "-fps_mode:v", "passthrough"]);
        a.AddRange(EncoderArgs("h264_nvenc", quality));
        a.AddRange(GopArgs);
        a.AddRange(["-movflags", "+faststart", output]);
        return a;
    }

    /// <summary>Re-times the cam clip onto the game clip's timeline (same start, <paramref name="durationSec"/> long): head trimmed when the cam started earlier, first frame held when later.</summary>
    public static List<string> BuildCamAlign(string camClip, double camShiftSec, double durationSec, string encoder, int quality, string output)
    {
        var inv = CultureInfo.InvariantCulture;
        var vf = camShiftSec >= 0
            ? $"tpad=start_duration={camShiftSec.ToString("0.######", inv)}:start_mode=clone"
            : $"trim=start={(-camShiftSec).ToString("0.######", inv)},setpts=PTS-STARTPTS";
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-hwaccel", "d3d11va", "-i", camClip, "-vf", vf + ",format=yuv420p",
            "-t", durationSec.ToString("0.######", inv), "-c:v", encoder, "-fps_mode:v", "passthrough" };
        a.AddRange(EncoderArgs(encoder, quality));
        a.AddRange(GopArgs);
        a.AddRange(["-movflags", "+faststart", output]);
        return a;
    }

    /// <summary>Concat-demuxer list file contents (forward slashes, quoted).</summary>
    public static string BuildConcatList(IEnumerable<string> files)
        => string.Join("\n", files.Select(FileLine)) + "\n";

    /// <summary>
    /// Like the plain list, plus a `duration` after each file equal to the wall-clock gap to the next segment's start. Without it the
    /// demuxer chains files by their muxed length (AAC priming/padding, frame rounding) and the drift grows with every boundary.
    /// </summary>
    public static string BuildConcatList(IReadOnlyList<SegmentInfo> segments)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            sb.Append(FileLine(segments[i].File)).Append('\n');
            if (i + 1 < segments.Count)
            {
                var gap = (segments[i + 1].StartUtc - segments[i].StartUtc).TotalSeconds;
                if (gap > 0) sb.Append("duration ").Append(gap.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string FileLine(string f) => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'";

    public static List<string> BuildConcat(string listFile, string output)
        => ["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile,
            "-c", "copy", "-movflags", "+faststart", output];
}
