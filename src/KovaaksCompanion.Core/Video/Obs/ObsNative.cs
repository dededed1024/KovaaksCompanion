using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>Raw libobs (OBS Studio 32.x) entry points. Handles are plain <see cref="nint"/>; strings are UTF-8.</summary>
internal static unsafe partial class ObsNative
{
    const string Lib = "obs";

    public const uint OutputVideo = 1, OutputAudio = 2, OutputEncoded = 4;
    public const int LogError = 100, LogWarning = 200, LogInfo = 300, LogDebug = 400;
    public const int BoundsStretch = 1, BoundsScaleOuter = 3;
    public const uint AlignCenter = 0, AlignLeft = 1, AlignRight = 2, AlignTop = 4, AlignBottom = 8;
    public const int VideoFormatNv12 = 2, VideoCs709 = 2, VideoRangePartial = 1, ScaleBicubic = 2;
    public const int SpeakersStereo = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoInfo
    {
        public nint GraphicsModule;
        public uint FpsNum, FpsDen, BaseWidth, BaseHeight, OutputWidth, OutputHeight;
        public int OutputFormat;
        public uint Adapter;
        public byte GpuConversion;
        public int Colorspace, Range, ScaleType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioInfo { public uint SamplesPerSec; public int Speakers; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec2 { public float X, Y; public Vec2(float x, float y) { X = x; Y = y; } }

    /// <summary>struct encoder_packet (obs-encoder.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EncoderPacket
    {
        public byte* Data;
        public nuint Size;
        public long Pts, Dts;
        public int TimebaseNum, TimebaseDen;
        public int Type;            // 0 audio, 1 video
        public byte Keyframe;
        public long DtsUsec, SysDtsUsec;
        public int Priority, DropPriority;
        public nuint TrackIdx;
        public nint Encoder;
    }

    /// <summary>struct encoder_packet_time (obs-encoder.h): os_gettime_ns() stamps of one video frame.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct EncoderPacketTime { public long Pts; public ulong Cts, Fer, Ferc, Pir; }

    /// <summary>struct obs_output_info (obs-output.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct OutputInfo
    {
        public nint Id;
        public uint Flags;
        public nint GetName, Create, Destroy, Start, Stop, RawVideo, RawAudio, EncodedPacket, Update, GetDefaults, GetProperties, Unused1,
            GetTotalBytes, GetDroppedFrames, TypeData, FreeTypeData, GetCongestion, GetConnectTimeMs, EncodedVideoCodecs, EncodedAudioCodecs, RawAudio2, Protocols;
    }

    [LibraryImport("kernel32", EntryPoint = "SetDllDirectoryW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetDllDirectory(string path);

    [LibraryImport("msvcrt", EntryPoint = "_vsnprintf")]
    public static partial int vsnprintf(byte* buffer, nuint size, byte* format, nint args);

    [LibraryImport(Lib)] public static partial void base_set_log_handler(nint handler, nint param);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_startup([MarshalAs(UnmanagedType.LPUTF8Str)] string locale, nint moduleConfigPath, nint store);
    [LibraryImport(Lib)] public static partial void obs_shutdown();
    [LibraryImport(Lib)] public static partial void obs_set_ui_task_handler(nint handler);
    [LibraryImport(Lib)] public static partial int obs_reset_video(ref VideoInfo ovi);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_reset_audio(ref AudioInfo oai);
    [LibraryImport(Lib)] public static partial void obs_add_data_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [LibraryImport(Lib)] public static partial void obs_add_module_path([MarshalAs(UnmanagedType.LPUTF8Str)] string bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string data);
    [LibraryImport(Lib)] public static partial void obs_load_all_modules();
    [LibraryImport(Lib)] public static partial void obs_post_load_modules();
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_enum_encoder_types(nuint idx, out nint id);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_enum_source_types(nuint idx, out nint id);
    [LibraryImport(Lib)] public static partial nint obs_get_video();
    [LibraryImport(Lib)] public static partial nint obs_get_audio();
    [LibraryImport(Lib)] public static partial void obs_set_output_source(uint channel, nint source);
    [LibraryImport(Lib)] public static partial ulong obs_get_video_frame_time();

    [LibraryImport(Lib)] public static partial nint obs_data_create();
    [LibraryImport(Lib)] public static partial void obs_data_release(nint data);
    [LibraryImport(Lib)] public static partial void obs_data_set_string(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string val);
    [LibraryImport(Lib)] public static partial void obs_data_set_int(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, long val);
    [LibraryImport(Lib)] public static partial void obs_data_set_bool(nint data, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.U1)] bool val);

    [LibraryImport(Lib)] public static partial nint obs_source_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeyData);
    [LibraryImport(Lib)] public static partial void obs_source_release(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_update(nint source, nint settings);
    [LibraryImport(Lib)] public static partial uint obs_source_get_width(nint source);
    [LibraryImport(Lib)] public static partial uint obs_source_get_height(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_set_audio_mixers(nint source, uint mixers);
    [LibraryImport(Lib)] public static partial nint obs_get_source_properties([MarshalAs(UnmanagedType.LPUTF8Str)] string id);
    [LibraryImport(Lib)] public static partial nint obs_source_properties(nint source);
    [LibraryImport(Lib)] public static partial nint obs_properties_get(nint props, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [LibraryImport(Lib)] public static partial void obs_properties_destroy(nint props);
    [LibraryImport(Lib)] public static partial nuint obs_property_list_item_count(nint prop);
    [LibraryImport(Lib)] public static partial nint obs_property_list_item_name(nint prop, nuint idx);
    [LibraryImport(Lib)] public static partial nint obs_property_list_item_string(nint prop, nuint idx);

    [LibraryImport(Lib)] public static partial nint obs_scene_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [LibraryImport(Lib)] public static partial nint obs_scene_get_source(nint scene);
    [LibraryImport(Lib)] public static partial nint obs_scene_add(nint scene, nint source);
    [LibraryImport(Lib)] public static partial void obs_scene_release(nint scene);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_remove(nint item);
    [LibraryImport(Lib)] public static partial void obs_source_remove(nint source);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_pos(nint item, in Vec2 pos);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_alignment(nint item, uint alignment);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_bounds_type(nint item, int type);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_bounds_alignment(nint item, uint alignment);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_bounds_crop(nint item, [MarshalAs(UnmanagedType.U1)] bool crop);
    [LibraryImport(Lib)] public static partial void obs_sceneitem_set_bounds(nint item, in Vec2 bounds);

    [LibraryImport(Lib)] public static partial nint obs_canvas_create_private([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref VideoInfo ovi, uint flags);
    [LibraryImport(Lib)] public static partial void obs_canvas_remove(nint canvas);
    [LibraryImport(Lib)] public static partial void obs_canvas_release(nint canvas);
    [LibraryImport(Lib)] public static partial nint obs_canvas_scene_create(nint canvas, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [LibraryImport(Lib)] public static partial void obs_canvas_set_channel(nint canvas, uint channel, nint source);
    [LibraryImport(Lib)] public static partial nint obs_canvas_get_video(nint canvas);
    [LibraryImport(Lib)] public static partial nint obs_video_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeyData);
    [LibraryImport(Lib)] public static partial nint obs_audio_encoder_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nuint mixerIdx, nint hotkeyData);
    [LibraryImport(Lib)] public static partial void obs_encoder_release(nint encoder);
    [LibraryImport(Lib)] public static partial void obs_encoder_set_video(nint encoder, nint video);
    [LibraryImport(Lib)] public static partial void obs_encoder_set_audio(nint encoder, nint audio);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_encoder_get_extra_data(nint encoder, out nint data, out nuint size);

    [LibraryImport(Lib)] public static partial void obs_register_output_s(in OutputInfo info, nuint size);
    [LibraryImport(Lib)] public static partial nint obs_output_create([MarshalAs(UnmanagedType.LPUTF8Str)] string id, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, nint settings, nint hotkeyData);
    [LibraryImport(Lib)] public static partial void obs_output_release(nint output);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_output_start(nint output);
    [LibraryImport(Lib)] public static partial void obs_output_stop(nint output);
    [LibraryImport(Lib)] public static partial void obs_output_force_stop(nint output);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_output_active(nint output);
    [LibraryImport(Lib)] public static partial nint obs_output_get_last_error(nint output);
    [LibraryImport(Lib)] public static partial void obs_output_set_video_encoder(nint output, nint encoder);
    [LibraryImport(Lib)] public static partial void obs_output_set_audio_encoder(nint output, nint encoder, nuint idx);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_output_can_begin_data_capture(nint output, uint flags);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_output_initialize_encoders(nint output, uint flags);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool obs_output_begin_data_capture(nint output, uint flags);
    [LibraryImport(Lib)] public static partial void obs_output_end_data_capture(nint output);
    [LibraryImport(Lib)] public static partial void obs_output_add_packet_callback(nint output, nint callback, nint param);
    [LibraryImport(Lib)] public static partial void obs_output_remove_packet_callback(nint output, nint callback, nint param);
    [LibraryImport("obs", EntryPoint = "os_gettime_ns")] public static partial ulong os_gettime_ns();
    [LibraryImport(Lib)] public static partial void obs_output_signal_stop(nint output, int code);

    public const int GsBgra = 5, GsZsNone = 0;
    public const uint GsClearColor = 1;
    public const int MonitorOnly = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec4 { public float X, Y, Z, W; }

    [LibraryImport(Lib)] public static partial void obs_enter_graphics();
    [LibraryImport(Lib)] public static partial void obs_leave_graphics();
    [LibraryImport(Lib)] public static partial nint gs_texrender_create(int format, int zsformat);
    [LibraryImport(Lib)] public static partial void gs_texrender_destroy(nint texrender);
    [LibraryImport(Lib)] public static partial void gs_texrender_reset(nint texrender);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool gs_texrender_begin(nint texrender, uint cx, uint cy);
    [LibraryImport(Lib)] public static partial void gs_texrender_end(nint texrender);
    [LibraryImport(Lib)] public static partial nint gs_texrender_get_texture(nint texrender);
    [LibraryImport(Lib)] public static partial void gs_ortho(float left, float right, float top, float bottom, float znear, float zfar);
    [LibraryImport(Lib)] public static partial void gs_clear(uint flags, in Vec4 color, float depth, byte stencil);
    [LibraryImport(Lib)] public static partial nint gs_stagesurface_create(uint width, uint height, int format);
    [LibraryImport(Lib)] public static partial void gs_stagesurface_destroy(nint surface);
    [LibraryImport(Lib)] public static partial void gs_stage_texture(nint surface, nint texture);
    [LibraryImport(Lib)][return: MarshalAs(UnmanagedType.U1)] public static partial bool gs_stagesurface_map(nint surface, out byte* data, out uint linesize);
    [LibraryImport(Lib)] public static partial void gs_stagesurface_unmap(nint surface);
    [LibraryImport(Lib)] public static partial void obs_source_video_render(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_media_play_pause(nint source, [MarshalAs(UnmanagedType.U1)] bool pause);
    [LibraryImport(Lib)] public static partial long obs_source_media_get_duration(nint source);
    [LibraryImport(Lib)] public static partial long obs_source_media_get_time(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_media_set_time(nint source, long ms);
    [LibraryImport(Lib)] public static partial int obs_source_media_get_state(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_set_monitoring_type(nint source, int type);
    [LibraryImport(Lib)] public static partial void obs_source_inc_showing(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_dec_showing(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_inc_active(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_dec_active(nint source);
    [LibraryImport(Lib)] public static partial void obs_source_set_async_unbuffered(nint source, [MarshalAs(UnmanagedType.U1)] bool unbuffered);
    [LibraryImport(Lib)] public static partial void obs_source_set_async_decoupled(nint source, [MarshalAs(UnmanagedType.U1)] bool decoupled);

    public static string? Str(nint p) => p == 0 ? null : Marshal.PtrToStringUTF8(p);
}
