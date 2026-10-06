namespace KovaaksCompanion.Core.Input;

/// <summary>Thread-safe in-memory history of raw mouse events covering at least <c>keepSeconds</c>. No I/O.</summary>
public sealed class MouseRingBuffer
{
    readonly object _gate = new();
    readonly Queue<RawMouseEvent> _events = new();
    readonly long _keepTicks;

    /// <summary>Everything at or after this QPC is guaranteed to be present (capture start, or the trim cutoff).</summary>
    public long CoverageStartQpc { get; private set; }

    /// <summary>Capture is not running after this QPC (paused). long.MaxValue while running.</summary>
    public long CoverageEndQpc { get; private set; } = long.MaxValue;

    public MouseRingBuffer(double keepSeconds, long frequency, long startQpc)
    {
        _keepTicks = (long)(keepSeconds * frequency);
        CoverageStartQpc = startQpc;
    }

    /// <summary>Capture paused: nothing is recorded from qpc until Resume.</summary>
    public void Pause(long qpc) { lock (_gate) CoverageEndQpc = qpc; }

    /// <summary>Capture resumed at qpc: the gap makes windows overlapping it partial.</summary>
    public void Resume(long qpc) { lock (_gate) { CoverageEndQpc = long.MaxValue; CoverageStartQpc = Math.Max(CoverageStartQpc, qpc); } }

    public void Add(RawMouseEvent e)
    {
        lock (_gate)
        {
            _events.Enqueue(e);
            var cutoff = e.Qpc - _keepTicks;
            while (_events.Count > 0 && _events.Peek().Qpc < cutoff) _events.Dequeue();
            if (cutoff > CoverageStartQpc) CoverageStartQpc = cutoff;
        }
    }

    /// <summary>Events with <c>fromQpc &lt;= Qpc &lt; toQpc</c>, in arrival order.</summary>
    public RawMouseEvent[] Slice(long fromQpc, long toQpc)
    {
        lock (_gate) return _events.Where(e => e.Qpc >= fromQpc && e.Qpc < toQpc).ToArray();
    }

    public int Count { get { lock (_gate) return _events.Count; } }
}
