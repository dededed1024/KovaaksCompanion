using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>A hand cam composited into the game frame while recording.</summary>
public sealed record ObsCamSpec(string DeviceId, HandCamLayout Layout);

/// <summary>DirectShow cam sources and their placement in a scene.</summary>
internal static class ObsCamSource
{
    public const int Width = 1280, Height = 720, Fps = 30;

    /// <summary>dshow_input asking the device for 1280x720@30 (see <see cref="UseDeviceDefault"/> for devices without that mode).</summary>
    public static nint Create(string deviceId, string name)
    {
        var d = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(d, "video_device_id", deviceId);
            ObsNative.obs_data_set_int(d, "res_type", 1);
            ObsNative.obs_data_set_string(d, "resolution", $"{Width}x{Height}");
            ObsNative.obs_data_set_int(d, "frame_interval", 10_000_000 / Fps);
            ObsNative.obs_data_set_bool(d, "deactivate_when_not_showing", false);
            return ObsNative.obs_source_create("dshow_input", name, d, 0);
        }
        finally { ObsNative.obs_data_release(d); }
    }

    /// <summary>True when the source delivers frames; false (no mode matched) otherwise.</summary>
    public static bool HasFrames(nint source) => ObsNative.obs_source_get_width(source) > 0;

    /// <summary>Falls back to whatever resolution the device defaults to.</summary>
    public static void UseDeviceDefault(nint source)
    {
        var d = ObsNative.obs_data_create();
        try { ObsNative.obs_data_set_int(d, "res_type", 0); ObsNative.obs_source_update(source, d); }
        finally { ObsNative.obs_data_release(d); }
    }

    /// <summary>Adds the source to the scene as a cover-scaled, cropped box of <paramref name="layout"/> inside a <paramref name="frameW"/> x <paramref name="frameH"/> frame.</summary>
    public static void Place(nint scene, nint source, int frameW, int frameH, HandCamLayout layout)
    {
        var (w, h) = HandCamGeometry.CamBox(frameW, frameH, layout);
        var (x, y) = HandCamGeometry.Position(frameW, frameH, w, h, layout);
        var item = ObsNative.obs_scene_add(scene, source);
        ObsNative.obs_sceneitem_set_alignment(item, ObsNative.AlignLeft | ObsNative.AlignTop);
        ObsNative.obs_sceneitem_set_pos(item, new ObsNative.Vec2(x, y));
        ObsNative.obs_sceneitem_set_bounds_type(item, ObsNative.BoundsScaleOuter);
        ObsNative.obs_sceneitem_set_bounds_alignment(item, ObsNative.AlignCenter);
        ObsNative.obs_sceneitem_set_bounds_crop(item, true);
        ObsNative.obs_sceneitem_set_bounds(item, new ObsNative.Vec2(w, h));
    }
}

/// <summary>
/// One hand cam recorded on its own: a private libobs canvas (the cam alone at its capture size) -> encoder -> <see cref="PacketOutput"/>.
/// Used when the cam is saved as a separate file next to the game video.
/// </summary>
internal sealed class ObsCamCapture : IDisposable
{
    const uint CanvasActivate = 1u << 1, CanvasSceneRef = 1u << 3; // sources go active when visible; the canvas keeps the scene alive
    nint _canvas, _scene, _source, _encoder;
    PacketOutput? _output;
    readonly object _sync = new();

    public string Error { get; private set; } = "";
    public PacketOutput Output => _output ?? throw new InvalidOperationException("not started");
    public int Width => ObsCamSource.Width;
    public int Height => ObsCamSource.Height;

    public bool Start(string deviceId, string encoderId, int quality, Action<EncodedPacket> onPacket)
    {
        var gfx = Marshal.StringToCoTaskMemUTF8("libobs-d3d11");
        try
        {
            var v = new ObsNative.VideoInfo
            {
                GraphicsModule = gfx, FpsNum = ObsCamSource.Fps, FpsDen = 1, BaseWidth = ObsCamSource.Width, BaseHeight = ObsCamSource.Height,
                OutputWidth = ObsCamSource.Width, OutputHeight = ObsCamSource.Height, OutputFormat = ObsNative.VideoFormatNv12, Adapter = 0,
                GpuConversion = 1, Colorspace = ObsNative.VideoCs709, Range = ObsNative.VideoRangePartial, ScaleType = ObsNative.ScaleBicubic,
            };
            _canvas = ObsNative.obs_canvas_create_private("cam", ref v, CanvasActivate | CanvasSceneRef);
        }
        finally { Marshal.FreeCoTaskMem(gfx); }
        if (_canvas == 0) { Error = "canvas failed"; return false; }
        _source = ObsCamSource.Create(deviceId, "cam");
        if (_source == 0) { Error = "cam source failed"; return false; }
        _scene = ObsNative.obs_canvas_scene_create(_canvas, "cam");
        var item = ObsNative.obs_scene_add(_scene, _source);
        ObsNative.obs_sceneitem_set_pos(item, new ObsNative.Vec2(0, 0));
        ObsNative.obs_sceneitem_set_bounds_type(item, ObsNative.BoundsScaleOuter);
        ObsNative.obs_sceneitem_set_bounds_crop(item, true);
        ObsNative.obs_sceneitem_set_bounds(item, new ObsNative.Vec2(ObsCamSource.Width, ObsCamSource.Height));
        ObsNative.obs_canvas_set_channel(_canvas, 0, ObsNative.obs_scene_get_source(_scene));

        _encoder = ObsCapture.CreateVideoEncoder(encoderId, quality);
        if (_encoder == 0) { Error = "encoder " + encoderId + " unavailable"; return false; }
        ObsNative.obs_encoder_set_video(_encoder, ObsNative.obs_canvas_get_video(_canvas));
        _output = new PacketOutput(_encoder, 0, "kc-cam");
        _output.Packet += onPacket;
        if (!_output.Start()) { Error = _output.LastError; return false; }
        return true;
    }

    /// <summary>A cam that delivers no frames at 1280x720@30 falls back to its device default mode.</summary>
    public void FixMode()
    {
        lock (_sync)
            if (_source != 0 && !ObsCamSource.HasFrames(_source)) { Diagnostics.AppLog.Write("handcam", "no frames at 1280x720@30, using the device default"); ObsCamSource.UseDeviceDefault(_source); }
    }

    public void Dispose()
    {
        lock (_sync) DisposeCore();
    }

    void DisposeCore()
    {
        _output?.Dispose(); _output = null;
        if (_encoder != 0) { ObsNative.obs_encoder_release(_encoder); _encoder = 0; }
        if (_canvas != 0) ObsNative.obs_canvas_set_channel(_canvas, 0, 0);
        if (_source != 0) { ObsNative.obs_source_release(_source); _source = 0; }
        if (_scene != 0) { ObsNative.obs_scene_release(_scene); _scene = 0; }
        if (_canvas != 0) { ObsNative.obs_canvas_remove(_canvas); ObsNative.obs_canvas_release(_canvas); _canvas = 0; }
    }
}
