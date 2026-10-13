namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>Plays a video file through libobs' ffmpeg_source and hands out BGRA frames scaled by the GPU to the requested size (replay view; needs <see cref="ObsRuntime.Start"/> first).</summary>
public sealed unsafe class ObsPlayer : IDisposable
{
    /// <summary>obs_media_state OBS_MEDIA_STATE_ENDED.</summary>
    public const int StateEnded = 6;

    nint _src, _render, _stage;
    uint _w, _h;
    bool _disposed;

    public double Duration => _src == 0 ? 0 : ObsNative.obs_source_media_get_duration(_src) / 1000.0;
    public int State => _src == 0 ? 0 : ObsNative.obs_source_media_get_state(_src);
    public int VideoWidth => _src == 0 ? 0 : (int)ObsNative.obs_source_get_width(_src);
    public int VideoHeight => _src == 0 ? 0 : (int)ObsNative.obs_source_get_height(_src);
    public double Position => _src == 0 ? 0 : ObsNative.obs_source_media_get_time(_src) / 1000.0;

    public static ObsPlayer? Open(string path, bool hardware = true, bool audio = true)
    {
        if (!ObsRuntime.IsStarted) return null;
        return ObsRuntime.Run(() =>
        {
            var d = ObsNative.obs_data_create();
            try
            {
                ObsNative.obs_data_set_bool(d, "is_local_file", true);
                ObsNative.obs_data_set_string(d, "local_file", path);
                ObsNative.obs_data_set_bool(d, "looping", false);
                ObsNative.obs_data_set_bool(d, "hw_decode", hardware);
                ObsNative.obs_data_set_bool(d, "close_when_inactive", false);
                ObsNative.obs_data_set_bool(d, "restart_on_activate", false);
                ObsNative.obs_data_set_bool(d, "clear_on_media_end", false);
                var src = ObsNative.obs_source_create("ffmpeg_source", "kc_replay", d, 0);
                if (src == 0) return null;
                // a source outside any scene is never active; mark it so the decoder runs and its audio is monitored
                ObsNative.obs_source_inc_showing(src);
                ObsNative.obs_source_inc_active(src);
                if (audio) ObsNative.obs_source_set_monitoring_type(src, ObsNative.MonitorOnly);
                return new ObsPlayer { _src = src };
            }
            finally { ObsNative.obs_data_release(d); }
        });
    }

    public void Play() { if (_src != 0) ObsNative.obs_source_media_play_pause(_src, false); }
    public void Pause() { if (_src != 0) ObsNative.obs_source_media_play_pause(_src, true); }
    public void Seek(double seconds) { if (_src != 0) ObsNative.obs_source_media_set_time(_src, (long)(seconds * 1000)); }

    public void SetSpeed(double speed)
    {
        if (_src == 0) return;
        var d = ObsNative.obs_data_create();
        try { ObsNative.obs_data_set_int(d, "speed_percent", (long)Math.Round(speed * 100)); ObsNative.obs_source_update(_src, d); }
        finally { ObsNative.obs_data_release(d); }
    }

    /// <summary>Renders the current frame at width x height and passes the mapped BGRA bytes (pointer, row stride) to <paramref name="read"/>. False when the source has no frame yet.</summary>
    public bool ReadFrame(int width, int height, Action<nint, int> read)
    {
        if (_src == 0 || width <= 0 || height <= 0) return false;
        uint srcW = ObsNative.obs_source_get_width(_src), srcH = ObsNative.obs_source_get_height(_src);
        if (srcW == 0 || srcH == 0) return false;
        ObsNative.obs_enter_graphics();
        try
        {
            if (_render == 0 || _w != width || _h != height)
            {
                Free();
                _w = (uint)width; _h = (uint)height;
                _render = ObsNative.gs_texrender_create(ObsNative.GsBgra, ObsNative.GsZsNone);
                _stage = ObsNative.gs_stagesurface_create(_w, _h, ObsNative.GsBgra);
            }
            ObsNative.gs_texrender_reset(_render);
            if (!ObsNative.gs_texrender_begin(_render, _w, _h)) return false;
            ObsNative.gs_clear(ObsNative.GsClearColor, new ObsNative.Vec4(), 1f, 0);
            ObsNative.gs_ortho(0, srcW, 0, srcH, -100f, 100f);
            ObsNative.obs_source_video_render(_src);
            ObsNative.gs_texrender_end(_render);
            ObsNative.gs_stage_texture(_stage, ObsNative.gs_texrender_get_texture(_render));
            if (!ObsNative.gs_stagesurface_map(_stage, out var data, out var line)) return false;
            try { read((nint)data, (int)line); }
            finally { ObsNative.gs_stagesurface_unmap(_stage); }
            return true;
        }
        finally { ObsNative.obs_leave_graphics(); }
    }

    void Free()
    {
        if (_stage != 0) { ObsNative.gs_stagesurface_destroy(_stage); _stage = 0; }
        if (_render != 0) { ObsNative.gs_texrender_destroy(_render); _render = 0; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ObsRuntime.Run(() =>
        {
            ObsNative.obs_enter_graphics();
            try { Free(); } finally { ObsNative.obs_leave_graphics(); }
            if (_src != 0)
            {
                ObsNative.obs_source_dec_active(_src);
                ObsNative.obs_source_dec_showing(_src);
                ObsNative.obs_source_release(_src);
                _src = 0;
            }
            return 0;
        });
    }
}
