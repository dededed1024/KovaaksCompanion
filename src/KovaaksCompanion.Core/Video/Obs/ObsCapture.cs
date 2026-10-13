using System.Runtime.InteropServices;
using System.Text;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>What one capture pipeline records.</summary>
/// <param name="Encoder">libobs video encoder id (see <see cref="ObsCapture.EncoderChain"/>).</param>
public sealed record ObsCaptureSpec(CaptureTarget Target, string ProcessName, int Fps, int Quality, int? MaxHeight, string Encoder, bool Audio, IReadOnlyList<ObsCamSpec>? Cams = null);

/// <summary>
/// Game window (Windows Graphics Capture) + the game process's audio -> hardware H.264 + AAC -> <see cref="PacketOutput"/>.
/// One instance per recording; libobs allows a single active video setup at a time.
/// </summary>
internal sealed class ObsCapture : IDisposable
{
    /// <summary>Hardware encoders in preference order (NVENC, AMD AMF, Intel QSV). No software fallback.</summary>
    public static readonly string[] EncoderChain = ["obs_nvenc_h264_tex", "h264_texture_amf", "obs_qsv11_v2"];

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(nint hwnd, StringBuilder text, int max);

    readonly ObsCaptureSpec _spec;
    nint _scene, _videoSource, _audioSource, _videoEncoder, _audioEncoder;
    readonly List<nint> _camSources = [];
    readonly List<nint> _items = [];
    readonly object _sync = new();
    PacketOutput? _output;

    public ObsCapture(ObsCaptureSpec spec) => _spec = spec;

    public PacketOutput Output => _output ?? throw new InvalidOperationException("not started");
    public int OutputWidth { get; private set; }
    public int OutputHeight { get; private set; }
    public bool HasAudio => _audioEncoder != 0;
    /// <summary>Cam sources composited into the frame.</summary>
    public IReadOnlyList<nint> CamSources => _camSources;
    public string Error { get; private set; } = "";

    /// <summary>Encoder ids libobs actually registered on this machine, in chain order.</summary>
    public static IReadOnlyList<string> AvailableEncoders(string? forced = null)
    {
        if (!string.IsNullOrWhiteSpace(forced)) return [forced];
        var have = ObsRuntime.EncoderIds();
        return EncoderChain.Where(have.Contains).ToList();
    }

    public bool Start(Action<EncodedPacket> onPacket) => ObsRuntime.Run(() => StartCore(onPacket));

    bool StartCore(Action<EncodedPacket> onPacket)
    {
        var t = _spec.Target;
        var (ow, oh) = VideoGeometry.OutputSize(t.Width, t.Height, _spec.MaxHeight);
        (OutputWidth, OutputHeight) = (ow, oh);
        if (!ObsRuntime.ResetVideo(t.Width & ~1, t.Height & ~1, ow, oh, Math.Max(1, _spec.Fps)) || !ObsRuntime.ResetAudio()) { Error = ObsRuntime.LastError; return false; }

        var window = WindowString(t.Hwnd, _spec.ProcessName);
        var s = ObsNative.obs_data_create();
        ObsNative.obs_data_set_string(s, "window", window);
        ObsNative.obs_data_set_int(s, "method", 2);      // Windows Graphics Capture
        ObsNative.obs_data_set_int(s, "priority", 2);    // match by exe
        ObsNative.obs_data_set_bool(s, "cursor", true);
        ObsNative.obs_data_set_bool(s, "client_area", true);
        _videoSource = ObsNative.obs_source_create("window_capture", "game", s, 0);
        ObsNative.obs_data_release(s);
        if (_videoSource == 0) { Error = "window_capture source failed"; return false; }

        _scene = ObsNative.obs_scene_create("scene");
        var item = ObsNative.obs_scene_add(_scene, _videoSource);
        ObsNative.obs_sceneitem_set_pos(item, new ObsNative.Vec2(0, 0));
        ObsNative.obs_sceneitem_set_bounds_type(item, 2); // scale inner
        ObsNative.obs_sceneitem_set_bounds(item, new ObsNative.Vec2(t.Width & ~1, t.Height & ~1));
        _items.Add(item);
        // cams go on top of the game, each in its box (cover-scaled and cropped to it), in list order
        int baseW = t.Width & ~1, baseH = t.Height & ~1;
        foreach (var cam in _spec.Cams ?? [])
        {
            var src = ObsCamSource.Create(cam.DeviceId, "cam" + (_camSources.Count + 1));
            if (src == 0) { AppLog.Write("obs", "hand cam source failed: " + cam.DeviceId); continue; }
            _camSources.Add(src);
            _items.Add(ObsCamSource.Place(_scene, src, baseW, baseH, cam.Layout));
        }
        ObsNative.obs_set_output_source(0, ObsNative.obs_scene_get_source(_scene));

        if (_spec.Audio)
        {
            var a = ObsNative.obs_data_create();
            ObsNative.obs_data_set_string(a, "window", window);
            ObsNative.obs_data_set_int(a, "priority", 2);
            _audioSource = ObsNative.obs_source_create("wasapi_process_output_capture", "game audio", a, 0);
            ObsNative.obs_data_release(a);
            if (_audioSource != 0) { ObsNative.obs_source_set_audio_mixers(_audioSource, 1); ObsNative.obs_set_output_source(1, _audioSource); }
            else AppLog.Write("obs", "process audio source failed");
        }

        _videoEncoder = CreateVideoEncoder(_spec.Encoder, _spec.Quality);
        if (_videoEncoder == 0) { Error = "encoder " + _spec.Encoder + " unavailable"; return false; }
        ObsNative.obs_encoder_set_video(_videoEncoder, ObsNative.obs_get_video());
        if (_audioSource != 0)
        {
            var e = ObsNative.obs_data_create();
            ObsNative.obs_data_set_int(e, "bitrate", 192);
            _audioEncoder = ObsNative.obs_audio_encoder_create("ffmpeg_aac", "aac", e, 0, 0);
            ObsNative.obs_data_release(e);
            if (_audioEncoder != 0) ObsNative.obs_encoder_set_audio(_audioEncoder, ObsNative.obs_get_audio());
        }

        _output = new PacketOutput(_videoEncoder, _audioEncoder, "kc");
        _output.Packet += onPacket;
        if (!_output.Start()) { Error = _output.LastError; return false; }
        return true;
    }

    /// <summary>Per-vendor constant-quality settings, keyframe every second, no B-frames.</summary>
    internal static nint CreateVideoEncoder(string id, int quality)
    {
        var d = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_int(d, "keyint_sec", 1);
            switch (id)
            {
                case "obs_nvenc_h264_tex":
                    ObsNative.obs_data_set_string(d, "rate_control", "CQP");
                    ObsNative.obs_data_set_int(d, "cqp", quality);
                    ObsNative.obs_data_set_string(d, "preset", "p4");
                    ObsNative.obs_data_set_string(d, "tune", "ll");
                    ObsNative.obs_data_set_string(d, "multipass", "disabled");
                    ObsNative.obs_data_set_string(d, "profile", "high");
                    ObsNative.obs_data_set_int(d, "bf", 0);
                    ObsNative.obs_data_set_bool(d, "lookahead", false);
                    ObsNative.obs_data_set_bool(d, "adaptive_quantization", false);
                    break;
                case "h264_texture_amf":
                    ObsNative.obs_data_set_string(d, "rate_control", "CQP");
                    ObsNative.obs_data_set_int(d, "cqp", quality);
                    ObsNative.obs_data_set_string(d, "preset", "speed");
                    ObsNative.obs_data_set_string(d, "profile", "high");
                    ObsNative.obs_data_set_int(d, "bf", 0);
                    break;
                default: // QSV
                    ObsNative.obs_data_set_string(d, "rate_control", "CQP");
                    foreach (var k in new[] { "cqp", "qpi", "qpp", "qpb" }) ObsNative.obs_data_set_int(d, k, quality);
                    ObsNative.obs_data_set_string(d, "target_usage", "TU7");
                    ObsNative.obs_data_set_string(d, "profile", "high");
                    ObsNative.obs_data_set_int(d, "bframes", 0);
                    break;
            }
            return ObsNative.obs_video_encoder_create(id, "video", d, 0);
        }
        finally { ObsNative.obs_data_release(d); }
    }

    /// <summary>libobs window id "title:class:exe" (':' and '#' escaped as #3A / #23).</summary>
    static string WindowString(long hwnd, string processName)
    {
        var title = new StringBuilder(512); var cls = new StringBuilder(256);
        GetWindowText((nint)hwnd, title, title.Capacity);
        GetClassName((nint)hwnd, cls, cls.Capacity);
        static string Esc(string s) => s.Replace("#", "#23").Replace(":", "#3A");
        return $"{Esc(title.ToString())}:{Esc(cls.ToString())}:{Esc(processName + ".exe")}";
    }

    /// <summary>Size libobs sees for the game source; 0x0 means the window was not found or never delivered a frame.</summary>
    public (uint Width, uint Height) SourceSize()
    {
        lock (_sync)
            return _videoSource == 0 ? (0, 0) : (ObsNative.obs_source_get_width(_videoSource), ObsNative.obs_source_get_height(_videoSource));
    }

    /// <summary>Cams that deliver no frames at 1280x720@30 fall back to their device default mode.</summary>
    public void FixCamModes() => ObsRuntime.Run(() =>
    {
        lock (_sync)
            foreach (var c in _camSources)
                if (!ObsCamSource.HasFrames(c)) { AppLog.Write("handcam", "no frames at 1280x720@30, using the device default"); ObsCamSource.UseDeviceDefault(c); }
        return true;
    });

    public void Dispose()
    {
        ObsRuntime.Run(() => { lock (_sync) DisposeCore(); return true; });
    }

    void DisposeCore()
    {
        _output?.Dispose(); _output = null;
        if (_videoEncoder != 0) { ObsNative.obs_encoder_release(_videoEncoder); _videoEncoder = 0; }
        if (_audioEncoder != 0) { ObsNative.obs_encoder_release(_audioEncoder); _audioEncoder = 0; }
        ObsNative.obs_set_output_source(0, 0); ObsNative.obs_set_output_source(1, 0);
        // drop the scene's references first, otherwise a cam stays open (webcam LED on) until the process ends
        foreach (var i in _items) ObsNative.obs_sceneitem_remove(i);
        _items.Clear();
        foreach (var c in _camSources) { ObsNative.obs_source_remove(c); ObsNative.obs_source_release(c); }
        _camSources.Clear();
        if (_videoSource != 0) { ObsNative.obs_source_release(_videoSource); _videoSource = 0; }
        if (_audioSource != 0) { ObsNative.obs_source_release(_audioSource); _audioSource = 0; }
        if (_scene != 0) { ObsNative.obs_scene_release(_scene); _scene = 0; }
    }
}

/// <summary>Output sizing shared by the capture pipeline and the hand-cam layout.</summary>
public static class VideoGeometry
{
    /// <summary>Even output size for a source capped to <paramref name="maxHeight"/> (aspect kept, never upscaled).</summary>
    public static (int Width, int Height) OutputSize(int srcWidth, int srcHeight, int? maxHeight)
    {
        if (maxHeight is not int cap || srcHeight <= cap)
            return (Math.Max(2, srcWidth & ~1), Math.Max(2, srcHeight & ~1));
        int h = Math.Max(2, cap & ~1);
        int w = (int)Math.Round((long)srcWidth * h / (double)srcHeight);
        return (Math.Max(2, w & ~1), h);
    }
}
