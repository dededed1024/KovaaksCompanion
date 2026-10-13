using KovaaksCompanion.Core.Video.Mp4;
using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>
/// Rolling buffer of the encoded packets of one recording. Packets go to one chunk file per GOP (a video keyframe starts a new chunk) in the
/// buffer folder; chunks older than <c>keep</c> are deleted. A clip is written from the packets with <see cref="Mp4Writer"/>, so cutting needs no re-encode.
/// Times are seconds on the encoder's clock (video pts 0 = first frame); <see cref="OriginNs"/> places that clock on the QPC (os_gettime_ns) clock.
/// </summary>
public sealed class PacketRing : IDisposable
{
    public const string ChunkPrefix = "kc_";
    public const string ChunkExt = ".chunk";

    /// <param name="CaptureNs">os_gettime_ns the frame was captured at (0 for audio and for outputs libobs gives no capture time).</param>
    sealed record Entry(bool IsVideo, bool Key, double DtsSec, double PtsSec, int Length, int Chunk, long Offset, long CaptureNs = 0);

    sealed class Chunk
    {
        public int Id;
        public string Path = "";
        public FileStream? Writer;
        public double LastDtsSec;
        public int Entries;
    }

    readonly object _lock = new();
    readonly string _dir;
    readonly double _keepSec;
    readonly List<Entry> _entries = [];
    readonly List<Chunk> _chunks = [];
    readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    Chunk? _open;
    int _nextChunk;
    bool _hasOrigin, _originFromCapture;
    int _arrivalSamples, _captureSamples;
    long _arrivalOriginNs = long.MaxValue, _captureOriginNs = long.MaxValue;
    /// <summary>Typical delay between a frame's capture and its packet arriving, used when libobs gives no capture time (secondary canvases).</summary>
    const long ArrivalLatencyNs = 90_000_000;
    const int ArrivalSamples = 120;
    /// <summary>Frames over which the origin is refined from their capture times.</summary>
    const int CaptureSamples = 120;
    readonly Func<long> _nowNs;
    double _lastVideoSec = double.NegativeInfinity;

    /// <param name="nowNs">QPC nanoseconds clock (libobs os_gettime_ns); default is libobs's own.</param>
    public PacketRing(string bufferDir, TimeSpan keep, Func<long>? nowNs = null)
    {
        _nowNs = nowNs ?? (() => (long)ObsRuntime.NowNs());
        _dir = bufferDir;
        _keepSec = Math.Max(5, keep.TotalSeconds);
        Directory.CreateDirectory(bufferDir);
    }

    /// <summary>QPC nanoseconds (os_gettime_ns) of ring time 0; valid once <see cref="HasOrigin"/>.</summary>
    public long OriginNs { get; private set; }
    public bool HasOrigin => _hasOrigin;

    /// <summary>Newest video packet time in the ring (seconds); -inf when empty.</summary>
    public double LastVideoSec { get { lock (_lock) return _lastVideoSec; } }

    /// <summary>Oldest keyframe time in the ring; null when empty.</summary>
    public double? FirstKeySec { get { lock (_lock) return _entries.FirstOrDefault(e => e.IsVideo && e.Key)?.DtsSec; } }

    public double ToRingSec(long qpcNs) => (qpcNs - OriginNs) / 1e9;
    public long ToQpcNs(double ringSec) => OriginNs + (long)Math.Round(ringSec * 1e9);

    /// <summary>Adds one packet (video is converted to length-prefixed NAL units). Audio before the first video keyframe is dropped.</summary>
    public void Append(EncodedPacket p)
    {
        byte[] data = p.IsVideo ? Avc.ToLengthPrefixed(p.Data) : p.Data;
        if (data.Length == 0) return;
        double scale = (double)p.TimebaseNum / p.TimebaseDen;
        double dts = p.Dts * scale, pts = p.Pts * scale;
        lock (_lock)
        {
            if (p.IsVideo && p.CaptureNs != 0 && _captureSamples < CaptureSamples)
            {
                // a late compositor tick only pushes a frame's capture time later, so the earliest (capture - pts) of the first frames is the truest origin;
                // one frame alone (usually delayed while the capture starts up) would put every later frame off by that delay
                _captureOriginNs = Math.Min(_captureOriginNs, (long)p.CaptureNs - (long)Math.Round(pts * 1e9));
                OriginNs = _captureOriginNs;
                _hasOrigin = _originFromCapture = true;
                _captureSamples++;
            }
            else if (p.IsVideo && !_originFromCapture)
            {
                if (_arrivalSamples < ArrivalSamples)
                {
                    // no capture time: the earliest (arrival - pts) over the first frames is the one with the least queueing delay
                    _arrivalOriginNs = Math.Min(_arrivalOriginNs, _nowNs() - (long)Math.Round(pts * 1e9));
                    OriginNs = _arrivalOriginNs - ArrivalLatencyNs;
                    if (++_arrivalSamples >= 30) _hasOrigin = true;
                }
            }
            if (p.IsVideo && p.Keyframe) StartChunk();
            if (_open == null) return;
            var w = _open.Writer!;
            var off = w.Position;
            w.Write(data);
            _entries.Add(new Entry(p.IsVideo, p.Keyframe, dts, pts, data.Length, _open.Id, off, (long)p.CaptureNs));
            _open.LastDtsSec = dts; _open.Entries++;
            if (p.IsVideo) _lastVideoSec = dts;
        }
    }

    void StartChunk()
    {
        if (_open != null) { _open.Writer!.Flush(); _open.Writer.Dispose(); _open.Writer = null; }
        var c = new Chunk { Id = _nextChunk++ };
        c.Path = Path.Combine(_dir, $"{ChunkPrefix}{_runId}_{c.Id:D6}{ChunkExt}");
        c.Writer = new FileStream(c.Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        _chunks.Add(c); _open = c;
        Evict();
    }

    void Evict()
    {
        var newest = _lastVideoSec;
        while (_chunks.Count > 2 && _chunks[0] != _open && _chunks[0].LastDtsSec < newest - _keepSec)
        {
            var c = _chunks[0];
            _chunks.RemoveAt(0);
            _entries.RemoveAll(e => e.Chunk == c.Id);
            try { File.Delete(c.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Writes the packets covering [<paramref name="fromSec"/>, <paramref name="toSec"/>] to <paramref name="path"/>: it starts at the last video keyframe at or before
    /// <paramref name="fromSec"/> (the ring's first keyframe when that is older than the ring) and ends with the last video packet at or before <paramref name="toSec"/>.
    /// With <paramref name="originSec"/> the file's time 0 is that ring time (the lead-in from the keyframe is cut off by an edit list) instead of the keyframe.
    /// Returns the actual (start, end) in ring seconds, or null when the ring holds no video in the range.
    /// </summary>
    public (double Start, double End)? WriteClip(string path, double fromSec, double toSec, AvcInfo video, AacInfo? audio, double? originSec = null)
    {
        List<Entry> sel;
        Dictionary<int, string> paths;
        lock (_lock)
        {
            _open?.Writer?.Flush();
            if (fromSec > _lastVideoSec) return null;
            int first = -1;
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (!e.IsVideo || !e.Key) continue;
                if (first < 0 || e.DtsSec <= fromSec) first = i; else break;
            }
            if (first < 0) return null;
            var t0 = _entries[first].DtsSec;
            int last = -1;
            for (int i = first; i < _entries.Count; i++)
                if (_entries[i].IsVideo && _entries[i].DtsSec <= toSec) last = i;
            if (last < first) return null;
            var tEnd = _entries[last].DtsSec;
            sel = _entries.Skip(first).Take(last - first + 1).Where(e => e.IsVideo || (e.DtsSec >= t0 && e.DtsSec <= tEnd + 0.05)).ToList();
            paths = _chunks.ToDictionary(c => c.Id, c => c.Path);
        }

        // Video frames are stamped with the time each one was really captured (like the per-frame RTCTIME of the ffmpeg recorder), not with a grid of
        // frame number x interval: a frame that libobs rendered late keeps its true time. Audio keeps the encoder clock.
        var times = FrameTimes(sel);
        var samples = sel.Select((e, i) => new Mp4Sample(e.IsVideo, e.Key, times[i], e.PtsSec + (times[i] - e.DtsSec), e.Length)).ToList();
        var readers = new Dictionary<int, FileStream>();
        try
        {
            using var outStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            Mp4Writer.Write(outStream, video, audio, samples, (i, o) =>
            {
                var e = sel[i];
                if (!readers.TryGetValue(e.Chunk, out var r))
                    readers[e.Chunk] = r = new FileStream(paths[e.Chunk], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                r.Position = e.Offset;
                var buf = new byte[e.Length];
                r.ReadExactly(buf);
                o.Write(buf);
            }, originSec);
        }
        finally { foreach (var r in readers.Values) r.Dispose(); }
        int firstV = sel.FindIndex(e => e.IsVideo), lastV = sel.FindLastIndex(e => e.IsVideo);
        return (times[firstV], times[lastV]);
    }

    /// <summary>Strictly increasing ring time of every selected entry: captured frames use their capture time, the rest their packet time.</summary>
    double[] FrameTimes(List<Entry> sel)
    {
        const double MinStep = 1e-4;
        var t = new double[sel.Count];
        var prev = double.NegativeInfinity;
        for (int i = 0; i < sel.Count; i++)
        {
            var e = sel[i];
            if (!e.IsVideo) { t[i] = e.DtsSec; continue; }
            var v = e.CaptureNs != 0 ? (e.CaptureNs - OriginNs) / 1e9 : e.DtsSec;
            // never earlier than the packet's own clock (the origin is the earliest capture - pts): a frame cannot be captured before it was timed
            v = Math.Max(v, e.DtsSec);
            if (v <= prev) v = prev + MinStep;
            t[i] = prev = v;
        }
        return t;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var c in _chunks)
            {
                try { c.Writer?.Dispose(); } catch (IOException) { }
                try { File.Delete(c.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            _chunks.Clear(); _entries.Clear(); _open = null;
        }
    }
}
