using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using static KovaaksCompanion.Core.Video.Ffmpeg.FfmpegNative;

namespace KovaaksCompanion.Core.Video.Ffmpeg;

/// <summary>Plays a video file with the FFmpeg libraries of the libobs bundle: a decode thread fills a small frame queue, a QPC-based clock (any speed, no restart on change)
/// picks the frame to show, and seeks are frame accurate (seek to the previous keyframe, decode forward to the target). Video only; <see cref="Position"/> is the
/// clock position in seconds from the first frame. Needs <see cref="FfmpegNative.Load"/> first.</summary>
public sealed unsafe class FfmpegPlayer : IDisposable
{
    /// <summary>Same value as obs_media_state OBS_MEDIA_STATE_ENDED, so callers can treat both players alike.</summary>
    public const int StateEnded = 6;
    const int QueueMax = 8;

    sealed class QFrame { public nint P; public double Pts; }

    readonly object _gate = new();
    readonly List<QFrame> _queue = [];
    Thread? _thread;
    bool _stop, _disposed;

    nint _fmt, _ctx, _pkt, _work, _sws, _swf, _hwDev;
    bool _hw, _hwFail;
    nint _codec, _par, _got;
    double _resumeAt, _inSeekTarget = -1;
    FfmpegAudio? _audio;
    int _epoch;
    double _lastGoodPts;
    int _vs;
    double _tbNum, _tbDen, _startSec, _frameDur;
    int _swsSrcW, _swsSrcH, _swsSrcFmt, _swsDstW, _swsDstH, _srcRange, _colorSpace;

    // clock + state, all under _gate
    bool _playing, _clockLive, _eof, _ended, _seekPending;
    double _speed = 1, _basePos, _seekTarget, _newestPts = -1, _endPos;
    long _baseTick;
    int _seekGen;
    QFrame? _cur;
    long _curSerial, _scaledSerial = -1;
    int _scaledW, _scaledH;
    nint _scaled;

    public double Duration { get; private set; }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public string? Error { get; private set; }
    public int State { get { lock (_gate) return _ended ? StateEnded : _playing ? 3 : 4; } }
    public double Position { get { lock (_gate) return PosLocked(); } }
    /// <summary>Presentation time of the frame <see cref="ReadFrame"/> currently shows (-1 before the first).</summary>
    public double ShownPts { get { lock (_gate) return _cur?.Pts ?? -1; } }

    /// <summary>True when the decoder runs on the GPU (D3D11VA); false after a fallback to software.</summary>
    public bool HardwareDecode => _hw;

    /// <summary>Diagnostics: decoder thread count read from the codec context, audio blocks sent to the device and audio restarts since open.</summary>
    public string Diagnostics => $"threads={(_ctx == 0 ? 0 : ReadInt(_ctx, CtxThreadCount))} audio={(_audio == null ? "none" : $"blocks={_audio.BlocksSubmitted} restarts={_audio.Restarts} drift={_audio.LastDrift * 1000:F0}ms graph={_audio.GraphBuilt}")}";

    public static FfmpegPlayer? Open(string path, bool hardware = true, bool audio = true)
    {
        if (!IsLoaded) return null;
        var p = new FfmpegPlayer();
        if (!p.Init(path, hardware)) { p.Dispose(); return null; }
        if (audio) p._audio = FfmpegAudio.TryCreate(path, p.ClockSnapshot, p._startSec);
        p._thread = new Thread(p.DecodeLoop) { IsBackground = true, Name = "kc-ffmpeg-decode" };
        p._thread.Start();
        return p;
    }

    bool Init(string path, bool hardware)
    {
        var url = Encoding.UTF8.GetBytes(path + "\0");
        nint fmt = 0;
        int r;
        fixed (byte* u = url) r = avformat_open_input(&fmt, u, 0, null);
        if (r < 0) { Error = "open: " + Err(r); return false; }
        _fmt = fmt;
        r = avformat_find_stream_info(_fmt, null);
        if (r < 0) { Error = "stream info: " + Err(r); return false; }
        _vs = av_find_best_stream(_fmt, AvMediaTypeVideo, -1, -1, null, 0);
        if (_vs < 0) { Error = "no video stream"; return false; }

        var stream = *(nint*)(ReadPtr(_fmt, FmtStreams) + _vs * sizeof(nint));
        var par = ReadPtr(stream, StreamCodecpar);
        int num = ReadInt(stream, StreamTimeBase), den = ReadInt(stream, StreamTimeBase + 4);
        if (num <= 0 || den <= 0) { Error = "bad time base"; return false; }
        _tbNum = num; _tbDen = den;
        long start = ReadLong(stream, StreamStartTime);
        _startSec = start == NoPts ? 0 : start * _tbNum / _tbDen;
        VideoWidth = ReadInt(par, ParWidth);
        VideoHeight = ReadInt(par, ParHeight);
        _srcRange = ReadInt(par, ParColorRange);
        _colorSpace = ReadInt(par, ParColorSpace);
        int fn = ReadInt(stream, StreamAvgFrameRate), fd = ReadInt(stream, StreamAvgFrameRate + 4);
        _frameDur = fn > 0 && fd > 0 ? (double)fd / fn : 1.0 / 60;

        long dur = ReadLong(_fmt, FmtDuration); // AV_TIME_BASE units
        long sdur = ReadLong(stream, StreamDuration);
        Duration = dur > 0 && dur != NoPts ? dur / 1e6 : sdur > 0 && sdur != NoPts ? sdur * _tbNum / _tbDen : 0;

        _codec = avcodec_find_decoder(ReadInt(par, ParCodecId));
        if (_codec == 0) { Error = "no decoder"; return false; }
        _par = par;
        if (!OpenCodec(hardware)) return false;
        _pkt = av_packet_alloc();
        _work = av_frame_alloc();
        return _pkt != 0 && _work != 0;
    }


    /// <summary>(Re)creates the decoder context, on the GPU first when <paramref name="hw"/>; a context that fails to open with the GPU is reopened in software.</summary>
    bool OpenCodec(bool hw)
    {
        if (_ctx != 0) { var old = _ctx; avcodec_free_context(&old); _ctx = 0; }
        _ctx = avcodec_alloc_context3(_codec);
        if (_ctx == 0) { Error = "alloc codec"; return false; }
        int r = avcodec_parameters_to_context(_ctx, _par);
        if (r < 0) { Error = "codec params: " + Err(r); return false; }
        _hw = hw && TryEnableHardware();
        nint opts = 0;
        fixed (byte* k = "threads\0"u8, v = "auto\0"u8) av_dict_set(&opts, k, v, 0);
        r = avcodec_open2(_ctx, _codec, &opts);
        av_dict_free(&opts);
        if (r < 0)
        {
            if (_hw) return OpenCodec(false);
            Error = "open codec: " + Err(r);
            return false;
        }
        return true;
    }

    bool TryEnableHardware()
    {
        // the field offsets come from the n8.0 headers; refuse to write through them unless the leading fields read back right
        if (ReadInt(_ctx, CtxWidth) != VideoWidth || ReadInt(_ctx, CtxHeight) != VideoHeight) return false;
        if (_hwDev == 0)
        {
            nint d = 0;
            if (av_hwdevice_ctx_create(&d, HwDeviceD3d11va, null, 0, 0) < 0) return false;
            _hwDev = d;
        }
        *(nint*)(_ctx + CtxHwDeviceCtx) = av_buffer_ref(_hwDev);
        *(nint*)(_ctx + CtxGetFormat) = (nint)(delegate* unmanaged[Cdecl]<nint, int*, int>)&GetFormat;
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static int GetFormat(nint ctx, int* fmts)
    {
        int last = PixFmtNone;
        for (; *fmts != PixFmtNone; fmts++)
        {
            if (*fmts == PixFmtD3d11) return PixFmtD3d11;
            last = *fmts;
        }
        return last; // the software format comes last in the list
    }

    // ---- control ----

    public void Play()
    {
        lock (_gate)
        {
            if (_ended) SeekLocked(0);
            if (_playing) return;
            _basePos = PosLocked();
            _baseTick = Stopwatch.GetTimestamp();
            _playing = true;
            _epoch++;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_playing) return;
            _basePos = PosLocked();
            _playing = false;
            _epoch++;
        }
    }

    public bool Muted
    {
        get => _audio?.Muted ?? false;
        set { if (_audio is { } a) a.Muted = value; }
    }

    public void Seek(double seconds) { lock (_gate) SeekLocked(seconds); }

    public void SetSpeed(double speed)
    {
        if (speed <= 0) return;
        lock (_gate)
        {
            _basePos = PosLocked();
            _baseTick = Stopwatch.GetTimestamp();
            _speed = speed;
            _epoch++;
        }
    }

    void SeekLocked(double t)
    {
        if (Duration > 0) t = Math.Min(t, Duration);
        t = Math.Max(0, t);
        foreach (var f in _queue) FreeFrame(f);
        _queue.Clear();
        _seekTarget = t;
        _seekGen++;
        _seekPending = true;
        _eof = false; _ended = false;
        _newestPts = -1;
        _basePos = t;
        _baseTick = Stopwatch.GetTimestamp();
        _clockLive = false;
        _epoch++;
        Monitor.PulseAll(_gate);
    }

    FfmpegAudio.ClockState ClockSnapshot()
    {
        lock (_gate)
        {
            double pos = PosLocked();
            return new FfmpegAudio.ClockState(pos, _playing && _clockLive && !_ended, _speed, _epoch);
        }
    }

    /// <summary>Clock position; also advances the end-of-media and starved-decoder states. Caller holds _gate.</summary>
    double PosLocked()
    {
        if (!_playing || !_clockLive) return _ended ? _endPos : _basePos;
        long now = Stopwatch.GetTimestamp();
        double p = _basePos + (now - _baseTick) / (double)Stopwatch.Frequency * _speed;
        if (_eof && p >= _endPos)
        {
            _ended = true; _playing = false; _basePos = _endPos;
            return _endPos;
        }
        if (!_eof && _newestPts >= 0)
        {
            // decoder is behind the clock: hold at the newest frame instead of drifting ahead of the picture
            double limit = _newestPts + _frameDur * 0.999;
            if (p > limit) { p = limit; _basePos = p; _baseTick = now; }
        }
        return p;
    }

    // ---- display ----

    /// <summary>Scales the current frame to width x height (BGRA) and passes the mapped bytes (pointer, row stride) to <paramref name="read"/>. False when no frame is decoded yet.</summary>
    public bool ReadFrame(int width, int height, Action<nint, int> read)
    {
        if (width <= 0 || height <= 0) return false;
        lock (_gate)
        {
            if (_disposed) return false;
            double pos = PosLocked();
            while (_queue.Count > 0 && _queue[0].Pts <= pos + 1e-6)
            {
                if (_cur != null) FreeFrame(_cur);
                _cur = _queue[0]; _curSerial++;
                _queue.RemoveAt(0);
                Monitor.PulseAll(_gate);
            }
            if (_cur == null) return false;
            int stride = width * 4;
            if (_scaled == 0 || _scaledW != width || _scaledH != height)
            {
                if (_scaled != 0) NativeMemory.AlignedFree((void*)_scaled);
                _scaled = (nint)NativeMemory.AlignedAlloc((nuint)((long)stride * height), 32);
                _scaledW = width; _scaledH = height; _scaledSerial = -1;
            }
            if (_scaledSerial != _curSerial)
            {
                if (!Scale(_cur.P, width, height, stride)) return false;
                _scaledSerial = _curSerial;
            }
            read(_scaled, stride);
            return true;
        }
    }

    bool Scale(nint frame, int w, int h, int stride)
    {
        int sw = ReadInt(frame, FrWidth), sh = ReadInt(frame, FrHeight), sf = ReadInt(frame, FrFormat);
        _sws = sws_getCachedContext(_sws, sw, sh, sf, w, h, PixFmtBgra, SwsBilinear, 0, 0, null);
        if (_sws == 0) return false;
        if (sw != _swsSrcW || sh != _swsSrcH || sf != _swsSrcFmt || w != _swsDstW || h != _swsDstH)
        {
            (_swsSrcW, _swsSrcH, _swsSrcFmt, _swsDstW, _swsDstH) = (sw, sh, sf, w, h);
            // the default is BT.601; untagged HD video is BT.709
            int cs = _colorSpace == 1 || (_colorSpace is 0 or 2 && VideoHeight >= 720) ? SwsCsItu709 : SwsCsItu601;
            int* table = sws_getCoefficients(cs);
            sws_setColorspaceDetails(_sws, table, _srcRange == 2 ? 1 : 0, table, 1, 0, 1 << 16, 1 << 16);
        }
        byte** src = stackalloc byte*[4];
        int* srcStride = stackalloc int[4];
        for (int i = 0; i < 4; i++) { src[i] = *(byte**)(frame + FrData + i * 8); srcStride[i] = ReadInt(frame, FrLinesize + i * 4); }
        byte** dst = stackalloc byte*[4] { (byte*)_scaled, null, null, null };
        int* dstStride = stackalloc int[4] { stride, 0, 0, 0 };
        return sws_scale(_sws, src, srcStride, 0, sh, dst, dstStride) == h;
    }

    // ---- decoding ----

    void DecodeLoop()
    {
        try { Decode(); }
        catch (Exception e) { Error = e.ToString(); KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("ffmpeg decode", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.High); }
    }

    void Decode()
    {
        bool draining = false;
        while (true)
        {
            int gen;
            double target = -1;
            lock (_gate)
            {
                while (!_stop && !_seekPending && !_hwFail && (_queue.Count >= QueueMax || _eof)) Monitor.Wait(_gate);
                if (_stop) return;
                gen = _seekGen;
                if (_seekPending) { _seekPending = false; target = _seekTarget; }
            }
            if (_hwFail)
            {
                // the GPU decoder broke: continue in software from where it was (or from the pending seek target)
                _hwFail = false;
                double resume = _resumeAt;
                OpenCodec(false);
                draining = false;
                if (target < 0) target = resume;
            }
            if (target >= 0)
            {
                long ts = (long)((target + _startSec) * _tbDen / _tbNum);
                if (av_seek_frame(_fmt, _vs, ts, AvseekFlagBackward) < 0) av_seek_frame(_fmt, _vs, 0, AvseekFlagBackward);
                avcodec_flush_buffers(_ctx);
                draining = false;
                _inSeekTarget = target;
                SeekForward(target, gen, ref draining);
                _inSeekTarget = -1;
                continue;
            }
            if (!NextFrame(ref draining, out double pts)) { if (!_hwFail) MarkEof(gen); continue; }
            Enqueue(gen, pts);
        }
    }

    /// <summary>After a keyframe seek, decodes up to the frame that covers <paramref name="target"/> and queues it (and its successor).</summary>
    void SeekForward(double target, int gen, ref bool draining)
    {
        nint held = 0;
        double heldPts = 0;
        while (true)
        {
            lock (_gate) { if (_stop || _seekGen != gen) { if (held != 0) { var h = held; av_frame_free(&h); } return; } }
            if (!NextFrame(ref draining, out double pts))
            {
                if (_hwFail) { if (held != 0) { var h = held; av_frame_free(&h); } return; }
                if (held != 0) { Push(gen, held, heldPts); held = 0; }
                MarkEof(gen);
                return;
            }
            if (pts <= target + 1e-6)
            {
                if (held != 0) { var h = held; av_frame_free(&h); }
                held = av_frame_clone(_got); heldPts = pts;
                av_frame_unref(_got);
                continue;
            }
            if (held != 0) { Push(gen, held, heldPts); held = 0; }
            Enqueue(gen, pts);
            return;
        }
    }

    /// <summary>Decodes the next frame into _work and returns its presentation time in seconds. False at end of file.</summary>
    bool NextFrame(ref bool draining, out double pts)
    {
        pts = 0;
        while (true)
        {
            int r = avcodec_receive_frame(_ctx, _work);
            if (r == 0)
            {
                _got = _work;
                if (_hw && ReadInt(_work, FrFormat) == PixFmtD3d11)
                {
                    // download to system memory right away so queued frames do not pin the decoder's small surface pool
                    if (_swf == 0) _swf = av_frame_alloc();
                    av_frame_unref(_swf);
                    if (av_hwframe_transfer_data(_swf, _work, 0) < 0 || av_frame_copy_props(_swf, _work) < 0) return FailHardware();
                    av_frame_unref(_work);
                    _got = _swf;
                }
                long p = ReadLong(_got, FrPts);
                if (p == NoPts) p = ReadLong(_got, FrPktDts);
                pts = p == NoPts ? _newestPts + _frameDur : p * _tbNum / _tbDen - _startSec;
                return true;
            }
            if (r == AvErrorEof) return false;
            if (r != AvErrorEagain) return _hw ? FailHardware() : false;
            if (draining) return false;
            lock (_gate) { if (_stop || _seekPending) return false; }
            r = av_read_frame(_fmt, _pkt);
            if (r < 0) { avcodec_send_packet(_ctx, 0); draining = true; continue; }
            if (ReadInt(_pkt, PktStreamIndex) == _vs) avcodec_send_packet(_ctx, _pkt);
            av_packet_unref(_pkt);
        }
    }

    bool FailHardware()
    {
        _hwFail = true;
        _resumeAt = _inSeekTarget >= 0 ? _inSeekTarget : _lastGoodPts;
        av_frame_unref(_work);
        return false;
    }

    void Enqueue(int gen, double pts)
    {
        var clone = av_frame_clone(_got);
        av_frame_unref(_got);
        if (clone != 0) Push(gen, clone, pts);
    }

    void Push(int gen, nint frame, double pts)
    {
        lock (_gate)
        {
            if (_seekGen != gen || _stop) { var f = frame; av_frame_free(&f); return; }
            _queue.Add(new QFrame { P = frame, Pts = pts });
            _newestPts = Math.Max(_newestPts, pts);
            _lastGoodPts = pts;
            if (!_clockLive)
            {
                _clockLive = true;
                _baseTick = Stopwatch.GetTimestamp();
            }
        }
    }

    void MarkEof(int gen)
    {
        lock (_gate)
        {
            if (_seekGen != gen || _seekPending) return;
            _eof = true;
            _endPos = Math.Max(_newestPts, 0) + _frameDur;
            if (_newestPts < 0 && _cur != null) _endPos = Duration;
            _clockLive = true;
        }
    }

    static void FreeFrame(QFrame f) { var p = f.P; av_frame_free(&p); f.P = 0; }

    public void Dispose()
    {
        _audio?.Dispose();
        _audio = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;
            Monitor.PulseAll(_gate);
        }
        _thread?.Join();
        lock (_gate)
        {
            foreach (var f in _queue) FreeFrame(f);
            _queue.Clear();
            if (_cur != null) { FreeFrame(_cur); _cur = null; }
        }
        if (_sws != 0) { sws_freeContext(_sws); _sws = 0; }
        if (_scaled != 0) { NativeMemory.AlignedFree((void*)_scaled); _scaled = 0; }
        if (_work != 0) { var w = _work; av_frame_free(&w); _work = 0; }
        if (_pkt != 0) { var k = _pkt; av_packet_free(&k); _pkt = 0; }
        if (_ctx != 0) { var c = _ctx; avcodec_free_context(&c); _ctx = 0; }
        if (_fmt != 0) { var f = _fmt; avformat_close_input(&f); _fmt = 0; }
        if (_swf != 0) { var w = _swf; av_frame_free(&w); _swf = 0; }
        if (_hwDev != 0) { var d = _hwDev; av_buffer_unref(&d); _hwDev = 0; }
    }
}
