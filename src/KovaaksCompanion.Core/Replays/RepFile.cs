using System.IO.Compression;
using System.Text;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Replays;

/// <summary>What a replay keeps besides its info and video: the mouse trajectory, the run's per-second data, and the run statistics. What KovaaK's .perf and stats CSV held that the app uses is kept here, in its own compact form.</summary>
public sealed record RepParts(byte[]? Trajectory, PerfData? Perf, RepStats? Stats);

/// <summary>
/// The <c>.rep</c> file: <see cref="RepParts"/> in one file. "KREP", a version byte, then a brotli stream of three sections
/// (trajectory, per-second data, statistics), each an int length (-1 = absent) followed by its bytes.
/// </summary>
public static class RepFile
{
    public const string Extension = ".rep";
    static readonly byte[] Magic = "KREP"u8.ToArray();
    const byte Version = 5;

    public static byte[] Pack(RepParts p)
    {
        using var ms = new MemoryStream();
        ms.Write(Magic);
        ms.WriteByte(Version);
        using (var z = new BrotliStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var w = new BinaryWriter(z, Encoding.UTF8, leaveOpen: true))
        {
            Section(w, p.Trajectory);
            Section(w, p.Perf is null ? null : PerfCodec.Write(p.Perf));
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
            w.Write(s.Reloads.HasValue); w.Write(s.Reloads ?? 0);
            w.Write(s.Distance.HasValue); w.Write(s.Distance ?? 0);
            w.Write(s.Hash); w.Write(s.GameVersion);
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
        bool hasReloads = r.ReadBoolean(); int reloads = r.ReadInt32();
        bool hasDistance = r.ReadBoolean(); double distance = r.ReadDouble();
        string hash = r.ReadString(), gameVersion = r.ReadString();
        return new(settings, kills, hits, misses, list, hasReloads ? reloads : null, hasDistance ? distance : null, hash, gameVersion);
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
            using var z = new BrotliStream(new MemoryStream(data, Magic.Length + 1, data.Length - Magic.Length - 1), CompressionMode.Decompress);
            using var r = new BinaryReader(z, Encoding.UTF8);
            var traj = Section(r);
            var perf = Section(r);
            var stats = Section(r);
            return new(traj, perf is null ? null : ReadPerf(perf), stats is null ? null : UnpackStats(stats));
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or ArgumentException or IOException)
        {
            throw new ReplayFormatException("The replay data file is damaged.");
        }
    }

    /// <summary>A per-second section that cannot be read is left out: the replay still opens without it.</summary>
    static PerfData? ReadPerf(byte[] data)
    {
        try { return PerfCodec.Read(data); }
        catch (PerfFormatException) { return null; }
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
