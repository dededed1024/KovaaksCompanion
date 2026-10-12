using System.Buffers.Binary;

namespace KovaaksCompanion.Core.Video.Mp4;

/// <summary>H.264 video parameters for the avc1 sample entry.</summary>
public sealed record AvcInfo(int Width, int Height, byte[] Sps, byte[] Pps);

/// <summary>AAC-LC parameters for the mp4a sample entry.</summary>
public sealed record AacInfo(int SampleRate, int Channels, byte[] AudioSpecificConfig);

/// <summary>One sample in file order. Times are seconds on one shared clock; <see cref="Length"/> is the size of the bytes <c>writeSample</c> emits.</summary>
public sealed record Mp4Sample(bool IsVideo, bool Keyframe, double DtsSec, double PtsSec, int Length);

/// <summary>
/// Minimal MP4 muxer: one H.264 track (length-prefixed samples, no B-frame reordering beyond a ctts) and an optional AAC track,
/// moov before mdat (fast start). Samples are written interleaved in the order given, so a player needs no index rebuild.
/// </summary>
public static class Mp4Writer
{
    const int MovieTimescale = 1000;
    const int VideoTimescale = 90000;

    /// <summary>
    /// Writes the file. <paramref name="writeSample"/> receives the sample's index in <paramref name="samples"/> and must write exactly its <see cref="Mp4Sample.Length"/> bytes.
    /// <paramref name="originSec"/> is the clip's time 0 on the samples' clock (default: the first video sample): a later first video sample is delayed by an empty edit,
    /// an earlier one (decode lead-in before a keyframe) is cut off by an edit that starts inside the media.
    /// </summary>
    public static void Write(Stream output, AvcInfo video, AacInfo? audio, IReadOnlyList<Mp4Sample> samples, Action<int, Stream> writeSample, double? originSec = null)
    {
        var vIdx = Enumerable.Range(0, samples.Count).Where(i => samples[i].IsVideo).ToList();
        var aIdx = audio == null ? [] : Enumerable.Range(0, samples.Count).Where(i => !samples[i].IsVideo).ToList();
        if (vIdx.Count == 0) throw new ArgumentException("no video samples", nameof(samples));
        var t0 = originSec ?? samples[vIdx[0]].DtsSec;

        var ftyp = Box("ftyp", w => { w.Ascii("isom"); w.U32(0x200); w.Ascii("isom"); w.Ascii("iso2"); w.Ascii("avc1"); w.Ascii("mp41"); });
        // moov size does not depend on the chunk offsets (fixed-width stco), so build it once with zeros to learn the mdat start
        var offsets = new long[samples.Count];
        var moov = BuildMoov(video, audio, samples, vIdx, aIdx, t0, offsets);
        var dataStart = ftyp.Length + moov.Length + 8;
        long pos = dataStart;
        for (int i = 0; i < samples.Count; i++) { offsets[i] = pos; pos += samples[i].Length; }
        if (pos > uint.MaxValue) throw new IOException("clip larger than 4 GB");
        moov = BuildMoov(video, audio, samples, vIdx, aIdx, t0, offsets);

        output.Write(ftyp);
        output.Write(moov);
        var hdr = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(hdr, (uint)(pos - dataStart + 8));
        "mdat"u8.CopyTo(hdr.AsSpan(4));
        output.Write(hdr);
        for (int i = 0; i < samples.Count; i++) writeSample(i, output);
    }

    static byte[] BuildMoov(AvcInfo v, AacInfo? a, IReadOnlyList<Mp4Sample> s, List<int> vIdx, List<int> aIdx, double t0, long[] offsets)
    {
        var vFirst = s[vIdx[0]].DtsSec;
        var vTicks = Ticks(s, vIdx, VideoTimescale, vFirst, out var vDur);
        var vDelayMs = (long)Math.Round(Math.Max(0, vFirst - t0) * MovieTimescale);
        var vTrimTicks = (long)Math.Round(Math.Max(0, t0 - vFirst) * VideoTimescale);
        long aDelayMs = 0;
        long[]? aTicks = null; long aDur = 0;
        if (a != null && aIdx.Count > 0)
        {
            var aStart = s[aIdx[0]].DtsSec;
            aDelayMs = (long)Math.Round(Math.Max(0, aStart - t0) * MovieTimescale);
            aTicks = Ticks(s, aIdx, a.SampleRate, aStart, out aDur);
        }
        var vMovieDur = vDelayMs + (long)Math.Round(Math.Max(0, vDur - vTrimTicks) * MovieTimescale / (double)VideoTimescale);
        var aMovieDur = aTicks == null ? 0 : aDelayMs + (long)Math.Round(aDur * MovieTimescale / (double)a!.SampleRate);
        var movieDur = Math.Max(vMovieDur, aMovieDur);

        return Box("moov", w =>
        {
            w.Raw(Box("mvhd", m =>
            {
                m.U32(0); m.U32(0); m.U32(0); m.U32(MovieTimescale); m.U32((uint)movieDur);
                m.U32(0x00010000); m.U16(0x0100); m.U16(0); m.U32(0); m.U32(0);
                m.Matrix(); for (int i = 0; i < 6; i++) m.U32(0);
                m.U32(aTicks == null ? 2u : 3u);
            }));
            w.Raw(VideoTrak(v, s, vIdx, vTicks, vDur, vMovieDur, offsets, vDelayMs, vTrimTicks));
            if (aTicks != null) w.Raw(AudioTrak(a!, s, aIdx, aTicks, aDur, aMovieDur, aDelayMs, offsets));
        });
    }

    /// <summary>Per-sample decode times in track ticks (relative to <paramref name="origin"/>), plus the track length. Cumulative rounding keeps long clips drift-free.</summary>
    static long[] Ticks(IReadOnlyList<Mp4Sample> s, List<int> idx, int scale, double origin, out long duration)
    {
        var t = new long[idx.Count];
        for (int i = 0; i < idx.Count; i++) t[i] = (long)Math.Round((s[idx[i]].DtsSec - origin) * scale);
        long last = idx.Count > 1 ? t[^1] - t[^2] : scale / 30;
        duration = t[^1] + Math.Max(1, last);
        return t;
    }

    static byte[] VideoTrak(AvcInfo v, IReadOnlyList<Mp4Sample> s, List<int> idx, long[] ticks, long duration, long movieDur, long[] offsets, long delayMs, long trimTicks)
        => Box("trak", w =>
        {
            w.Raw(Tkhd(1, movieDur, v.Width, v.Height, 0));
            if (delayMs > 0 || trimTicks > 0)
                w.Raw(Box("edts", e => e.Raw(Box("elst", l =>
                {
                    var shownMs = (long)Math.Round(Math.Max(0, duration - trimTicks) * MovieTimescale / (double)VideoTimescale);
                    l.U32(0); l.U32(delayMs > 0 ? 2u : 1u);
                    if (delayMs > 0) { l.U32((uint)delayMs); l.U32(0xFFFFFFFF); l.U32(0x00010000); }
                    l.U32((uint)shownMs); l.U32((uint)trimTicks); l.U32(0x00010000);
                }))));
            w.Raw(Box("mdia", m =>
            {
                m.Raw(Mdhd(VideoTimescale, duration));
                m.Raw(Hdlr("vide", "VideoHandler"));
                m.Raw(Box("minf", f =>
                {
                    f.Raw(Box("vmhd", b => { b.U32(1); b.U32(0); b.U32(0); }));
                    f.Raw(Dinf());
                    f.Raw(Box("stbl", b =>
                    {
                        b.Raw(Box("stsd", d =>
                        {
                            d.U32(0); d.U32(1);
                            d.Raw(Box("avc1", e =>
                            {
                                e.Zero(6); e.U16(1); e.U16(0); e.U16(0); e.Zero(12);
                                e.U16((ushort)v.Width); e.U16((ushort)v.Height);
                                e.U32(0x00480000); e.U32(0x00480000); e.U32(0); e.U16(1);
                                e.Zero(32); e.U16(0x18); e.U16(0xFFFF);
                                e.Raw(Box("avcC", c =>
                                {
                                    c.U8(1); c.U8(v.Sps[1]); c.U8(v.Sps[2]); c.U8(v.Sps[3]); c.U8(0xFF);
                                    c.U8(0xE1); c.U16((ushort)v.Sps.Length); c.Raw(v.Sps);
                                    c.U8(1); c.U16((ushort)v.Pps.Length); c.Raw(v.Pps);
                                }));
                            }));
                        }));
                        b.Raw(Stts(ticks, duration));
                        var hasReorder = idx.Any(i => Math.Abs(s[i].PtsSec - s[i].DtsSec) > 1e-6);
                        if (hasReorder) b.Raw(Ctts(s, idx));
                        b.Raw(Box("stss", k =>
                        {
                            var keys = Enumerable.Range(0, idx.Count).Where(i => s[idx[i]].Keyframe).Select(i => (uint)(i + 1)).ToList();
                            k.U32(0); k.U32((uint)keys.Count); foreach (var x in keys) k.U32(x);
                        }));
                        b.Raw(SampleTables(s, idx, offsets));
                    }));
                }));
            }));
        });

    static byte[] AudioTrak(AacInfo a, IReadOnlyList<Mp4Sample> s, List<int> idx, long[] ticks, long duration, long movieDur, long delayMs, long[] offsets)
        => Box("trak", w =>
        {
            w.Raw(Tkhd(2, movieDur, 0, 0, 0x0100));
            w.Raw(Box("edts", e => e.Raw(Box("elst", l =>
            {
                var mediaMs = (long)Math.Round(duration * MovieTimescale / (double)a.SampleRate);
                l.U32(0); l.U32(delayMs > 0 ? 2u : 1u);
                if (delayMs > 0) { l.U32((uint)delayMs); l.U32(0xFFFFFFFF); l.U32(0x00010000); }
                l.U32((uint)mediaMs); l.U32(0); l.U32(0x00010000);
            }))));
            w.Raw(Box("mdia", m =>
            {
                m.Raw(Mdhd(a.SampleRate, duration));
                m.Raw(Hdlr("soun", "SoundHandler"));
                m.Raw(Box("minf", f =>
                {
                    f.Raw(Box("smhd", b => { b.U32(0); b.U16(0); b.U16(0); }));
                    f.Raw(Dinf());
                    f.Raw(Box("stbl", b =>
                    {
                        b.Raw(Box("stsd", d =>
                        {
                            d.U32(0); d.U32(1);
                            d.Raw(Box("mp4a", e =>
                            {
                                e.Zero(6); e.U16(1); e.U32(0); e.U32(0);
                                e.U16((ushort)a.Channels); e.U16(16); e.U16(0); e.U16(0); e.U32((uint)a.SampleRate << 16);
                                e.Raw(Box("esds", x =>
                                {
                                    x.U32(0);
                                    var asc = a.AudioSpecificConfig;
                                    // DecoderConfigDescriptor: objectTypeIndication 0x40 (AAC), streamType 0x15 (audio), bufferSizeDB(3), maxBitrate(4), avgBitrate(4), then the ASC
                                    var body = new List<byte> { 0x40, 0x15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x05, (byte)asc.Length };
                                    body.AddRange(asc);
                                    var es = new List<byte> { 0, 2, 0, 0x04, (byte)body.Count }; // ES_ID = 2, flags 0, then DecoderConfigDescriptor
                                    es.AddRange(body);
                                    es.AddRange([0x06, 0x01, 0x02]);                              // SLConfigDescriptor (MP4)
                                    x.U8(0x03); x.U8((byte)es.Count); x.Raw(es.ToArray());
                                }));
                            }));
                        }));
                        b.Raw(Stts(ticks, duration));
                        b.Raw(SampleTables(s, idx, offsets));
                    }));
                }));
            }));
        });

    /// <summary>stsc (one sample per chunk), stsz and stco for the samples of one track.</summary>
    static byte[] SampleTables(IReadOnlyList<Mp4Sample> s, List<int> idx, long[] offsets)
    {
        using var ms = new MemoryStream();
        ms.Write(Box("stsc", w => { w.U32(0); w.U32(1); w.U32(1); w.U32(1); w.U32(1); }));
        ms.Write(Box("stsz", w => { w.U32(0); w.U32(0); w.U32((uint)idx.Count); foreach (var i in idx) w.U32((uint)s[i].Length); }));
        ms.Write(Box("stco", w => { w.U32(0); w.U32((uint)idx.Count); foreach (var i in idx) w.U32((uint)offsets[i]); }));
        return ms.ToArray();
    }

    static byte[] Stts(long[] ticks, long duration)
    {
        var runs = new List<(uint Count, uint Delta)>();
        for (int i = 0; i < ticks.Length; i++)
        {
            var d = (uint)Math.Max(1, (i + 1 < ticks.Length ? ticks[i + 1] : duration) - ticks[i]);
            if (runs.Count > 0 && runs[^1].Delta == d) runs[^1] = (runs[^1].Count + 1, d);
            else runs.Add((1, d));
        }
        return Box("stts", w => { w.U32(0); w.U32((uint)runs.Count); foreach (var (c, d) in runs) { w.U32(c); w.U32(d); } });
    }

    static byte[] Ctts(IReadOnlyList<Mp4Sample> s, List<int> idx)
        => Box("ctts", w =>
        {
            w.U32(0x01000000); w.U32((uint)idx.Count);
            foreach (var i in idx) { w.U32(1); w.U32((uint)(int)Math.Round((s[i].PtsSec - s[i].DtsSec) * VideoTimescale)); }
        });

    static byte[] Tkhd(uint id, long durationMovie, int width, int height, ushort volume)
        => Box("tkhd", w =>
        {
            w.U32(0x00000007); w.U32(0); w.U32(0); w.U32(id); w.U32(0); w.U32((uint)durationMovie);
            w.U32(0); w.U32(0); w.U16(0); w.U16(0); w.U16(volume); w.U16(0); w.Matrix();
            w.U32((uint)width << 16); w.U32((uint)height << 16);
        });

    static byte[] Mdhd(int timescale, long duration)
        => Box("mdhd", w => { w.U32(0); w.U32(0); w.U32(0); w.U32((uint)timescale); w.U32((uint)duration); w.U16(0x55C4); w.U16(0); });

    static byte[] Hdlr(string type, string name)
        => Box("hdlr", w => { w.U32(0); w.U32(0); w.Ascii(type); w.U32(0); w.U32(0); w.U32(0); w.Ascii(name); w.U8(0); });

    static byte[] Dinf()
        => Box("dinf", w => w.Raw(Box("dref", d => { d.U32(0); d.U32(1); d.Raw(Box("url ", u => u.U32(1))); })));

    static byte[] Box(string type, Action<BoxWriter> body)
    {
        var w = new BoxWriter();
        body(w);
        var payload = w.ToArray();
        var b = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        payload.CopyTo(b, 8);
        return b;
    }

    sealed class BoxWriter
    {
        readonly MemoryStream _ms = new();
        public byte[] ToArray() => _ms.ToArray();
        public void U8(byte v) => _ms.WriteByte(v);
        public void U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, v); _ms.Write(b); }
        public void U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); _ms.Write(b); }
        public void Ascii(string s) => _ms.Write(System.Text.Encoding.ASCII.GetBytes(s));
        public void Zero(int n) => _ms.Write(new byte[n]);
        public void Raw(byte[] b) => _ms.Write(b);
        public void Matrix() { foreach (var v in new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 }) U32(v); }
    }
}
