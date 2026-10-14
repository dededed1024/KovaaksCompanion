using System.IO;

namespace KovaaksCompanion.App;

/// <summary>Animated GIF writer: one shared 255 colour palette, ordered dithering, and per frame only the changed rectangle (unchanged pixels are transparent).</summary>
internal sealed class GifFrameEncoder : IDisposable
{
    const int TransparentIndex = 255;
    static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    readonly Stream _out;
    readonly int _w, _h, _delay;
    readonly byte[] _lut;
    readonly byte _bgR, _bgG, _bgB;
    byte[]? _prev;
    bool _finished;

    // LZW bit packing state.
    int _bitBuf, _bitCnt, _blkLen;
    readonly byte[] _blk = new byte[255];

    /// <param name="bgra">Opaque or premultiplied BGRA frames used to build the palette.</param>
    public GifFrameEncoder(Stream output, int width, int height, int delayCentiseconds, (byte R, byte G, byte B) background, IEnumerable<byte[]> paletteSamples)
    {
        _out = output; _w = width; _h = height; _delay = delayCentiseconds;
        (_bgR, _bgG, _bgB) = background;
        var palette = BuildPalette(paletteSamples.Select(Flatten), TransparentIndex, out _lut);
        WriteHeader(palette);
    }

    byte[] Flatten(byte[] bgra)
    {
        var o = new byte[bgra.Length];
        FlattenInto(bgra, o);
        return o;
    }

    /// <summary>Pbgra32 over the background colour into opaque BGRA.</summary>
    void FlattenInto(byte[] src, byte[] dst)
    {
        for (var i = 0; i < src.Length; i += 4)
        {
            var inv = 255 - src[i + 3];
            dst[i] = (byte)Math.Min(255, src[i] + inv * _bgB / 255);
            dst[i + 1] = (byte)Math.Min(255, src[i + 1] + inv * _bgG / 255);
            dst[i + 2] = (byte)Math.Min(255, src[i + 2] + inv * _bgR / 255);
            dst[i + 3] = 255;
        }
    }

    /// <summary>Quantizes, diffs against the previous frame and writes the changed rectangle.</summary>
    public void AddFrame(byte[] pbgra)
    {
        var flat = Flatten(pbgra);
        var idx = new byte[_w * _h];
        Parallel.For(0, _h, y =>
        {
            for (var x = 0; x < _w; x++)
            {
                var p = y * _w + x;
                var o = (Bayer[(y & 3) * 4 + (x & 3)] * 2 - 15) >> 2;
                var b = Math.Clamp(flat[p * 4] + o, 0, 255);
                var g = Math.Clamp(flat[p * 4 + 1] + o, 0, 255);
                var r = Math.Clamp(flat[p * 4 + 2] + o, 0, 255);
                idx[p] = _lut[((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2)];
            }
        });

        int x0 = 0, y0 = 0, rw = _w, rh = _h;
        byte[] region;
        if (_prev == null)
        {
            region = idx;
        }
        else
        {
            int minX = _w, minY = _h, maxX = -1, maxY = -1;
            for (var y = 0; y < _h; y++)
            {
                var row = y * _w;
                for (var x = 0; x < _w; x++)
                {
                    if (idx[row + x] == _prev[row + x]) continue;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    maxY = y;
                }
            }
            if (maxX < 0)
            {
                x0 = 0; y0 = 0; rw = 1; rh = 1;
                region = [TransparentIndex];
            }
            else
            {
                x0 = minX; y0 = minY; rw = maxX - minX + 1; rh = maxY - minY + 1;
                region = new byte[rw * rh];
                for (var y = 0; y < rh; y++)
                    for (var x = 0; x < rw; x++)
                    {
                        var p = (y0 + y) * _w + x0 + x;
                        region[y * rw + x] = idx[p] == _prev[p] ? (byte)TransparentIndex : idx[p];
                    }
            }
        }
        _prev = idx;
        WriteFrame(region, x0, y0, rw, rh);
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        _out.WriteByte(0x3B);
        _out.Flush();
    }

    public void Dispose() => Finish();

    void WriteHeader(byte[] palette)
    {
        Span<byte> head = stackalloc byte[13];
        "GIF89a"u8.CopyTo(head);
        head[6] = (byte)_w; head[7] = (byte)(_w >> 8);
        head[8] = (byte)_h; head[9] = (byte)(_h >> 8);
        head[10] = 0xF7; // global table, 8 bit colour, 256 entries
        head[11] = 0; head[12] = 0;
        _out.Write(head);
        _out.Write(palette);
        // Loop forever.
        _out.Write([0x21, 0xFF, 0x0B, .. "NETSCAPE2.0"u8, 0x03, 0x01, 0x00, 0x00, 0x00]);
    }

    void WriteFrame(byte[] px, int x, int y, int w, int h)
    {
        // Graphic control: do not dispose (keep the canvas), transparent index, delay.
        _out.Write([0x21, 0xF9, 0x04, 0x05, (byte)_delay, (byte)(_delay >> 8), TransparentIndex, 0x00]);
        _out.Write([0x2C, (byte)x, (byte)(x >> 8), (byte)y, (byte)(y >> 8), (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0x00]);
        _out.WriteByte(8);
        Lzw(px);
        _out.WriteByte(0);
    }

    void PutCode(int code, int size)
    {
        _bitBuf |= code << _bitCnt;
        _bitCnt += size;
        while (_bitCnt >= 8)
        {
            AddByte((byte)(_bitBuf & 0xFF));
            _bitBuf >>= 8;
            _bitCnt -= 8;
        }
    }

    void AddByte(byte b)
    {
        _blk[_blkLen++] = b;
        if (_blkLen == 255) FlushBlock();
    }

    void FlushBlock()
    {
        if (_blkLen == 0) return;
        _out.WriteByte((byte)_blkLen);
        _out.Write(_blk, 0, _blkLen);
        _blkLen = 0;
    }

    void Lzw(byte[] px)
    {
        const int clear = 256, eoi = 257;
        _bitBuf = 0; _bitCnt = 0; _blkLen = 0;
        var dict = new Dictionary<int, int>(8192);
        int next = eoi + 1, size = 9;

        // Writes a code, then widens the code size once the next free entry no longer fits (same order as the decoder).
        void Emit(int code)
        {
            PutCode(code, size);
            if (next > (1 << size) - 1 && size < 12) size++;
        }

        PutCode(clear, size);
        int prefix = px[0];
        for (var i = 1; i < px.Length; i++)
        {
            int k = px[i];
            var key = (prefix << 8) | k;
            if (dict.TryGetValue(key, out var found)) { prefix = found; continue; }
            Emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
            }
            else
            {
                PutCode(clear, size);
                dict.Clear();
                next = eoi + 1; size = 9;
            }
            prefix = k;
        }
        Emit(prefix);
        PutCode(eoi, size);
        if (_bitCnt > 0) { AddByte((byte)(_bitBuf & 0xFF)); _bitBuf = 0; _bitCnt = 0; }
        FlushBlock();
    }

    // Median cut over a 6-6-6 bit histogram. Priority of a box = longest channel range * sqrt(pixels), so small accent colours still get entries.
    static byte[] BuildPalette(IEnumerable<byte[]> opaqueBgra, int colors, out byte[] lut)
    {
        var hist = new int[262144];
        foreach (var f in opaqueBgra)
            for (var i = 0; i < f.Length; i += 4)
                hist[((f[i + 2] >> 2) << 12) | ((f[i + 1] >> 2) << 6) | (f[i] >> 2)]++;

        var ent = new List<(int Key, int Count)>();
        for (var k = 0; k < hist.Length; k++)
            if (hist[k] > 0) ent.Add((k, hist[k]));
        var arr = ent.ToArray();

        static int Chan(int key, int axis) => axis == 0 ? key >> 12 & 63 : axis == 1 ? key >> 6 & 63 : key & 63;

        (int Lo, int Hi, int Axis, double Pri) Describe(int lo, int hi)
        {
            Span<int> mn = [63, 63, 63], mx = [0, 0, 0];
            long cnt = 0;
            for (var i = lo; i < hi; i++)
            {
                cnt += arr[i].Count;
                for (var a = 0; a < 3; a++)
                {
                    var c = Chan(arr[i].Key, a);
                    if (c < mn[a]) mn[a] = c;
                    if (c > mx[a]) mx[a] = c;
                }
            }
            var axis = 0; var range = mx[0] - mn[0];
            for (var a = 1; a < 3; a++)
                if (mx[a] - mn[a] > range) { range = mx[a] - mn[a]; axis = a; }
            return (lo, hi, axis, hi - lo < 2 ? -1 : (range + 0.5) * Math.Sqrt(cnt));
        }

        var boxes = new List<(int Lo, int Hi, int Axis, double Pri)>();
        if (arr.Length > 0) boxes.Add(Describe(0, arr.Length));
        while (boxes.Count < colors)
        {
            var best = -1;
            for (var i = 0; i < boxes.Count; i++)
                if (boxes[i].Pri > 0 && (best < 0 || boxes[i].Pri > boxes[best].Pri)) best = i;
            if (best < 0) break;
            var (lo, hi, axis, _) = boxes[best];
            Array.Sort(arr, lo, hi - lo, Comparer<(int Key, int Count)>.Create((a, b) => Chan(a.Key, axis).CompareTo(Chan(b.Key, axis))));
            long total = 0;
            for (var i = lo; i < hi; i++) total += arr[i].Count;
            long run = 0; var mid = lo + 1;
            for (var i = lo; i < hi - 1; i++)
            {
                run += arr[i].Count;
                mid = i + 1;
                if (run * 2 >= total) break;
            }
            boxes[best] = Describe(lo, mid);
            boxes.Add(Describe(mid, hi));
        }

        var palette = new byte[768];
        var pal = new (int R, int G, int B)[boxes.Count];
        for (var n = 0; n < boxes.Count; n++)
        {
            double r = 0, g = 0, b = 0, tot = 0;
            for (var i = boxes[n].Lo; i < boxes[n].Hi; i++)
            {
                var (key, cnt) = arr[i];
                r += (key >> 12 & 63) * (double)cnt; g += (key >> 6 & 63) * (double)cnt; b += (key & 63) * (double)cnt; tot += cnt;
            }
            pal[n] = ((int)Math.Round(r / tot * 4 + 2), (int)Math.Round(g / tot * 4 + 2), (int)Math.Round(b / tot * 4 + 2));
            palette[n * 3] = (byte)Math.Min(255, pal[n].R);
            palette[n * 3 + 1] = (byte)Math.Min(255, pal[n].G);
            palette[n * 3 + 2] = (byte)Math.Min(255, pal[n].B);
        }

        lut = new byte[262144];
        var lutOut = lut;
        Parallel.For(0, 64, rr =>
        {
            for (var k = rr << 12; k < (rr + 1) << 12; k++)
            {
                int r = (k >> 12 & 63) * 4 + 2, g = (k >> 6 & 63) * 4 + 2, b = (k & 63) * 4 + 2;
                var bestD = int.MaxValue; var bestI = 0;
                for (var n = 0; n < pal.Length; n++)
                {
                    int dr = r - pal[n].R, dg = g - pal[n].G, db = b - pal[n].B;
                    var d = dr * dr + dg * dg + db * db;
                    if (d < bestD) { bestD = d; bestI = n; }
                }
                lutOut[k] = (byte)bestI;
            }
        });
        return palette;
    }
}
