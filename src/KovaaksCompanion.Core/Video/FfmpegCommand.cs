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
        int fps = VideoOptions.Fps;
        bool hwFrames = encoder is "h264_nvenc" or "h264_amf" or "hevc_nvenc";
        var e = epochUnixUs.ToString(inv);
        bool window = target is { Hwnd: not 0 };
        string src;
        if (window)
            src = $"gfxcapture=hwnd={target!.Hwnd.ToString(inv)}:max_framerate={fps.ToString(inv)}:capture_cursor=0:width=-2:height=-2," +
                  $"setpts=(RTCTIME-{e})/(TB*1000000)";
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

    private static IEnumerable<string> EncoderArgs(string enc, int q) => enc switch
    {
        "h264_nvenc" or "hevc_nvenc" => ["-preset", "p4", "-tune", "ll", "-rc", "vbr", "-cq", q.ToString(), "-b:v", "0", "-maxrate", "150M", "-bf", "0", "-forced-idr", "1"],
        "h264_amf" => ["-quality", "speed", "-rc", "cqp", "-qp_i", q.ToString(), "-qp_p", q.ToString(), "-bf", "0"],
        "h264_qsv" => ["-preset", "veryfast", "-global_quality", q.ToString(), "-bf", "0"],
        _ => ["-pix_fmt", "yuv420p"],
    };

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
