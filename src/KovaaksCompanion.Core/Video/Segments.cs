namespace KovaaksCompanion.Core.Video;

/// <summary>Result of a clip extraction. Clip time 0 is <see cref="ClipStartQpc"/> (Stopwatch ticks, the clock every timeline of a session is kept on).</summary>
public sealed record ClipResult(string Path, long ClipStartQpc, TimeSpan Duration);
