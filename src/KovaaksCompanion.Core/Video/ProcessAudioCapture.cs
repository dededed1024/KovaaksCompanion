using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video;

/// <summary>
/// WASAPI process loopback (Windows 10 2004+): captures only the audio one process tree renders, as 48 kHz
/// 16-bit stereo PCM. <see cref="Pump"/> writes a gap-free real-time stream (silence is padded in) so the
/// consumer's timeline stays aligned with wall-clock.
/// </summary>
public sealed unsafe class ProcessAudioCapture : IDisposable
{
    public const int SampleRate = 48000, Channels = 2, BytesPerFrame = 4;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    private readonly int _pid;
    private IAudioClient? _client;
    private readonly AutoResetEvent _event = new(false);

    public ProcessAudioCapture(int processId) => _pid = processId;

    public string Error { get; private set; } = "";

    /// <summary>Activates and initializes the loopback client. False (with <see cref="Error"/>) when unsupported or refused.</summary>
    public bool TryInitialize()
    {
        if (!IsSupported) { Error = "process loopback needs Windows 10 2004+"; return false; }
        IntPtr prop = IntPtr.Zero, parms = IntPtr.Zero;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { type = PROCESS_LOOPBACK(1), pid, mode = INCLUDE_TARGET_PROCESS_TREE(0) } inside a VT_BLOB PROPVARIANT
            parms = Marshal.AllocHGlobal(12);
            Marshal.WriteInt32(parms, 0, 1); Marshal.WriteInt32(parms, 4, _pid); Marshal.WriteInt32(parms, 8, 0);
            prop = Marshal.AllocHGlobal(24);
            new Span<byte>((void*)prop, 24).Clear();
            Marshal.WriteInt16(prop, 0, 65);                 // VT_BLOB
            Marshal.WriteInt32(prop, 8, 12);                 // cbSize
            Marshal.WriteIntPtr(prop, 8 + IntPtr.Size, parms);

            var handler = new Handler();
            var iid = typeof(IAudioClient).GUID;
            ActivateAudioInterfaceAsync("VAD\\Process_Loopback", in iid, prop, handler, out var op);
            if (!handler.Done.Wait(5000)) { Error = "audio activation timed out"; return false; }
            GC.KeepAlive(op);
            if (handler.Result is not IAudioClient ac) { Error = "audio activation failed (process has no audio session?)"; return false; }
            _client = ac;

            var fmt = new WaveFormat
            {
                Tag = 1, Channels = Channels, SamplesPerSec = SampleRate, AvgBytesPerSec = SampleRate * BytesPerFrame,
                BlockAlign = BytesPerFrame, BitsPerSample = 16, CbSize = 0,
            };
            // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
            int hr = _client.Initialize(0, 0x88060000, 0, 0, ref fmt, IntPtr.Zero);
            if (hr < 0) { Error = $"IAudioClient.Initialize 0x{hr:X8}"; return false; }
            hr = _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0) { Error = $"SetEventHandle 0x{hr:X8}"; return false; }
            return true;
        }
        catch (Exception e) { Error = e.Message; return false; }
        finally
        {
            if (prop != IntPtr.Zero) Marshal.FreeHGlobal(prop);
            if (parms != IntPtr.Zero) Marshal.FreeHGlobal(parms);
        }
    }

    /// <summary>Blocking capture loop: runs until cancelled or the sink fails. Call after a successful <see cref="TryInitialize"/>.</summary>
    public void Pump(Stream sink, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("not initialized");
        var iid = typeof(IAudioCaptureClient).GUID;
        if (client.GetService(ref iid, out var svc) < 0) { Error = "GetService(capture) failed"; return; }
        var capture = (IAudioCaptureClient)svc;
        if (client.Start() < 0) { Error = "IAudioClient.Start failed"; return; }
        var clock = Stopwatch.StartNew();
        long written = 0;                                         // frames sent
        var zeros = new byte[SampleRate / 10 * BytesPerFrame];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _event.WaitOne(20);
                while (capture.GetNextPacketSize(out var n) >= 0 && n > 0)
                {
                    if (capture.GetBuffer(out var data, out var frames, out var flags, out _, out _) < 0) break;
                    int bytes = (int)frames * BytesPerFrame;
                    if ((flags & 0x2) != 0) WriteZeros(sink, bytes, zeros);
                    else sink.Write(new ReadOnlySpan<byte>((void*)data, bytes));
                    capture.ReleaseBuffer(frames);
                    written += frames;
                }
                // no packets while the process is silent: keep the stream on wall-clock
                long behind = (long)(clock.Elapsed.TotalSeconds * SampleRate) - written;
                if (behind > SampleRate / 20)
                {
                    WriteZeros(sink, (int)behind * BytesPerFrame, zeros);
                    written += behind;
                }
            }
        }
        catch (IOException) { }       // consumer (ffmpeg) went away
        catch (ObjectDisposedException) { }
        finally { client.Stop(); }
    }

    private static void WriteZeros(Stream sink, int bytes, byte[] zeros)
    {
        while (bytes > 0) { int n = Math.Min(bytes, zeros.Length); sink.Write(zeros, 0, n); bytes -= n; }
    }

    public void Dispose()
    {
        if (_client != null && OperatingSystem.IsWindows()) { try { Marshal.ReleaseComObject(_client); } catch { } }
        _client = null;
        _event.Dispose();
    }

    // ---- interop ----

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string path, in Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler, out IActivateAudioInterfaceAsyncOperation op);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort Tag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, BitsPerSample, CbSize;
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, ref WaveFormat format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, ref WaveFormat format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int hr, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op);
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true)]
    private sealed class Handler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new(false);
        public object? Result;
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                op.GetActivateResult(out var hr, out var iface);
                if (hr >= 0) Result = iface;
            }
            finally { Done.Set(); }
        }
    }
}
