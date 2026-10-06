namespace KovaaksCompanion.Core.Trajectory;

/// <summary>Camera orientation at <see cref="TSec"/> seconds after run start. Yaw is cumulative (not wrapped), right = +; pitch up = +, clamped to +-90.</summary>
public readonly record struct CameraSample(float TSec, float YawDeg, float PitchDeg);

public enum TrajectoryEventType : byte { Fire = 0, Release = 1, Kill = 2 }

/// <summary>Button: 0 left, 1 right (0 for kills).</summary>
public readonly record struct TrajectoryEvent(float TSec, TrajectoryEventType Type, byte Button = 0);

public sealed record TrajectoryData(double SampleRateHz, IReadOnlyList<CameraSample> Samples, IReadOnlyList<TrajectoryEvent> Events);
