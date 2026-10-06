using System.Text;

namespace KovaaksCompanion.Core.Trajectory;

public sealed class TrajectoryFormatException(string message) : Exception(message);

/// <summary>
/// trajectory.bin, little endian: "KCTJ", u16 version, f32 rateHz, i32 sampleCount, i32 eventCount,
/// samples (f32 t, yaw, pitch = 12 B each), events (f32 t, u8 type, u8 button = 6 B each).
/// 60 s at 120 Hz is about 86 KB.
/// </summary>
public static class TrajectorySerializer
{
    public const ushort CurrentVersion = 1;
    static readonly byte[] Magic = "KCTJ"u8.ToArray();

    public static byte[] Serialize(TrajectoryData data)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(CurrentVersion);
            w.Write((float)data.SampleRateHz);
            w.Write(data.Samples.Count);
            w.Write(data.Events.Count);
            foreach (var s in data.Samples) { w.Write(s.TSec); w.Write(s.YawDeg); w.Write(s.PitchDeg); }
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
            if (version != CurrentVersion)
                throw new TrajectoryFormatException($"Unsupported trajectory format version {version} (this app reads version {CurrentVersion}).");
            var rate = r.ReadSingle();
            var sc = r.ReadInt32();
            var ec = r.ReadInt32();
            if (sc < 0 || ec < 0 || (long)sc * 12 + (long)ec * 6 > bytes.Length) throw new TrajectoryFormatException("Corrupt trajectory file (bad counts).");
            var samples = new CameraSample[sc];
            for (var i = 0; i < sc; i++) samples[i] = new CameraSample(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var events = new TrajectoryEvent[ec];
            for (var i = 0; i < ec; i++) events[i] = new TrajectoryEvent(r.ReadSingle(), (TrajectoryEventType)r.ReadByte(), r.ReadByte());
            return new TrajectoryData(rate, samples, events);
        }
        catch (EndOfStreamException) { throw new TrajectoryFormatException("Truncated trajectory file."); }
    }
}
