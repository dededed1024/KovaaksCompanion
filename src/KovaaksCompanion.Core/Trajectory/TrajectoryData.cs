namespace KovaaksCompanion.Core.Trajectory;

/// <summary>Camera orientation at <see cref="TSec"/> seconds after run start. Yaw is cumulative (not wrapped), right = +; pitch up = +, clamped to +-90.</summary>
public readonly record struct CameraSample(float TSec, float YawDeg, float PitchDeg);

public enum TrajectoryEventType : byte { Fire = 0, Release = 1, Kill = 2 }

/// <summary>Button: 0 left, 1 right (0 for kills).</summary>
public readonly record struct TrajectoryEvent(float TSec, TrajectoryEventType Type, byte Button = 0);

/// <summary>Mouse counts that arrived within the millisecond ending at <see cref="TUs"/> microseconds after run start.</summary>
public readonly record struct MouseMove(int TUs, int Dx, int Dy);

/// <summary>The recorded mouse movement as counts, with the degrees one count turns the view; the camera samples are integrated from it.</summary>
public sealed record RawTrack(double YawPerCount, double PitchPerCount, IReadOnlyList<MouseMove> Moves);

/// <param name="SampleRateHz">Rate of the fixed-rate samples; 0 when <see cref="Raw"/> is set and the samples follow the mouse's own timing (up to 1 kHz).</param>
public sealed record TrajectoryData(double SampleRateHz, IReadOnlyList<CameraSample> Samples, IReadOnlyList<TrajectoryEvent> Events, RawTrack? Raw = null)
{
    /// <summary>Integrates the raw movement into camera samples. Sample 0 is (0,0) at t=0 (the true start orientation is unknown).</summary>
    public static TrajectoryData FromRaw(RawTrack raw, IReadOnlyList<TrajectoryEvent> events)
    {
        var samples = new CameraSample[raw.Moves.Count + 1];
        double yaw = 0, pitch = 0;
        samples[0] = new CameraSample(0, 0, 0);
        for (var i = 0; i < raw.Moves.Count; i++)
        {
            var m = raw.Moves[i];
            yaw += m.Dx * raw.YawPerCount;
            pitch = Math.Clamp(pitch - m.Dy * raw.PitchPerCount, -90, 90);
            samples[i + 1] = new CameraSample((float)(m.TUs / 1e6), (float)yaw, (float)pitch);
        }
        return new TrajectoryData(0, samples, events, raw);
    }
}
