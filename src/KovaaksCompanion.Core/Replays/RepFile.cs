using System.IO.Compression;
using System.Text;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Replays;

/// <summary>What a replay keeps besides its info and video: the mouse trajectory, KovaaK's .perf, and the run statistics.</summary>
public sealed record RepParts(byte[]? Trajectory, byte[]? Perf, RepStats? Stats);

/// <summary>
/// The <c>.rep</c> file: <see cref="RepParts"/> in one file. "KREP", a version byte, then a deflate stream of three sections
/// (trajectory, perf, statistics), each an int length (-1 = absent) followed by its bytes.
/// </summary>
public static class RepFile
{
    public const string Extension = ".rep";
    static readonly byte[] Magic = "KREP"u8.ToArray();
    const byte Version = 2;

    public static byte[] Pack(RepParts p)
    {
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(Version);
        using (var z = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        using (var w = new BinaryWriter(z, Encoding.UTF8, leaveOpen: true))
        {
            Section(w, p.Trajectory);
            Section(w, p.Perf);
            Section(w, p.Stats is null ? null : PackStats(p.Stats));
        }
        return ms.ToArray();
    }

    static byte[] PackStats(RepStats s)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            var st = s.Settings;
            w.Write(st.SensScale); w.Write(st.HorizSens); w.Write(st.VertSens); w.Write(st.Dpi); w.Write(st.Fov); w.Write(st.FovScale); w.Write(st.Resolution);
            w.Write(s.Kills); w.Write(s.HitCount); w.Write(s.MissCount);
            w.Write(s.KillList.Count);
            foreach (var k in s.KillList) { w.Write(k.TimeMs); w.Write(k.TtkSec); w.Write(k.Shots); w.Write(k.Hits); w.Write(k.OverShots); }
        }
        return ms.ToArray();
    }

    static RepStats UnpackStats(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var settings = new InputSettings { SensScale = r.ReadString(), HorizSens = r.ReadDouble(), VertSens = r.ReadDouble(), Dpi = r.ReadInt32(), Fov = r.ReadDouble(), FovScale = r.ReadString(), Resolution = r.ReadString() };
        int kills = r.ReadInt32(), hits = r.ReadInt32(), misses = r.ReadInt32();
        var list = new RepKill[r.ReadInt32()];
        for (var i = 0; i < list.Length; i++) list[i] = new(r.ReadInt32(), r.ReadDouble(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        return new(settings, kills, hits, misses, list);
    }

    static void Section(BinaryWriter w, byte[]? data)
    {
        w.Write(data?.Length ?? -1);
        if (data != null) w.Write(data);
    }

    /// <summary>Throws <see cref="ReplayFormatException"/> when the bytes are not a .rep file of this version.</summary>
    public static RepParts Unpack(byte[] data)
    {
        if (data.Length < Magic.Length + 1 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic) || data[Magic.Length] != Version)
            throw new ReplayFormatException("The replay data file is damaged or of an unsupported version.");
        try
        {
            using var z = new DeflateStream(new MemoryStream(data, Magic.Length + 1, data.Length - Magic.Length - 1), CompressionMode.Decompress);
            using var r = new BinaryReader(z, Encoding.UTF8);
            var traj = Section(r);
            var perf = Section(r);
            var stats = Section(r);
            return new(traj, perf, stats is null ? null : UnpackStats(stats));
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException)
        {
            throw new ReplayFormatException("The replay data file is damaged.");
        }
    }

    static byte[]? Section(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n < 0) return null;
        var b = r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return b;
    }
}
