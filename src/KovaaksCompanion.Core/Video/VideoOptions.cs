namespace KovaaksCompanion.Core.Video;

public sealed record VideoOptions
{
    /// <summary>Folder holding the unpacked libobs runtime (bin\64bit, obs-plugins\64bit, data).</summary>
    public string ObsRoot { get; init; } = "";
    public string GameProcessName { get; init; } = "FPSAimTrainer-Win64-Shipping";
    /// <summary>Folder holding the rolling packet chunks.</summary>
    public string BufferDir { get; init; } = Path.Combine(Path.GetTempPath(), "KovaaksCompanion", "buffer");
    public const int DefaultFps = 120;
    /// <summary>Capture frame rate (upper limit for window capture).</summary>
    public int Fps { get; init; } = DefaultFps;
    public int BufferMinutes { get; init; } = 10;
    /// <summary>Constant-quality target (CQP / ICQ value of the encoder).</summary>
    public int Quality { get; init; } = 18;
    /// <summary>Caps the output height of window capture (aspect kept, never upscales); null = native size.</summary>
    public int? MaxHeight { get; init; }
    /// <summary>Record only the game process's audio (WASAPI process capture).</summary>
    public bool CaptureAudio { get; init; } = true;
    /// <summary>libobs video encoder id; null = first working encoder of the fallback chain.</summary>
    public string? Encoder { get; init; }
    /// <summary>Footage kept before the run start and after its end.</summary>
    public TimeSpan ClipMargin { get; init; } = TimeSpan.FromSeconds(5);
}
