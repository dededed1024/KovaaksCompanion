using System.Reflection;
using System.Runtime.InteropServices;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>Process-wide libobs lifetime: locates the unpacked bundle, starts the core once, loads the plugins and routes libobs logging to <see cref="AppLog"/>.</summary>
public static unsafe class ObsRuntime
{
    static readonly object Gate = new();
    static bool _loaded, _started, _videoReset, _audioReset;
    static string _root = "";
    static uint _fpsNum, _w, _h, _ow, _oh;

    public static bool IsStarted => _started;
    public static string Root => _root;
    public static string LastError { get; private set; } = "";
    /// <summary>Highest libobs log level written to the app log (warnings by default; KC_OBS_LOG=1 adds info).</summary>
    static readonly int MaxLogLevel = Environment.GetEnvironmentVariable("KC_OBS_LOG") == "1" ? ObsNative.LogInfo : ObsNative.LogWarning;

    /// <summary>Starts libobs from the bundle folder (contains bin\64bit, obs-plugins\64bit, data). Safe to call again; false (see <see cref="LastError"/>) when it cannot load.</summary>
    public static bool Start(string root) => Run(() => StartCore(root));

    [ThreadStatic] static bool _onObsThread;

    /// <summary>obs_startup initialises COM as an STA on the calling thread (an MTA thread gets RPC_E_CHANGED_MODE 0x80010106, as does any thread already in the other mode), and the plugins load on that same thread. Every libobs call that can start the core, load a plugin or create a source runs here, on a fresh STA thread; a nested call runs inline. Plugin worker threads manage their own COM.</summary>
    public static T Run<T>(Func<T> body)
    {
        if (_onObsThread) return body();
        T result = default!;
        Exception? error = null;
        var t = new Thread(() =>
        {
            _onObsThread = true;
            try { result = body(); }
            catch (Exception e) { error = e; }
        }) { IsBackground = true, Name = "libobs" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    static bool StartCore(string root)
    {
        lock (Gate)
        {
            if (_started) return true;
            try
            {
                _root = root;
                var bin = Path.Combine(root, "bin", "64bit");
                if (!File.Exists(Path.Combine(bin, "obs.dll"))) { LastError = "obs.dll missing in " + bin; return false; }
                if (!_loaded)
                {
                    ObsNative.SetDllDirectory(bin);
                    NativeLibrary.SetDllImportResolver(typeof(ObsNative).Assembly, (name, asm, path) =>
                        name == "obs" ? NativeLibrary.Load(Path.Combine(bin, "obs.dll")) : IntPtr.Zero);
                    _loaded = true;
                }
                ObsNative.base_set_log_handler((nint)(delegate* unmanaged[Cdecl]<int, byte*, nint, nint, void>)&LogHandler, 0);
                if (!ObsNative.obs_startup("en-US", 0, 0)) { LastError = "obs_startup failed"; return false; }
                _started = true;
                ObsNative.obs_add_data_path(Path.Combine(root, "data", "libobs") + "/");
                ObsNative.obs_add_module_path(Path.Combine(root, "obs-plugins", "64bit"), Path.Combine(root, "data", "obs-plugins", "%module%"));
                // win-capture decides at module load whether WGC is usable by asking the graphics device, so the device must exist before the modules load
                // (otherwise window capture silently falls back to BitBlt, which is black for D3D games)
                if (!ResetVideo(1920, 1080, 1920, 1080, 60)) return false;
                // the encoder plugins probe their GPU through obs-*-test.exe, which they look up next to the host exe; keep the copies only while they load
                var helpers = StageEncoderProbes(bin);
                try { ObsNative.obs_load_all_modules(); ObsNative.obs_post_load_modules(); }
                finally { foreach (var h in helpers) { try { File.Delete(h); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } } RemoveStrayPluginDir(); }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                LastError = e.Message;
                AppLog.Write("obs", "load failed: " + e);
                return false;
            }
        }
    }

    /// <summary>win-capture leaves an empty win-capture folder next to the host exe when it loads.</summary>
    static void RemoveStrayPluginDir()
    {
        try
        {
            var host = Path.GetDirectoryName(Environment.ProcessPath);
            var dir = host == null ? null : Path.Combine(host, "win-capture");
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static List<string> StageEncoderProbes(string bin)
    {
        var copies = new List<string>();
        var host = Path.GetDirectoryName(Environment.ProcessPath);
        if (host == null) return copies;
        foreach (var src in Directory.GetFiles(bin, "obs-*-test.exe"))
        {
            var dst = Path.Combine(host, Path.GetFileName(src));
            try { File.Copy(src, dst, true); copies.Add(dst); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("obs", $"cannot stage {Path.GetFileName(src)} in {host}: {e.Message}"); }
        }
        return copies;
    }

    /// <summary>Sets the compositing canvas. libobs allows this only while nothing is active; same size and rate is a no-op.</summary>
    public static bool ResetVideo(int width, int height, int outWidth, int outHeight, int fps, int adapter = 0)
    {
        lock (Gate)
        {
            if (_videoReset && _fpsNum == fps && _w == width && _h == height && _ow == outWidth && _oh == outHeight) return true;
            var gfx = Marshal.StringToCoTaskMemUTF8("libobs-d3d11");
            try
            {
                var v = new ObsNative.VideoInfo
                {
                    GraphicsModule = gfx, FpsNum = (uint)fps, FpsDen = 1, BaseWidth = (uint)width, BaseHeight = (uint)height,
                    OutputWidth = (uint)outWidth, OutputHeight = (uint)outHeight, OutputFormat = ObsNative.VideoFormatNv12, Adapter = (uint)adapter,
                    GpuConversion = 1, Colorspace = ObsNative.VideoCs709, Range = ObsNative.VideoRangePartial, ScaleType = ObsNative.ScaleBicubic,
                };
                var rc = ObsNative.obs_reset_video(ref v);
                if (rc != 0) { LastError = "obs_reset_video failed: " + rc; return false; }
                (_videoReset, _fpsNum, _w, _h, _ow, _oh) = (true, (uint)fps, (uint)width, (uint)height, (uint)outWidth, (uint)outHeight);
                return true;
            }
            finally { Marshal.FreeCoTaskMem(gfx); }
        }
    }

    [DllImport("rtworkq.dll")] static extern int RtwqStartup();

    public static bool ResetAudio() => Run(ResetAudioCore);

    static bool ResetAudioCore()
    {
        lock (Gate)
        {
            if (_audioReset) return true;
            // win-wasapi locks an RTWQ shared work queue but never starts the platform itself; without this it fails with MF_E_SHUTDOWN (0xC00D3E85)
            int hr = RtwqStartup();
            if (hr < 0) AppLog.Write("obs", $"RtwqStartup failed: 0x{hr:X8}");
            var a =new ObsNative.AudioInfo { SamplesPerSec = 48000, Speakers = ObsNative.SpeakersStereo };
            if (!ObsNative.obs_reset_audio(ref a)) { LastError = "obs_reset_audio failed"; return false; }
            _audioReset = true;
            return true;
        }
    }

    /// <summary>Ids of the encoder types libobs registered (e.g. jim_nvenc, h264_texture_amf, obs_qsv11, ffmpeg_aac).</summary>
    /// <summary>libobs os_gettime_ns(): the QPC clock in nanoseconds.</summary>
    public static ulong NowNs() => ObsNative.os_gettime_ns();

    public static List<string> EncoderIds() => Run(EncoderIdsCore);

    static List<string> EncoderIdsCore()
    {
        var l = new List<string>();
        for (nuint i = 0; ObsNative.obs_enum_encoder_types(i, out var p); i++) if (ObsNative.Str(p) is { } s) l.Add(s);
        return l;
    }

    /// <summary>(display name, libobs id) of every DirectShow video device, in the order the dshow_input source lists them.</summary>
    public static List<(string Name, string Id)> DshowDevices() => Run(DshowDevicesCore);

    static List<(string Name, string Id)> DshowDevicesCore()
    {
        var l = new List<(string, string)>();
        var props = ObsNative.obs_get_source_properties("dshow_input");
        if (props == 0) return l;
        try
        {
            var p = ObsNative.obs_properties_get(props, "video_device_id");
            if (p == 0) return l;
            for (nuint i = 0, n = ObsNative.obs_property_list_item_count(p); i < n; i++)
                if (ObsNative.Str(ObsNative.obs_property_list_item_name(p, i)) is { Length: > 0 } name && ObsNative.Str(ObsNative.obs_property_list_item_string(p, i)) is { Length: > 0 } id)
                    l.Add((name, id));
        }
        finally { ObsNative.obs_properties_destroy(props); }
        return l;
    }

    public static List<string> SourceIds()
    {
        var l = new List<string>();
        for (nuint i = 0; ObsNative.obs_enum_source_types(i, out var p); i++) if (ObsNative.Str(p) is { } s) l.Add(s);
        return l;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void LogHandler(int level, byte* fmt, nint args, nint param)
    {
        try
        {
            if (level > ObsNative.LogInfo) return;
            var buf = stackalloc byte[2048];
            ObsNative.vsnprintf(buf, 2048, fmt, args);
            var msg = Marshal.PtrToStringUTF8((nint)buf) ?? "";
            // info lines only for KC_OBS_LOG=1 or the game capture source (its window matching is the usual reason for an empty frame)
            if (level > MaxLogLevel && !msg.Contains("window-capture", StringComparison.Ordinal)) return;
            // win-capture probes its game-hook helper at load (graphics-hook is not bundled; window capture does not need it)
            if (level <= ObsNative.LogError && !msg.StartsWith("CreateProcessW failed", StringComparison.Ordinal)) ErrorReporter.Report("libobs", new VideoProblem(msg));
            AppLog.Write("obs", (level <= ObsNative.LogError ? "error: " : level <= ObsNative.LogWarning ? "warn: " : "info: ") + msg);
        }
        catch { }
    }
}
