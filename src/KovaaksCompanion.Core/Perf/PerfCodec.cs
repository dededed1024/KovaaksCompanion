using System.Text;

namespace KovaaksCompanion.Core.Perf;

/// <summary>
/// The compact form a replay keeps of its <see cref="PerfData"/>: the environment strings, then per bucket the time as milliseconds after the previous bucket and only the figures that are not zero.
/// Whole numbers are written as varints, others as 32-bit floats (the .perf holds nothing more precise).
/// </summary>
public static class PerfCodec
{
    // bucket flags: bit n set = figure n follows; bits 10..12 = damage done, damage possible, score are floats, not whole numbers
    const int Shots = 1, Hits = 2, Misses = 4, Kills = 8, DamageDone = 16, DamagePossible = 32, Score = 64, Reloads = 256, Distance = 512, FloatBase = 10;

    public static byte[] Write(PerfData perf)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        var h = perf.Header;
        w.Write(h is not null);
        if (h is not null)
        {
            w.Write(h.Map); w.Write(h.Profile); w.Write7BitEncodedInt(h.Bots.Count);
            foreach (var b in h.Bots) w.Write(b);
        }
        w.Write7BitEncodedInt(perf.Buckets.Count);
        var prevMs = 0L;
        foreach (var b in perf.Buckets)
        {
            var t = (long)Math.Round(b.TSec * 1000);
            w.Write7BitEncodedInt64(Zig(t - prevMs)); prevMs = t;
            var flags = 0;
            if (b.Shots != 0) flags |= Shots;
            if (b.Hits != 0) flags |= Hits;
            if (b.Misses != 0) flags |= Misses;
            if (b.Kills != 0) flags |= Kills;
            if (b.DamageDone != 0) flags |= DamageDone;
            if (b.DamagePossible != 0) flags |= DamagePossible;
            if (b.Score != 0) flags |= Score;
            if (b.Reloads != 0) flags |= Reloads;
            if (b.Distance != 0) flags |= Distance;
            if (IsFraction(b.DamageDone)) flags |= 1 << FloatBase;
            if (IsFraction(b.DamagePossible)) flags |= 1 << (FloatBase + 1);
            if (IsFraction(b.Score)) flags |= 1 << (FloatBase + 2);
            w.Write7BitEncodedInt(flags);
            if ((flags & Shots) != 0) w.Write7BitEncodedInt64(Zig(b.Shots));
            if ((flags & Hits) != 0) w.Write7BitEncodedInt64(Zig(b.Hits));
            if ((flags & Misses) != 0) w.Write7BitEncodedInt64(Zig(b.Misses));
            if ((flags & Kills) != 0) w.Write7BitEncodedInt64(Zig(b.Kills));
            Number(w, b.DamageDone, flags, DamageDone, FloatBase);
            Number(w, b.DamagePossible, flags, DamagePossible, FloatBase + 1);
            Number(w, b.Score, flags, Score, FloatBase + 2);
            if ((flags & Reloads) != 0) w.Write7BitEncodedInt64(Zig(b.Reloads));
            if ((flags & Distance) != 0) w.Write((float)b.Distance);
        }
        return ms.ToArray();
    }

    /// <summary>Throws <see cref="PerfFormatException"/> when the bytes are cut short.</summary>
    public static PerfData Read(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            PerfHeader? header = null;
            if (r.ReadBoolean())
            {
                string map = r.ReadString(), profile = r.ReadString();
                var bots = new string[r.Read7BitEncodedInt()];
                for (var i = 0; i < bots.Length; i++) bots[i] = r.ReadString();
                header = new PerfHeader(map, bots, profile);
            }
            var buckets = new PerfBucket[r.Read7BitEncodedInt()];
            var t = 0L;
            for (var i = 0; i < buckets.Length; i++)
            {
                t += Unzig(r.Read7BitEncodedInt64());
                var flags = r.Read7BitEncodedInt();
                int Int(int bit) => (flags & bit) != 0 ? (int)Unzig(r.Read7BitEncodedInt64()) : 0;
                int shots = Int(Shots), hits = Int(Hits), misses = Int(Misses), kills = Int(Kills);
                double dmg = Number(r, flags, DamageDone, FloatBase), possible = Number(r, flags, DamagePossible, FloatBase + 1), score = Number(r, flags, Score, FloatBase + 2);
                int reloads = Int(Reloads);
                var distance = (flags & Distance) != 0 ? r.ReadSingle() : 0;
                buckets[i] = new PerfBucket(t / 1000.0, shots, hits, misses, kills, dmg, possible, score, reloads, distance);
            }
            return new PerfData(buckets, header);
        }
        catch (Exception e) when (e is EndOfStreamException or OverflowException or ArgumentException or OutOfMemoryException or FormatException)
        {
            throw new PerfFormatException("Truncated per-second data");
        }
    }

    static bool IsFraction(double v) => v != Math.Floor(v) || Math.Abs(v) > 1e9;

    static void Number(BinaryWriter w, double v, int flags, int present, int floatBit)
    {
        if ((flags & present) == 0) return;
        if ((flags & (1 << floatBit)) != 0) w.Write((float)v);
        else w.Write7BitEncodedInt64(Zig((long)v));
    }

    static double Number(BinaryReader r, int flags, int present, int floatBit)
    {
        if ((flags & present) == 0) return 0;
        return (flags & (1 << floatBit)) != 0 ? r.ReadSingle() : Unzig(r.Read7BitEncodedInt64());
    }

    static long Zig(long v) => (v << 1) ^ (v >> 63);
    static long Unzig(long v) => (long)((ulong)v >> 1) ^ -(v & 1);
}
