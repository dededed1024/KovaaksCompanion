namespace KovaaksCompanion.Core.Perf;

/// <summary>One ~1 s bucket of a run. <see cref="TSec"/> is the bucket's end, seconds after challenge start.</summary>
public readonly record struct PerfBucket(double TSec, int Shots, int Hits, int Misses, int Kills, double DamageDone, double DamagePossible, double Score, int Reloads = 0, double Distance = 0);

/// <summary>Where the run was played: the map or environment file, the bot files (one per slot) and the profile name.</summary>
public sealed record PerfHeader(string Map, IReadOnlyList<string> Bots, string Profile);

public sealed record PerfData(IReadOnlyList<PerfBucket> Buckets, PerfHeader? Header = null)
{
    public int TotalShots => Buckets.Sum(b => b.Shots);
    public int TotalHits => Buckets.Sum(b => b.Hits);
    public int TotalReloads => Buckets.Sum(b => b.Reloads);
    public double TotalDistance => Buckets.Sum(b => b.Distance);
}

public sealed class PerfFormatException(string message) : Exception(message);

/// <summary>
/// Reads KovaaK's "&lt;run&gt; Performance.perf" (protobuf, no published schema). Top-level field 2 repeats; each record
/// has the bucket time in field 1 and one metric: 2/3/4/5/6/7/8 = shots/hits/misses/damage done/damage possible/score/kills,
/// 12/14 = reloads/distance moved (10 = overshots at a kill, not read), a submessage whose field 1 is the value. Records with the same time form one bucket; zero-valued metrics are omitted.
/// </summary>
public static class PerfParser
{
    const string Suffix = " Stats.csv";

    /// <summary>Path of the .perf that belongs to a stats CSV: sibling "performances" folder, same stem.</summary>
    public static string PathFor(string statsCsvPath)
    {
        var name = Path.GetFileName(statsCsvPath);
        var stem = name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase) ? name[..^Suffix.Length] : Path.GetFileNameWithoutExtension(name);
        var statsDir = Path.GetDirectoryName(Path.GetFullPath(statsCsvPath))!;
        return Path.Combine(Path.GetDirectoryName(statsDir) ?? statsDir, "performances", stem + " Performance.perf");
    }

    public static PerfData Parse(byte[] data)
    {
        var buckets = new List<PerfBucket>();
        PerfHeader? header = null;
        try
        {
            var pos = 0;
            while (pos < data.Length)
            {
                var (field, wire) = Tag(data, ref pos);
                if (field == 2 && wire == 2) Merge(buckets, Bucket(Bytes(data, ref pos)));
                else if (field == 1 && wire == 2) header = Header(Bytes(data, ref pos));
                else Skip(data, ref pos, wire);
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new PerfFormatException("Truncated or unsupported .perf file");
        }
        return new PerfData(buckets, header);
    }

    /// <summary>Field 5 of the header holds the setup: 2 = profile, 3 (repeated) = bot files, 8 = map. The other figures in it are not read.</summary>
    static PerfHeader? Header(byte[] msg)
    {
        var pos = 0;
        while (pos < msg.Length)
        {
            var (field, wire) = Tag(msg, ref pos);
            if (field != 5 || wire != 2) { Skip(msg, ref pos, wire); continue; }
            string map = "", profile = "";
            var bots = new List<string>();
            var cfg = Bytes(msg, ref pos);
            var p = 0;
            while (p < cfg.Length)
            {
                var (f, w) = Tag(cfg, ref p);
                if (w != 2) { Skip(cfg, ref p, w); continue; }
                var s = System.Text.Encoding.UTF8.GetString(Bytes(cfg, ref p));
                if (f == 2) profile = s; else if (f == 3) bots.Add(s); else if (f == 8) map = s;
            }
            return new PerfHeader(map, bots, profile);
        }
        return null;
    }

    static void Merge(List<PerfBucket> buckets, PerfBucket b)
    {
        var i = buckets.Count - 1;
        while (i >= 0 && buckets[i].TSec != b.TSec) i--;
        if (i < 0) { buckets.Add(b); return; }
        var a = buckets[i];
        buckets[i] = new PerfBucket(a.TSec, a.Shots + b.Shots, a.Hits + b.Hits, a.Misses + b.Misses, a.Kills + b.Kills,
            a.DamageDone + b.DamageDone, a.DamagePossible + b.DamagePossible, a.Score + b.Score, a.Reloads + b.Reloads, a.Distance + b.Distance);
    }

    static PerfBucket Bucket(byte[] msg)
    {
        double t = 0, dmg = 0, dmgMax = 0, score = 0;
        int shots = 0, hits = 0, misses = 0, kills = 0, reloads = 0;
        double distance = 0;
        var pos = 0;
        while (pos < msg.Length)
        {
            var (field, wire) = Tag(msg, ref pos);
            if (field == 1 && wire == 5) { t = Float(msg, ref pos); continue; }
            if (wire != 2) { Skip(msg, ref pos, wire); continue; }
            var (isFloat, value) = Value(Bytes(msg, ref pos));
            _ = isFloat;
            switch (field)
            {
                case 2: shots = (int)value; break;
                case 3: hits = (int)value; break;
                case 4: misses = (int)value; break;
                case 5: dmg = value; break;
                case 6: dmgMax = value; break;
                case 7: score = value; break;
                case 8: kills = (int)value; break;
                case 12: reloads = (int)value; break;
                case 14: distance = value; break;
            }
        }
        return new PerfBucket(t, shots, hits, misses, kills, dmg, dmgMax, score, reloads, distance);
    }

    /// <summary>Submessage holding one number in field 1 (varint or float).</summary>
    static (bool IsFloat, double Value) Value(byte[] msg)
    {
        var pos = 0;
        while (pos < msg.Length)
        {
            var (field, wire) = Tag(msg, ref pos);
            if (field == 1 && wire == 0) return (false, Varint(msg, ref pos));
            if (field == 1 && wire == 5) return (true, Float(msg, ref pos));
            Skip(msg, ref pos, wire);
        }
        return (false, 0);
    }

    static (int Field, int Wire) Tag(byte[] b, ref int pos)
    {
        var k = Varint(b, ref pos);
        return ((int)(k >> 3), (int)(k & 7));
    }

    static ulong Varint(byte[] b, ref int pos)
    {
        ulong r = 0;
        for (var shift = 0; ; shift += 7)
        {
            if (shift > 63) throw new OverflowException();
            var c = b[pos++];
            r |= (ulong)(c & 0x7F) << shift;
            if (c < 0x80) return r;
        }
    }

    static float Float(byte[] b, ref int pos)
    {
        var v = BitConverter.ToSingle(b, pos);
        pos += 4;
        return v;
    }

    static byte[] Bytes(byte[] b, ref int pos)
    {
        var n = checked((int)Varint(b, ref pos));
        if (n < 0 || pos + n > b.Length) throw new ArgumentOutOfRangeException(nameof(b));
        var r = b[pos..(pos + n)];
        pos += n;
        return r;
    }

    static void Skip(byte[] b, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: Varint(b, ref pos); break;
            case 1: pos += 8; break;
            case 2: Bytes(b, ref pos); break;
            case 5: pos += 4; break;
            default: throw new PerfFormatException($"Unsupported wire type {wire}");
        }
        if (pos > b.Length) throw new ArgumentOutOfRangeException(nameof(b));
    }
}
