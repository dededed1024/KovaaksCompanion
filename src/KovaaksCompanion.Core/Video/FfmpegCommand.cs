using System.Globalization;

namespace KovaaksCompanion.Core.Video;

/// <summary>Pure builders for ffmpeg command lines and encoder choice.</summary>
public static class FfmpegCommand
{
    public static readonly string[] EncoderChain = ["h264_nvenc", "h264_amf", "h264_qsv", "libx264"];

    /// <summary>Chain order filtered by what `ffmpeg -encoders` lists; libx264 as last resort when nothing matches.</summary>
    public static IReadOnlyList<string> ChooseEncoders(string ffmpegEncodersOutput, string? forced = null)
    {
        if (!string.IsNullOrWhiteSpace(forced)) return [forced];
        var list = EncoderChain.Where(e => ffmpegEncodersOutput.Contains(" " + e + " ", StringComparison.Ordinal)).ToList();
        if (list.Count == 0) list.Add("libx264");
        return list;
    }

    public const string SegmentFilePattern = "seg_%03d.mp4";

    /// <summary>Arguments for the rolling-buffer recorder. Segment list (csv) goes to stdout.</summary>
    public static List<string> BuildRecord(VideoOptions o, string encoder, int outputIdx, int adapterIdx, string bufferDir)
    {
        var inv = CultureInfo.InvariantCulture;
        bool hwFrames = encoder is "h264_nvenc" or "h264_amf" or "hevc_nvenc";
        var src = $"ddagrab=output_idx={outputIdx}:framerate={o.Fps.ToString(inv)}:draw_mouse=0";
        var filter = hwFrames ? src : src + ",hwdownload,format=bgra,format=yuv420p";
        var a = new List<string>
        {
            "-hide_banner", "-loglevel", "warning", "-nostats",
            "-init_hw_device", $"d3d11va=dda:{adapterIdx}", "-filter_hw_device", "dda",
            "-f", "lavfi", "-i", filter,
            "-c:v", encoder,
        };
        a.AddRange(EncoderArgs(encoder, o.Quality));
        a.AddRange(["-g", o.Fps.ToString(inv), "-keyint_min", o.Fps.ToString(inv),
            "-f", "segment", "-segment_time", o.SegmentSeconds.ToString(inv),
            "-segment_wrap", o.SlotCount.ToString(inv), "-reset_timestamps", "1",
            "-segment_list", "pipe:1", "-segment_list_type", "csv", "-segment_list_flags", "live",
            "-segment_format", "mp4",
            Path.Combine(bufferDir, SegmentFilePattern)]);
        return a;
    }

    private static IEnumerable<string> EncoderArgs(string enc, int q) => enc switch
    {
        "h264_nvenc" or "hevc_nvenc" => ["-preset", "p4", "-tune", "ll", "-rc", "vbr", "-cq", q.ToString(), "-b:v", "0", "-maxrate", "60M", "-bf", "0", "-forced-idr", "1"],
        "h264_amf" => ["-quality", "speed", "-rc", "cqp", "-qp_i", q.ToString(), "-qp_p", q.ToString(), "-bf", "0"],
        "h264_qsv" => ["-preset", "veryfast", "-global_quality", q.ToString(), "-bf", "0"],
        _ => ["-preset", "ultrafast", "-crf", q.ToString(), "-pix_fmt", "yuv420p"],
    };

    /// <summary>Concat-demuxer list file contents (forward slashes, quoted).</summary>
    public static string BuildConcatList(IEnumerable<string> files)
        => string.Join("\n", files.Select(f => "file '" + f.Replace('\\', '/').Replace("'", "'\\''") + "'")) + "\n";

    public static List<string> BuildConcat(string listFile, string output)
        => ["-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", listFile,
            "-c", "copy", "-movflags", "+faststart", output];
}
