using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video.Ffmpeg;

/// <summary>The FFmpeg 8.0 DLLs shipped in the libobs bundle (avutil-60, swresample-6, swscale-9, avcodec-62, avformat-62, avfilter-11), bound by function pointer so the
/// per-assembly DllImport resolver that ObsRuntime owns stays untouched. Struct fields are read at fixed x64 offsets taken from the n8.0 headers.</summary>
public static unsafe class FfmpegNative
{
    public const long NoPts = long.MinValue;
    public const int AvMediaTypeVideo = 0;
    public const int AvseekFlagBackward = 1;
    public const int AvErrorEagain = -11;
    public const int AvErrorEof = -541478725; // -MKTAG('E','O','F',' ')
    public const int AvMediaTypeAudio = 1;
    public const int PixFmtBgra = 28, PixFmtD3d11 = 174, PixFmtNone = -1;
    public const int HwDeviceD3d11va = 7;
    public const int SwsBilinear = 2, SwsArea = 0x20;
    public const int SwsCsItu709 = 1, SwsCsItu601 = 5;

    // AVFormatContext
    public const int FmtNbStreams = 44, FmtStreams = 48, FmtDuration = 104;
    // AVStream
    public const int StreamCodecpar = 16, StreamTimeBase = 32, StreamStartTime = 40, StreamDuration = 48, StreamAvgFrameRate = 88;
    // AVCodecParameters
    public const int ParCodecId = 4, ParWidth = 72, ParHeight = 76, ParColorRange = 100, ParColorSpace = 112;
    // AVPacket
    public const int PktPts = 8, PktDts = 16, PktStreamIndex = 36;
    // AVCodecContext (x64, n8.0): width/height are used to sanity-check the offset chain before hardware fields are written
    public const int CtxWidth = 112, CtxHeight = 116, CtxGetFormat = 192, CtxSampleRate = 344, CtxHwDeviceCtx = 560, CtxThreadCount = 656;
    // AVCodecParameters audio
    public const int ParChLayout = 128, ParSampleRate = 152;
    // AVFrame
    public const int FrNbSamples = 112, FrSampleRate = 180;
    public const int FrData = 0, FrLinesize = 64, FrWidth = 104, FrHeight = 108, FrFormat = 116, FrPts = 136, FrPktDts = 144;

    static nint _avutil, _swresample, _swscale, _avcodec, _avformat, _avfilter;
    static string? _dir;
    static readonly object Gate = new();

    public static bool IsLoaded { get; private set; }
    public static string LastError { get; private set; } = "";

    // avformat
    public static delegate* unmanaged[Cdecl]<nint*, byte*, nint, nint*, int> avformat_open_input;
    public static delegate* unmanaged[Cdecl]<nint, nint*, int> avformat_find_stream_info;
    public static delegate* unmanaged[Cdecl]<nint, int, int, int, nint*, int, int> av_find_best_stream;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> av_read_frame;
    public static delegate* unmanaged[Cdecl]<nint, int, long, int, int> av_seek_frame;
    public static delegate* unmanaged[Cdecl]<nint*, void> avformat_close_input;
    // avcodec
    public static delegate* unmanaged[Cdecl]<int, nint> avcodec_find_decoder;
    public static delegate* unmanaged[Cdecl]<nint, nint> avcodec_alloc_context3;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> avcodec_parameters_to_context;
    public static delegate* unmanaged[Cdecl]<nint, nint, nint*, int> avcodec_open2;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> avcodec_send_packet;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> avcodec_receive_frame;
    public static delegate* unmanaged[Cdecl]<nint, void> avcodec_flush_buffers;
    public static delegate* unmanaged[Cdecl]<nint*, void> avcodec_free_context;
    public static delegate* unmanaged[Cdecl]<nint> av_packet_alloc;
    public static delegate* unmanaged[Cdecl]<nint*, void> av_packet_free;
    public static delegate* unmanaged[Cdecl]<nint, void> av_packet_unref;
    // avutil
    public static delegate* unmanaged[Cdecl]<nint> av_frame_alloc;
    public static delegate* unmanaged[Cdecl]<nint*, void> av_frame_free;
    public static delegate* unmanaged[Cdecl]<nint, void> av_frame_unref;
    public static delegate* unmanaged[Cdecl]<nint, nint> av_frame_clone;
    public static delegate* unmanaged[Cdecl]<nint*, byte*, byte*, int, int> av_dict_set;
    public static delegate* unmanaged[Cdecl]<nint*, void> av_dict_free;
    public static delegate* unmanaged[Cdecl]<int, byte*, nuint, byte*> av_strerror;
    public static delegate* unmanaged[Cdecl]<nint*, int, byte*, nint, int, int> av_hwdevice_ctx_create;
    public static delegate* unmanaged[Cdecl]<nint, nint> av_buffer_ref;
    public static delegate* unmanaged[Cdecl]<nint*, void> av_buffer_unref;
    public static delegate* unmanaged[Cdecl]<nint, nint, int, int> av_hwframe_transfer_data;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> av_frame_copy_props;
    public static delegate* unmanaged[Cdecl]<int, byte*> av_get_sample_fmt_name;
    public static delegate* unmanaged[Cdecl]<nint, byte*, nuint, int> av_channel_layout_describe;
    // avfilter
    public static delegate* unmanaged[Cdecl]<nint> avfilter_graph_alloc;
    public static delegate* unmanaged[Cdecl]<byte*, nint> avfilter_get_by_name;
    public static delegate* unmanaged[Cdecl]<nint*, nint, byte*, byte*, nint, nint, int> avfilter_graph_create_filter;
    public static delegate* unmanaged[Cdecl]<nint, uint, nint, uint, int> avfilter_link;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> avfilter_graph_config;
    public static delegate* unmanaged[Cdecl]<nint*, void> avfilter_graph_free;
    public static delegate* unmanaged[Cdecl]<nint, nint, int, int> av_buffersrc_add_frame_flags;
    public static delegate* unmanaged[Cdecl]<nint, nint, int> av_buffersink_get_frame;
    // swscale
    public static delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, int, int, nint, nint, double*, nint> sws_getCachedContext;
    public static delegate* unmanaged[Cdecl]<nint, byte**, int*, int, int, byte**, int*, int> sws_scale;
    public static delegate* unmanaged[Cdecl]<nint, int*, int, int*, int, int, int, int, int> sws_setColorspaceDetails;
    public static delegate* unmanaged[Cdecl]<int, int*> sws_getCoefficients;
    public static delegate* unmanaged[Cdecl]<nint, void> sws_freeContext;

    /// <summary>Loads the five DLLs from <paramref name="binDir"/> (the bundle's bin\64bit). Safe to call repeatedly.</summary>
    public static bool Load(string binDir)
    {
        lock (Gate)
        {
            if (IsLoaded) return true;
            try
            {
                if (_dir != binDir) { Obs.ObsNative.SetDllDirectory(binDir); _dir = binDir; }
                // dependency order: avutil first, avformat last
                _avutil = Open(binDir, "avutil-60.dll");
                _swresample = Open(binDir, "swresample-6.dll");
                _swscale = Open(binDir, "swscale-9.dll");
                _avcodec = Open(binDir, "avcodec-62.dll");
                _avformat = Open(binDir, "avformat-62.dll");
                _avfilter = Open(binDir, "avfilter-11.dll");

                avformat_open_input = (delegate* unmanaged[Cdecl]<nint*, byte*, nint, nint*, int>)Sym(_avformat, "avformat_open_input");
                avformat_find_stream_info = (delegate* unmanaged[Cdecl]<nint, nint*, int>)Sym(_avformat, "avformat_find_stream_info");
                av_find_best_stream = (delegate* unmanaged[Cdecl]<nint, int, int, int, nint*, int, int>)Sym(_avformat, "av_find_best_stream");
                av_read_frame = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avformat, "av_read_frame");
                av_seek_frame = (delegate* unmanaged[Cdecl]<nint, int, long, int, int>)Sym(_avformat, "av_seek_frame");
                avformat_close_input = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avformat, "avformat_close_input");

                avcodec_find_decoder = (delegate* unmanaged[Cdecl]<int, nint>)Sym(_avcodec, "avcodec_find_decoder");
                avcodec_alloc_context3 = (delegate* unmanaged[Cdecl]<nint, nint>)Sym(_avcodec, "avcodec_alloc_context3");
                avcodec_parameters_to_context = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avcodec, "avcodec_parameters_to_context");
                avcodec_open2 = (delegate* unmanaged[Cdecl]<nint, nint, nint*, int>)Sym(_avcodec, "avcodec_open2");
                avcodec_send_packet = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avcodec, "avcodec_send_packet");
                avcodec_receive_frame = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avcodec, "avcodec_receive_frame");
                avcodec_flush_buffers = (delegate* unmanaged[Cdecl]<nint, void>)Sym(_avcodec, "avcodec_flush_buffers");
                avcodec_free_context = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avcodec, "avcodec_free_context");
                av_packet_alloc = (delegate* unmanaged[Cdecl]<nint>)Sym(_avcodec, "av_packet_alloc");
                av_packet_free = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avcodec, "av_packet_free");
                av_packet_unref = (delegate* unmanaged[Cdecl]<nint, void>)Sym(_avcodec, "av_packet_unref");

                av_frame_alloc = (delegate* unmanaged[Cdecl]<nint>)Sym(_avutil, "av_frame_alloc");
                av_frame_free = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avutil, "av_frame_free");
                av_frame_unref = (delegate* unmanaged[Cdecl]<nint, void>)Sym(_avutil, "av_frame_unref");
                av_frame_clone = (delegate* unmanaged[Cdecl]<nint, nint>)Sym(_avutil, "av_frame_clone");
                av_dict_set = (delegate* unmanaged[Cdecl]<nint*, byte*, byte*, int, int>)Sym(_avutil, "av_dict_set");
                av_dict_free = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avutil, "av_dict_free");
                av_strerror = (delegate* unmanaged[Cdecl]<int, byte*, nuint, byte*>)Sym(_avutil, "av_strerror");

                av_hwdevice_ctx_create = (delegate* unmanaged[Cdecl]<nint*, int, byte*, nint, int, int>)Sym(_avutil, "av_hwdevice_ctx_create");
                av_buffer_ref = (delegate* unmanaged[Cdecl]<nint, nint>)Sym(_avutil, "av_buffer_ref");
                av_buffer_unref = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avutil, "av_buffer_unref");
                av_hwframe_transfer_data = (delegate* unmanaged[Cdecl]<nint, nint, int, int>)Sym(_avutil, "av_hwframe_transfer_data");
                av_frame_copy_props = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avutil, "av_frame_copy_props");
                av_get_sample_fmt_name = (delegate* unmanaged[Cdecl]<int, byte*>)Sym(_avutil, "av_get_sample_fmt_name");
                av_channel_layout_describe = (delegate* unmanaged[Cdecl]<nint, byte*, nuint, int>)Sym(_avutil, "av_channel_layout_describe");

                avfilter_graph_alloc = (delegate* unmanaged[Cdecl]<nint>)Sym(_avfilter, "avfilter_graph_alloc");
                avfilter_get_by_name = (delegate* unmanaged[Cdecl]<byte*, nint>)Sym(_avfilter, "avfilter_get_by_name");
                avfilter_graph_create_filter = (delegate* unmanaged[Cdecl]<nint*, nint, byte*, byte*, nint, nint, int>)Sym(_avfilter, "avfilter_graph_create_filter");
                avfilter_link = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, int>)Sym(_avfilter, "avfilter_link");
                avfilter_graph_config = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avfilter, "avfilter_graph_config");
                avfilter_graph_free = (delegate* unmanaged[Cdecl]<nint*, void>)Sym(_avfilter, "avfilter_graph_free");
                av_buffersrc_add_frame_flags = (delegate* unmanaged[Cdecl]<nint, nint, int, int>)Sym(_avfilter, "av_buffersrc_add_frame_flags");
                av_buffersink_get_frame = (delegate* unmanaged[Cdecl]<nint, nint, int>)Sym(_avfilter, "av_buffersink_get_frame");

                sws_getCachedContext = (delegate* unmanaged[Cdecl]<nint, int, int, int, int, int, int, int, nint, nint, double*, nint>)Sym(_swscale, "sws_getCachedContext");
                sws_scale = (delegate* unmanaged[Cdecl]<nint, byte**, int*, int, int, byte**, int*, int>)Sym(_swscale, "sws_scale");
                sws_setColorspaceDetails = (delegate* unmanaged[Cdecl]<nint, int*, int, int*, int, int, int, int, int>)Sym(_swscale, "sws_setColorspaceDetails");
                sws_getCoefficients = (delegate* unmanaged[Cdecl]<int, int*>)Sym(_swscale, "sws_getCoefficients");
                sws_freeContext = (delegate* unmanaged[Cdecl]<nint, void>)Sym(_swscale, "sws_freeContext");

                IsLoaded = true;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                LastError = e.Message;
                Diagnostics.ErrorReporter.Report("ffmpeg load", e);
                return false;
            }
        }
    }

    static nint Open(string dir, string file) => NativeLibrary.Load(Path.Combine(dir, file));
    static nint Sym(nint lib, string name) => NativeLibrary.GetExport(lib, name);

    public static string Err(int code)
    {
        byte* buf = stackalloc byte[128];
        return av_strerror(code, buf, 128) == null ? "error " + code : Marshal.PtrToStringAnsi((nint)buf) ?? "error " + code;
    }

    public static nint ReadPtr(nint p, int off) => *(nint*)(p + off);
    public static int ReadInt(nint p, int off) => *(int*)(p + off);
    public static long ReadLong(nint p, int off) => *(long*)(p + off);
}
