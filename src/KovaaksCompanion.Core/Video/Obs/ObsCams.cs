using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>A hand cam composited into the game frame while recording.</summary>
public sealed record ObsCamSpec(string DeviceId, HandCamLayout Layout);

/// <summary>DirectShow cam sources and their placement in a scene.</summary>
internal static class ObsCamSource
{
    /// <summary>Recording frame rate of a cam.</summary>
    public const int Fps = 30;

    /// <summary>dshow_input in the device's own default mode, so the cam is recorded at the size it delivers.</summary>
    public static nint CreateNative(string deviceId, string name)
    {
        var d = ObsNative.obs_data_create();
        try
        {
            ObsNative.obs_data_set_string(d, "video_device_id", deviceId);
            ObsNative.obs_data_set_int(d, "res_type", 0);
            ObsNative.obs_data_set_bool(d, "deactivate_when_not_showing", false);
            return ObsNative.obs_source_create("dshow_input", name, d, 0);
        }
        finally { ObsNative.obs_data_release(d); }
    }

    /// <summary>True when the source delivers frames; false (no mode matched) otherwise.</summary>
    public static bool HasFrames(nint source) => ObsNative.obs_source_get_width(source) > 0;

    /// <summary>Adds the source to the scene as laid out by <paramref name="layout"/> inside a <paramref name="frameW"/> x <paramref name="frameH"/> frame (a 16:9 cam until <see cref="Fit"/> knows the real picture).</summary>
    public static nint Place(nint scene, nint source, int frameW, int frameH, HandCamLayout layout)
    {
        var item = ObsNative.obs_scene_add(scene, source);
        Fit(item, frameW, frameH, layout, 1280, 720);
        return item;
    }

    /// <summary>Cut, scale, turn and position of the cam for a picture of <paramref name="srcW"/> x <paramref name="srcH"/> pixels; the picture is trimmed by the cut, never stretched.</summary>
    public static void Fit(nint item, int frameW, int frameH, HandCamLayout layout, int srcW, int srcH)
    {
        var (w, h) = HandCamGeometry.BoxSize(frameW, frameH, layout, (double)srcW / srcH);
        var (x, y) = HandCamGeometry.Position(frameW, frameH, w, h, layout);
        var cut = layout.Cut ?? new HandCamCut();
        int cl = (int)(srcW * cut.Left), ct = (int)(srcH * cut.Top), cr = (int)(srcW * cut.Right), cb = (int)(srcH * cut.Bottom);
        double visW = Math.Max(1, srcW - cl - cr), visH = Math.Max(1, srcH - ct - cb);
        var scale = (float)(layout.Sideways ? w / visH : w / visW);
        // centered anchor: the item turns around the middle of its box, so the box stays where the preview shows it
        ObsNative.obs_sceneitem_set_bounds_type(item, 0);
        ObsNative.obs_sceneitem_set_alignment(item, ObsNative.AlignCenter);
        ObsNative.obs_sceneitem_set_crop(item, new ObsNative.ItemCrop(cl, ct, cr, cb));
        ObsNative.obs_sceneitem_set_scale(item, new ObsNative.Vec2(scale, scale));
        ObsNative.obs_sceneitem_set_pos(item, new ObsNative.Vec2((float)(x + w / 2), (float)(y + h / 2)));
        ObsNative.obs_sceneitem_set_rot(item, layout.Rotation);
    }
}

/// <summary>
/// One hand cam recorded on its own at the size the device delivers (trimmed by the cut when one is set): cam source -> private libobs canvas of that size -> encoder -> <see cref="PacketOutput"/>.
/// Used when the cam is saved as a separate file next to the game video.
/// </summary>
internal sealed class ObsCamCapture : IDisposable
{
    const uint CanvasActivate = 1u << 1, CanvasSceneRef = 1u << 3; // sources go active when visible; the canvas keeps the scene alive
    static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(6);
    nint _canvas, _scene, _source, _encoder;
    PacketOutput? _output;
    readonly object _sync = new();

    public string Error { get; private set; } = "";
    public PacketOutput Output => _output ?? throw new InvalidOperationException("not started");
    int _rotation, _camW, _camH;
    ObsNative.ItemCrop _crop;
    bool Sideways => _rotation is 90 or 270;
    /// <summary>Recorded frame size (after the turn).</summary>
    public int Width => Sideways ? _camH : _camW;
    public int Height => Sideways ? _camW : _camH;

    /// <summary>Opens the cam, waits for its first frame to learn the size, then builds the canvas and encoder at that size.</summary>
    public async Task<bool> StartAsync(string deviceId, string encoderId, int quality, int rotation, HandCamCut? cut, Action<EncodedPacket> onPacket)
    {
        _rotation = rotation;
        if (!ObsRuntime.Run(() => { lock (_sync) return (_source = ObsCamSource.CreateNative(deviceId, "cam")) != 0; })) { Error = "cam source failed"; return false; }
        var until = DateTime.UtcNow + FrameWait;
        int w = 0, h = 0;
        while (DateTime.UtcNow < until)
        {
            (w, h) = ObsRuntime.Run(() => { lock (_sync) return _source == 0 ? (0, 0) : ((int)ObsNative.obs_source_get_width(_source), (int)ObsNative.obs_source_get_height(_source)); });
            if (w > 0 && h > 0) break;
            await Task.Delay(100);
        }
        if (w <= 0 || h <= 0) { Error = "cam delivers no frames"; return false; }
        // the recorded picture is what the cut leaves of the cam
        var c = cut ?? new HandCamCut();
        _crop = new ObsNative.ItemCrop((int)(w * c.Left), (int)(h * c.Top), (int)(w * c.Right), (int)(h * c.Bottom));
        w -= _crop.Left + _crop.Right; h -= _crop.Top + _crop.Bottom;
        (_camW, _camH) = (Math.Max(16, w / 2 * 2), Math.Max(16, h / 2 * 2));
        return ObsRuntime.Run(() => StartCore(encoderId, quality, onPacket));
    }

    bool StartCore(string encoderId, int quality, Action<EncodedPacket> onPacket)
    {
        lock (_sync)
        {
            if (_source == 0) { Error = "cam closed"; return false; }
            var gfx = Marshal.StringToCoTaskMemUTF8("libobs-d3d11");
            try
            {
                var v = new ObsNative.VideoInfo
                {
                    GraphicsModule = gfx, FpsNum = ObsCamSource.Fps, FpsDen = 1, BaseWidth = (uint)Width, BaseHeight = (uint)Height,
                    OutputWidth = (uint)Width, OutputHeight = (uint)Height, OutputFormat = ObsNative.VideoFormatNv12, Adapter = 0,
                    GpuConversion = 1, Colorspace = ObsNative.VideoCs709, Range = ObsNative.VideoRangePartial, ScaleType = ObsNative.ScaleBicubic,
                };
                _canvas = ObsNative.obs_canvas_create_private("cam", ref v, CanvasActivate | CanvasSceneRef);
            }
            finally { Marshal.FreeCoTaskMem(gfx); }
            if (_canvas == 0) { Error = "canvas failed"; return false; }
            _scene = ObsNative.obs_canvas_scene_create(_canvas, "cam");
            var item = ObsNative.obs_scene_add(_scene, _source);
            ObsNative.obs_sceneitem_set_alignment(item, ObsNative.AlignCenter);
            ObsNative.obs_sceneitem_set_pos(item, new ObsNative.Vec2(Width / 2f, Height / 2f));
            ObsNative.obs_sceneitem_set_crop(item, _crop);
            ObsNative.obs_sceneitem_set_rot(item, _rotation);
            ObsNative.obs_canvas_set_channel(_canvas, 0, ObsNative.obs_scene_get_source(_scene));

            _encoder = ObsCapture.CreateVideoEncoder(encoderId, quality);
            if (_encoder == 0) { Error = "encoder " + encoderId + " unavailable"; return false; }
            ObsNative.obs_encoder_set_video(_encoder, ObsNative.obs_canvas_get_video(_canvas));
            _output = new PacketOutput(_encoder, 0, "kc-cam");
            _output.Packet += onPacket;
            if (!_output.Start()) { Error = _output.LastError; return false; }
            return true;
        }
    }

    public void Dispose()
    {
        ObsRuntime.Run(() => { lock (_sync) DisposeCore(); return true; });
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
