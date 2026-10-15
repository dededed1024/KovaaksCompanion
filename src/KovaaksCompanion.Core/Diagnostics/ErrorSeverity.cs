namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>How bad an automatic error report is: 1 is worst. The number and the embed color in the webhook message follow it.</summary>
public enum ErrorSeverity
{
    /// <summary>1: crash or unhandled exception.</summary>
    Critical = 1,
    /// <summary>2: a main feature (recording, replay playback, launching) is broken.</summary>
    High = 2,
    /// <summary>3: data or settings may not be read or saved.</summary>
    Medium = 3,
    /// <summary>4: best-effort work failed (cleanup, clipboard); the app keeps working.</summary>
    Low = 4,
}
