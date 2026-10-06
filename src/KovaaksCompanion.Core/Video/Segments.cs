namespace KovaaksCompanion.Core.Video;

/// <summary>A finished ring-buffer segment: file name plus its start on the wall (UTC) clock.</summary>
public sealed record SegmentInfo(string File, DateTime StartUtc, TimeSpan Duration)
{
    public DateTime EndUtc => StartUtc + Duration;
}

/// <summary>Result of a clip extraction. Clip time 0 corresponds to <see cref="ClipStartUtc"/> / <see cref="ClipStartQpc"/>.</summary>
public sealed record ClipResult(string Path, DateTime ClipStartUtc, long ClipStartQpc, TimeSpan Duration)
{
    /// <summary>Position inside the clip of a wall-clock instant.</summary>
    public TimeSpan ToClipTime(DateTime utc) => utc.ToUniversalTime() - ClipStartUtc;
    public DateTime ToUtc(TimeSpan clipTime) => ClipStartUtc + clipTime;
}

public static class SegmentSelector
{
    /// <summary>Contiguous-or-not segments overlapping [start - margin, end + margin], oldest first.</summary>
    public static List<SegmentInfo> Select(IEnumerable<SegmentInfo> segments, DateTime startUtc, DateTime endUtc, TimeSpan margin)
    {
        var from = startUtc.ToUniversalTime() - margin;
        var to = endUtc.ToUniversalTime() + margin;
        return segments.Where(s => s.EndUtc > from && s.StartUtc < to).OrderBy(s => s.StartUtc).ToList();
    }

    /// <summary>True when the selection reaches past the requested window on both sides (or the window is fully buffered).</summary>
    public static bool Covers(IReadOnlyList<SegmentInfo> selected, DateTime startUtc, DateTime endUtc)
        => selected.Count > 0 && selected[0].StartUtc <= startUtc.ToUniversalTime() && selected[^1].EndUtc >= endUtc.ToUniversalTime();
}

/// <summary>QPC (Stopwatch ticks) &lt;-&gt; UTC conversion from one paired sample taken at construction.</summary>
public sealed class QpcClock
{
    private readonly long _qpc0 = System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly DateTime _utc0 = DateTime.UtcNow;
    public DateTime ToUtc(long qpc) => _utc0 + TimeSpan.FromSeconds((double)(qpc - _qpc0) / System.Diagnostics.Stopwatch.Frequency);
    public long ToQpc(DateTime utc) => _qpc0 + (long)((utc.ToUniversalTime() - _utc0).TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
}

/// <summary>
/// Wall-clock anchor of the ffmpeg stream (stream time 0). Each closed segment gives one estimate:
/// (time its csv line arrived) - (segment end time in stream). Latency is always >= 0, so the minimum
/// over a short window is the best estimate; the window keeps it tracking slow drift.
/// </summary>
public sealed class StreamAnchor
{
    private readonly Queue<DateTime> _estimates = new();
    private readonly int _window;
    public StreamAnchor(int window = 6) => _window = window;

    public bool HasValue => _estimates.Count > 0;
    public DateTime Value => _estimates.Min();

    public void AddSegmentClosed(DateTime arrivalUtc, TimeSpan segmentEndInStream)
    {
        _estimates.Enqueue(arrivalUtc - segmentEndInStream);
        while (_estimates.Count > _window) _estimates.Dequeue();
    }

    public DateTime ToUtc(TimeSpan streamTime) => Value + streamTime;
}
