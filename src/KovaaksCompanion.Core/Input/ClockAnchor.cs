using System.Diagnostics;

namespace KovaaksCompanion.Core.Input;

/// <summary>Pairs a QPC reading with the wall clock so CSV wall-clock times map to QPC and back.</summary>
public readonly record struct ClockAnchor(long Qpc, DateTime Wall, long Frequency)
{
    public static ClockAnchor Now() => new(Stopwatch.GetTimestamp(), DateTime.Now, Stopwatch.Frequency);

    public long ToQpc(DateTime wall) => Qpc + (long)Math.Round((wall - Wall).TotalSeconds * Frequency);

    public DateTime ToWall(long qpc) => Wall + TimeSpan.FromSeconds((double)(qpc - Qpc) / Frequency);
}
