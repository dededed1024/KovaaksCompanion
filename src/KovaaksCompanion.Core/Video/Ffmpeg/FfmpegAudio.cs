using System.Runtime.InteropServices;
using System.Text;
using static KovaaksCompanion.Core.Video.Ffmpeg.FfmpegNative;

namespace KovaaksCompanion.Core.Video.Ffmpeg;

/// <summary>Audio side of <see cref="FfmpegPlayer"/>: its own demuxer and decoder, an atempo filter graph (pitch-preserving, any speed) and a WinMM waveOut device.
/// It follows the player's clock: whenever the clock restarts (play, pause, seek, speed change) the output is flushed and restarted at the clock position.</summary>
internal sealed unsafe class FfmpegAudio : IDisposable
{
    public readonly record struct ClockState(double Pos, bool Running, double Speed, int Epoch);

    const int Rate = 48000, BlockSamples = 960, BlockBytes = BlockSamples * 4, Blocks = 5;
    const double BlockSec = (double)BlockSamples / Rate;
    const double DriftLimit = 0.15, DeviceLatency = 0.03;

    readonly Func<ClockState> _clock;
    readonly double _startSec;
    readonly Thread _thread;
    volatile bool _stop;

    nint _fmt, _ctx, _pkt, _in, _out;
    int _si;
    double _tbNum, _tbDen;
    string _layout = "stereo";

    // per start
    nint _graph, _src, _sink;
    double _speed = 1, _anchor, _mediaCursor, _fifoStart, _trimTo;
    long _outSamples;
    bool _eofIn, _eofSink, _flushed, _firstSubmit, _anchorSet;
    byte[] _fifo = new byte[BlockBytes * 16];
    int _fifoCount;
    DateTime _startedAt;
    public long BlocksSubmitted;
    public int Restarts;
    public double LastDrift;
    public bool GraphBuilt => _graph != 0;

    volatile bool _muted;
    /// <summary>Silences the output at once (device volume) and in the blocks still to be submitted.</summary>
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (_wo != 0) waveOutSetVolume(_wo, value ? 0u : 0xFFFFFFFFu); }
    }

    // waveOut
    nint _wo, _hdrs;
    readonly nint[] _bufs = new nint[Blocks];
    int _nextBlock;

    public static FfmpegAudio? TryCreate(string path, Func<ClockState> clock, double startSec)
    {
        var a = new FfmpegAudio(clock, startSec);
        if (!a.Init(path)) { a.Dispose(); return null; }
        a._thread.Start();
        return a;
    }

    FfmpegAudio(Func<ClockState> clock, double startSec)
    {
        _clock = clock; _startSec = startSec;
        _thread = new Thread(Run) { IsBackground = true, Name = "kc-ffmpeg-audio" };
    }

    bool Init(string path)
    {
        var url = Encoding.UTF8.GetBytes(path + "\0");
        nint fmt = 0;
        fixed (byte* u = url) if (avformat_open_input(&fmt, u, 0, null) < 0) return false;
        _fmt = fmt;
        if (avformat_find_stream_info(_fmt, null) < 0) return false;
        _si = av_find_best_stream(_fmt, AvMediaTypeAudio, -1, -1, null, 0);
        if (_si < 0) return false;
        var stream = *(nint*)(ReadPtr(_fmt, FmtStreams) + _si * sizeof(nint));
        var par = ReadPtr(stream, StreamCodecpar);
        int num = ReadInt(stream, StreamTimeBase), den = ReadInt(stream, StreamTimeBase + 4);
        if (num <= 0 || den <= 0) return false;
        _tbNum = num; _tbDen = den;
        byte* desc = stackalloc byte[128];
        if (av_channel_layout_describe(par + ParChLayout, desc, 128) > 0) _layout = Marshal.PtrToStringAnsi((nint)desc) ?? "stereo";
        var codec = avcodec_find_decoder(ReadInt(par, ParCodecId));
        if (codec == 0) return false;
        _ctx = avcodec_alloc_context3(codec);
        if (_ctx == 0 || avcodec_parameters_to_context(_ctx, par) < 0 || avcodec_open2(_ctx, codec, null) < 0) return false;
        _pkt = av_packet_alloc(); _in = av_frame_alloc(); _out = av_frame_alloc();
        if (_pkt == 0 || _in == 0 || _out == 0) return false;
        return OpenDevice();
    }

    // ---- thread ----

    void Run()
    {
        try
        {
            int epoch = -1;
            bool started = false;
            while (!_stop)
            {
                var c = _clock();
                if (c.Epoch != epoch || !c.Running)
                {
                    if (started) { Flush(); started = false; }
                    epoch = c.Epoch;
                }
                if (!c.Running) { Thread.Sleep(5); continue; }
                if (!started)
                {
                    if (!StartAt(c)) { Thread.Sleep(50); continue; }
                    started = true;
                }
                Pump(c);
                if (started && Drifted(c)) { Flush(); started = false; epoch = -1; }
                Thread.Sleep(3);
            }
        }
        catch (Exception e) { Diagnostics.ErrorReporter.Report("ffmpeg audio", e); } // best effort: the video keeps playing
    }

    bool Drifted(ClockState c)
    {
        if (_eofSink || (DateTime.UtcNow - _startedAt).TotalMilliseconds < 400) return false;
        double played = _mediaCursor - Pending() * BlockSec * _speed - _fifoCount / 4.0 / Rate * _speed;
        LastDrift = played - c.Pos;
        return Math.Abs(LastDrift) > DriftLimit;
    }

    bool StartAt(ClockState c)
    {
        _speed = c.Speed;
        Restarts++;
        long ts = (long)((c.Pos + _startSec) * _tbDen / _tbNum);
        if (av_seek_frame(_fmt, _si, Math.Max(0, ts), AvseekFlagBackward) < 0) av_seek_frame(_fmt, _si, 0, AvseekFlagBackward);
        avcodec_flush_buffers(_ctx);
        FreeGraph();
        _outSamples = 0; _fifoCount = 0;
        _eofIn = _eofSink = _flushed = _anchorSet = false;
        _firstSubmit = true;
        _trimTo = c.Pos;
        _fifoStart = c.Pos; _mediaCursor = c.Pos;
        _startedAt = DateTime.UtcNow;
        return true;
    }

    void Flush()
    {
        if (_wo != 0) waveOutReset(_wo);
        _fifoCount = 0;
        FreeGraph();
    }

    // ---- output ----

    void Pump(ClockState c)
    {
        while (Pending() < Blocks)
        {
            while (_fifoCount < BlockBytes)
            {
                if (!Produce()) return;
            }
            if (_firstSubmit)
            {
                // decoding after the restart took time: skip what the clock has already passed (plus the device latency)
                double now = _clock().Pos + DeviceLatency * _speed;
                _firstSubmit = false;
                if (now > _fifoStart)
                {
                    _trimTo = now;
                    int drop = (int)((now - _fifoStart) / _speed * Rate) * 4;
                    if (drop >= _fifoCount) { _fifoCount = 0; _fifoStart = now; continue; }
                    Buffer.BlockCopy(_fifo, drop, _fifo, 0, _fifoCount - drop);
                    _fifoCount -= drop; _fifoStart += drop / 4.0 / Rate * _speed;
                    if (_fifoCount < BlockBytes) { _firstSubmit = false; continue; }
                }
            }
            if (!Submit()) return;
        }
    }

    int Pending()
    {
        if (_wo == 0) return Blocks;
        int n = 0;
        for (int i = 0; i < Blocks; i++) if ((*(uint*)(_hdrs + i * 48 + 24) & 0x10) != 0) n++;
        return n;
    }

    bool Submit()
    {
        int i = _nextBlock;
        nint hdr = _hdrs + i * 48;
        if ((*(uint*)(hdr + 24) & 0x10) != 0) return false;
        Marshal.Copy(_fifo, 0, _bufs[i], BlockBytes);
        if (_muted) NativeMemory.Clear((void*)_bufs[i], BlockBytes);
        Buffer.BlockCopy(_fifo, BlockBytes, _fifo, 0, _fifoCount - BlockBytes);
        _fifoCount -= BlockBytes;
        _fifoStart += BlockSec * _speed;
        waveOutWrite(_wo, hdr, 48);
        BlocksSubmitted++;
        _nextBlock = (i + 1) % Blocks;
        return true;
    }

    // ---- decode + filter ----

    /// <summary>Adds more filtered samples to the FIFO. False when none can be produced now (end of stream or error).</summary>
    bool Produce()
    {
        if (_eofSink) return false;
        for (int guard = 0; guard < 64; guard++)
        {
            if (_graph != 0)
            {
                int r = av_buffersink_get_frame(_sink, _out);
                if (r == 0) { Append(); av_frame_unref(_out); return true; }
                if (r == AvErrorEof) { _eofSink = true; return false; }
                if (r != AvErrorEagain) return false;
            }
            if (_eofIn)
            {
                if (_graph == 0) { _eofSink = true; return false; }
                if (!_flushed) { av_buffersrc_add_frame_flags(_src, 0, 0); _flushed = true; continue; }
                _eofSink = true;
                return false;
            }
            if (!DecodeOne()) _eofIn = true;
        }
        return false;
    }

    void Append()
    {
        int n = ReadInt(_out, FrNbSamples);
        byte* data = *(byte**)(_out + FrData);
        double m = _anchor + _outSamples * _speed / Rate;
        _outSamples += n;
        int skip = 0;
        if (_trimTo > m) skip = (int)((_trimTo - m) / _speed * Rate);
        if (skip >= n) { _mediaCursor = m + n * _speed / Rate; return; }
        int bytes = (n - skip) * 4;
        if (_fifoCount + bytes > _fifo.Length) Array.Resize(ref _fifo, Math.Max(_fifo.Length * 2, _fifoCount + bytes));
        if (_fifoCount == 0) _fifoStart = m + skip * _speed / Rate;
        Marshal.Copy((nint)(data + skip * 4), _fifo, _fifoCount, bytes);
        _fifoCount += bytes;
        _mediaCursor = m + n * _speed / Rate;
    }

    /// <summary>Decodes one audio frame and pushes it into the graph. False at end of file.</summary>
    bool DecodeOne()
    {
        while (true)
        {
            int r = avcodec_receive_frame(_ctx, _in);
            if (r == 0)
            {
                long p = ReadLong(_in, FrPts);
                if (!_anchorSet)
                {
                    if (!BuildGraph()) return false;
                    _anchor = p == NoPts ? _trimTo : p * _tbNum / _tbDen - _startSec;
                    _anchorSet = true;
                }
                av_buffersrc_add_frame_flags(_src, _in, 0);
                av_frame_unref(_in);
                return true;
            }
            if (r != AvErrorEagain) return false;
            r = av_read_frame(_fmt, _pkt);
            if (r < 0) { avcodec_send_packet(_ctx, 0); continue; }
            if (ReadInt(_pkt, PktStreamIndex) == _si) avcodec_send_packet(_ctx, _pkt);
            av_packet_unref(_pkt);
        }
    }

    bool BuildGraph()
    {
        FreeGraph();
        int rate = ReadInt(_in, FrSampleRate);
        if (rate <= 0) rate = Rate;
        var fmtName = Marshal.PtrToStringAnsi((nint)av_get_sample_fmt_name(ReadInt(_in, FrFormat)));
        if (fmtName == null) return false;
        _graph = avfilter_graph_alloc();
        if (_graph == 0) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string srcArgs = $"time_base=1/{rate}:sample_rate={rate}:sample_fmt={fmtName}:channel_layout={_layout}";
        if (Create(out _src, "abuffer", "in", srcArgs) < 0) return FailGraph();

        nint last = _src;
        var chain = new List<string>();
        double t = _speed;
        // atempo accepts 0.5..100 per instance; split below 0.5
        while (t < 0.5) { chain.Add("0.5"); t /= 0.5; }
        if (Math.Abs(t - 1) > 1e-6 || chain.Count > 0) chain.Add(t.ToString("0.#####", inv));
        int idx = 0;
        foreach (var tempo in chain)
        {
            if (Create(out var f, "atempo", "t" + idx++, "tempo=" + tempo) < 0) return FailGraph();
            if (avfilter_link(last, 0, f, 0) < 0) return FailGraph();
            last = f;
        }
        if (Create(out var fmtF, "aformat", "fmt", $"sample_fmts=s16:sample_rates={Rate}:channel_layouts=stereo") < 0) return FailGraph();
        if (avfilter_link(last, 0, fmtF, 0) < 0) return FailGraph();
        if (Create(out _sink, "abuffersink", "out", null) < 0) return FailGraph();
        if (avfilter_link(fmtF, 0, _sink, 0) < 0) return FailGraph();
        if (avfilter_graph_config(_graph, 0) < 0) return FailGraph();
        return true;
    }

    int Create(out nint ctx, string filter, string name, string? args)
    {
        ctx = 0;
        var fb = Encoding.ASCII.GetBytes(filter + "\0");
        var nb = Encoding.ASCII.GetBytes(name + "\0");
        var ab = args == null ? null : Encoding.ASCII.GetBytes(args + "\0");
        fixed (byte* f = fb, n = nb, a = ab)
        {
            var def = avfilter_get_by_name(f);
            if (def == 0) return -1;
            nint c = 0;
            int r = avfilter_graph_create_filter(&c, def, n, a, 0, _graph);
            ctx = c;
            return r;
        }
    }

    bool FailGraph() { FreeGraph(); _eofIn = _eofSink = true; return false; }

    void FreeGraph()
    {
        if (_graph != 0) { var g = _graph; avfilter_graph_free(&g); _graph = 0; }
        _src = _sink = 0;
        _anchorSet = false;
    }

    // ---- waveOut ----

    bool OpenDevice()
    {
        var wf = new WaveFormat { Tag = 1, Channels = 2, SamplesPerSec = Rate, AvgBytesPerSec = Rate * 4, BlockAlign = 4, Bits = 16, Size = 0 };
        if (waveOutOpen(out _wo, 0xFFFFFFFF, ref wf, 0, 0, 0) != 0) { _wo = 0; return false; }
        _hdrs = (nint)NativeMemory.AllocZeroed((nuint)(Blocks * 48));
        for (int i = 0; i < Blocks; i++)
        {
            _bufs[i] = (nint)NativeMemory.AllocZeroed(BlockBytes);
            nint h = _hdrs + i * 48;
            *(nint*)h = _bufs[i];
            *(uint*)(h + 8) = BlockBytes;
            if (waveOutPrepareHeader(_wo, h, 48) != 0) return false;
        }
        return true;
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join();
        if (_wo != 0)
        {
            waveOutReset(_wo);
            for (int i = 0; i < Blocks && _hdrs != 0; i++) waveOutUnprepareHeader(_wo, _hdrs + i * 48, 48);
            waveOutClose(_wo);
            _wo = 0;
        }
        for (int i = 0; i < Blocks; i++) if (_bufs[i] != 0) { NativeMemory.Free((void*)_bufs[i]); _bufs[i] = 0; }
        if (_hdrs != 0) { NativeMemory.Free((void*)_hdrs); _hdrs = 0; }
        FreeGraph();
        if (_out != 0) { var f = _out; av_frame_free(&f); _out = 0; }
        if (_in != 0) { var f = _in; av_frame_free(&f); _in = 0; }
        if (_pkt != 0) { var k = _pkt; av_packet_free(&k); _pkt = 0; }
        if (_ctx != 0) { var c = _ctx; avcodec_free_context(&c); _ctx = 0; }
        if (_fmt != 0) { var f = _fmt; avformat_close_input(&f); _fmt = 0; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WaveFormat { public ushort Tag, Channels; public uint SamplesPerSec, AvgBytesPerSec; public ushort BlockAlign, Bits, Size; }

    [DllImport("winmm")] static extern int waveOutOpen(out nint hwo, uint device, ref WaveFormat fmt, nint callback, nint instance, uint flags);
    [DllImport("winmm")] static extern int waveOutPrepareHeader(nint hwo, nint hdr, uint size);
    [DllImport("winmm")] static extern int waveOutUnprepareHeader(nint hwo, nint hdr, uint size);
    [DllImport("winmm")] static extern int waveOutSetVolume(nint hwo, uint volume);
    [DllImport("winmm")] static extern int waveOutWrite(nint hwo, nint hdr, uint size);
    [DllImport("winmm")] static extern int waveOutReset(nint hwo);
    [DllImport("winmm")] static extern int waveOutClose(nint hwo);
}
