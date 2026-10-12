namespace KovaaksCompanion.Core.Video;

/// <summary>Result of a clip extraction. Clip time 0 corresponds to <see cref="ClipStartUtc"/> / <see cref="ClipStartQpc"/>.</summary>
public sealed record ClipResult(string Path, DateTime ClipStartUtc, long ClipStartQpc, TimeSpan Duration)
{
    /// <summary>Position inside the clip of a wall-clock instant.</summary>
    public TimeSpan ToClipTime(DateTime utc) => utc.ToUniversalTime() - ClipStartUtc;
    public DateTime ToUtc(TimeSpan clipTime) => ClipStartUtc + clipTime;
}

/// <summary>QPC (Stopwatch ticks) &lt;-&gt; UTC conversion from one paired sample taken at construction.</summary>
public sealed class QpcClock
{
    private readonly long _qpc0 = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly DateTime _utc0 = DateTime.UtcNow;
    public DateTime ToUtc(long qpc) => _utc0 + TimeSpan.FromSeconds((double)(qpc - _qpc0) / System.Diagnostics.Stopwatch.Frequency);
    public long ToQpc(DateTime utc) => _qpc0 + (long)((utc.ToUniversalTime() - _utc0).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
}
