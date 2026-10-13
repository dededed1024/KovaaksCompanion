using System.Text;

namespace KovaaksCompanion.Core.Trajectory;

public sealed class TrajectoryFormatException(string message) : Exception(message);

/// <summary>
/// trajectory.bin, little endian: "KCTJ", u16 version, then
/// version 1: f32 rateHz, i32 sampleCount, i32 eventCount, samples (f32 t, yaw, pitch = 12 B each), events (f32 t, u8 type, u8 button = 6 B each);
/// version 2: f64 yawPerCount, f64 pitchPerCount, i32 moveCount, i32 eventCount, moves (varint microseconds since the previous move, zigzag varint dx, dy),
/// events as in version 1. The camera samples are integrated from the moves on load. 60 s of play is a few hundred KB.
/// </summary>
public static class TrajectorySerializer
{
    public const ushort CurrentVersion = 2;
    static readonly byte[] Magic = "KCTJ"u8.ToArray();

    public static byte[] Serialize(TrajectoryData data)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            if (data.Raw is { } raw)
            {
                w.Write(CurrentVersion);
                w.Write(raw.YawPerCount);
                w.Write(raw.PitchPerCount);
                w.Write(raw.Moves.Count);
                w.Write(data.Events.Count);
                var prev = 0;
                foreach (var m in raw.Moves)
                {
                    Varint(w, (uint)(m.TUs - prev));
                    Varint(w, Zigzag(m.Dx));
                    Varint(w, Zigzag(m.Dy));
                    prev = m.TUs;
                }
            }
            else
            {
                w.Write((ushort)1);
                w.Write((float)data.SampleRateHz);
                w.Write(data.Samples.Count);
                w.Write(data.Events.Count);
                foreach (var s in data.Samples) { w.Write(s.TSec); w.Write(s.YawDeg); w.Write(s.PitchDeg); }
            }
            foreach (var e in data.Events) { w.Write(e.TSec); w.Write((byte)e.Type); w.Write(e.Button); }
        }
        return ms.ToArray();
    }

    public static TrajectoryData Deserialize(byte[] bytes)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(bytes));
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new TrajectoryFormatException("Not a trajectory file (bad magic).");
            var version = r.ReadUInt16();
            if (version is not (1 or 2))
                throw new TrajectoryFormatException($"Unsupported trajectory format version {version} (this app reads versions 1 and {CurrentVersion}).");
            if (version == 1) return ReadV1(r, bytes.Length);

            var yawPer = r.ReadDouble();
            var pitchPer = r.ReadDouble();
            var mc = r.ReadInt32();
            var ec = r.ReadInt32();
            if (mc < 0 || ec < 0 || (long)mc * 3 + (long)ec * 6 > bytes.Length) throw new TrajectoryFormatException("Corrupt trajectory file (bad counts).");
            var moves = new MouseMove[mc];
            var t = 0;
            for (var i = 0; i < mc; i++)
            {
                t += (int)ReadVarint(r);
                moves[i] = new MouseMove(t, Unzigzag(ReadVarint(r)), Unzigzag(ReadVarint(r)));
            }
            return TrajectoryData.FromRaw(new RawTrack(yawPer, pitchPer, moves), ReadEvents(r, ec));
        }
        catch (EndOfStreamException) { throw new TrajectoryFormatException("Truncated trajectory file."); }
    }

    static TrajectoryData ReadV1(BinaryReader r, int length)
    {
        var rate = r.ReadSingle();
        var sc = r.ReadInt32();
        var ec = r.ReadInt32();
        if (sc < 0 || ec < 0 || (long)sc * 12 + (long)ec * 6 > length) throw new TrajectoryFormatException("Corrupt trajectory file (bad counts).");
        var samples = new CameraSample[sc];
        for (var i = 0; i < sc; i++) samples[i] = new CameraSample(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new TrajectoryData(rate, samples, ReadEvents(r, ec));
    }

    static TrajectoryEvent[] ReadEvents(BinaryReader r, int count)
    {
        var events = new TrajectoryEvent[count];
        for (var i = 0; i < count; i++) events[i] = new TrajectoryEvent(r.ReadSingle(), (TrajectoryEventType)r.ReadByte(), r.ReadByte());
        return events;
    }

    static uint Zigzag(int v) => (uint)((v << 1) ^ (v >> 31));

    static int Unzigzag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    static void Varint(BinaryWriter w, uint v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    static uint ReadVarint(BinaryReader r)
    {
        uint v = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var b = r.ReadByte();
            v |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return v;
        }
        throw new TrajectoryFormatException("Corrupt trajectory file (bad number).");
    }
}
