namespace KovaaksCompanion.Core.Video;

public sealed record VideoOptions
{
    public string FfmpegPath { get; init; } = @"C:\ffmpeg\bin\ffmpeg.exe";
    public string GameProcessName { get; init; } = "FPSAimTrainer-Win64-Shipping";
    /// <summary>Folder holding the rolling segments.</summary>
    public string BufferDir { get; init; } = Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "buffer");
    /// <summary>Frames per second; 0 = match the refresh rate of the monitor showing the game.</summary>
    public int Fps { get; init; } = 0;
    public int SegmentSeconds { get; init; } = 10;
    public int BufferMinutes { get; init; } = 10;
    /// <summary>Constant-quality target (nvenc -cq / amf+qsv quality / x264 crf).</summary>
    public int Quality { get; init; } = 18;
    /// <summary>True = capture only the game window's client area at native size; false = whole monitor.</summary>
    public bool CaptureWindow { get; init; } = true;
    /// <summary>Record only the game process's audio (WASAPI process loopback).</summary>
    public bool CaptureAudio { get; init; } = true;
    /// <summary>How long to wait for the game window to appear after the process starts.</summary>
    public TimeSpan WindowWait { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>Whole-monitor mode only. Null = primary monitor resolved through DXGI.</summary>
    public int? OutputIndex { get; init; }
    public int AdapterIndex { get; init; }
    /// <summary>Null = first working encoder of the fallback chain.</summary>
    public string? Encoder { get; init; }
    public TimeSpan ClipMargin { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Segment slots before indices wrap and overwrite the oldest (+2 slack: one open, one being read).</summary>
    public int SlotCount => Math.Max(3, BufferMinutes * 60 / Math.Max(1, SegmentSeconds)) + 2;
}
