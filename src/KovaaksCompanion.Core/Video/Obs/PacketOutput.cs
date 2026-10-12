using System.Runtime.InteropServices;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>
/// One encoded frame/audio chunk as libobs hands it to an output. Times are in <see cref="TimebaseNum"/>/<see cref="TimebaseDen"/> second units;
/// <see cref="CaptureNs"/> is the os_gettime_ns() (QPC nanoseconds) the video frame was captured at, 0 for audio.
/// </summary>
public sealed record EncodedPacket(bool IsVideo, bool Keyframe, long Pts, long Dts, int TimebaseNum, int TimebaseDen, byte[] Data, long DtsUsec = 0, ulong CaptureNs = 0)
{
    public double PtsSeconds => Pts * (double)TimebaseNum / TimebaseDen;
    public double DtsSeconds => Dts * (double)TimebaseNum / TimebaseDen;
}

/// <summary>
/// A libobs output of our own ("kc_packets") that receives the encoded H.264 / AAC packets in managed code instead of muxing them:
/// the rolling buffer keeps the packets and writes a clip mp4 from them on demand.
/// </summary>
internal sealed unsafe class PacketOutput : IDisposable
{
    const string Id = "kc_packets";
    static bool _registered;
    static readonly object RegLock = new();
    [ThreadStatic] static PacketOutput? _pending;

    GCHandle _self;
    nint _output;
    nint _video, _audio;
    bool _started;
    string _error = "";

    public event Action<EncodedPacket>? Packet;
    public event Action<string>? Stopped;

    public PacketOutput(nint videoEncoder, nint audioEncoder, string name)
    {
        EnsureRegistered();
        _video = videoEncoder; _audio = audioEncoder;
        _self = GCHandle.Alloc(this);
        _pending = this;
        try { _output = ObsNative.obs_output_create(Id, name, 0, 0); }
        finally { _pending = null; }
        if (_output == 0) { _self.Free(); throw new InvalidOperationException("obs_output_create failed for " + Id); }
        ObsNative.obs_output_set_video_encoder(_output, _video);
        if (_audio != 0) ObsNative.obs_output_set_audio_encoder(_output, _audio, 0);
        ObsNative.obs_output_add_packet_callback(_output, (nint)(delegate* unmanaged[Cdecl]<nint, ObsNative.EncoderPacket*, ObsNative.EncoderPacketTime*, nint, void>)&CbPacketTimed, GCHandle.ToIntPtr(_self));
    }

    public bool IsActive => _output != 0 && ObsNative.obs_output_active(_output);
    public string LastError => _error.Length > 0 ? _error : ObsNative.Str(ObsNative.obs_output_get_last_error(_output)) ?? "";

    /// <summary>Starts the encoders and the packet flow; false when libobs refuses (see <see cref="LastError"/>).</summary>
    public bool Start() => ObsNative.obs_output_start(_output);

    /// <summary>H.264 SPS/PPS (Annex B) once the video encoder runs.</summary>
    public byte[]? VideoExtraData() => ExtraData(_video);

    /// <summary>AAC AudioSpecificConfig once the audio encoder runs.</summary>
    public byte[]? AudioExtraData() => _audio == 0 ? null : ExtraData(_audio);

    static byte[]? ExtraData(nint encoder)
    {
        if (!ObsNative.obs_encoder_get_extra_data(encoder, out var p, out var n) || p == 0 || n == 0) return null;
        var b = new byte[(int)n];
        Marshal.Copy(p, b, 0, b.Length);
        return b;
    }

    public void Stop()
    {
        if (_output == 0) return;
        ObsNative.obs_output_stop(_output);
    }

    public void Dispose()
    {
        if (_output == 0) return;
        if (ObsNative.obs_output_active(_output)) { ObsNative.obs_output_force_stop(_output); }
        ObsNative.obs_output_release(_output);
        _output = 0;
        if (_self.IsAllocated) _self.Free();
    }

    static void EnsureRegistered()
    {
        lock (RegLock)
        {
            if (_registered) return;
            var info = new ObsNative.OutputInfo
            {
                Id = (nint)Marshal.StringToCoTaskMemUTF8(Id),
                Flags = ObsNative.OutputVideo | ObsNative.OutputAudio | ObsNative.OutputEncoded,
                GetName = (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&CbGetName,
                Create = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint>)&CbCreate,
                Destroy = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&CbDestroy,
                Start = (nint)(delegate* unmanaged[Cdecl]<nint, byte>)&CbStart,
                Stop = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, void>)&CbStop,
                EncodedPacket = (nint)(delegate* unmanaged[Cdecl]<nint, ObsNative.EncoderPacket*, void>)&CbPacketIgnored,
                EncodedVideoCodecs = (nint)Marshal.StringToCoTaskMemUTF8("h264"),
                EncodedAudioCodecs = (nint)Marshal.StringToCoTaskMemUTF8("AAC"),
            };
            ObsNative.obs_register_output_s(info, (nuint)sizeof(ObsNative.OutputInfo));
            _registered = true;
        }
    }

    static PacketOutput? From(nint data) => data == 0 ? null : GCHandle.FromIntPtr(data).Target as PacketOutput;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static nint CbGetName(nint typeData) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static nint CbCreate(nint settings, nint output)
    {
        var p = _pending;
        if (p == null) return 0;
        p._output = output;
        return GCHandle.ToIntPtr(p._self);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void CbDestroy(nint data) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static byte CbStart(nint data)
    {
        var s = From(data);
        if (s == null) return 0;
        try
        {
            const uint flags = 0;
            if (!ObsNative.obs_output_can_begin_data_capture(s._output, flags)) return 0;
            if (!ObsNative.obs_output_initialize_encoders(s._output, flags)) return 0;
            if (!ObsNative.obs_output_begin_data_capture(s._output, flags)) return 0;
            s._started = true;
            return 1;
        }
        catch (Exception e) { AppLog.Write("obs", "output start failed: " + e); return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void CbStop(nint data, ulong ts)
    {
        var s = From(data);
        if (s == null) return;
        try
        {
            ObsNative.obs_output_end_data_capture(s._output);
            s._started = false;
            s.Stopped?.Invoke(s._error);
        }
        catch (Exception e) { AppLog.Write("obs", "output stop failed: " + e); }
    }

    /// <summary>The output callback itself is not used: the packet callback below sees the same packets together with their capture time.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void CbPacketIgnored(nint data, ObsNative.EncoderPacket* pkt) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static void CbPacketTimed(nint output, ObsNative.EncoderPacket* pkt, ObsNative.EncoderPacketTime* time, nint param)
    {
        var s = From(param);
        if (s == null || pkt == null || pkt->Data == null) return;
        try
        {
            var bytes = new byte[(int)pkt->Size];
            Marshal.Copy((nint)pkt->Data, bytes, 0, bytes.Length);
            s.Packet?.Invoke(new EncodedPacket(pkt->Type == 1, pkt->Keyframe != 0, pkt->Pts, pkt->Dts, pkt->TimebaseNum, pkt->TimebaseDen, bytes,
                pkt->DtsUsec, time != null ? time->Cts : 0));
        }
        catch (Exception e) { AppLog.Write("obs", "packet handler failed: " + e); }
    }
}
