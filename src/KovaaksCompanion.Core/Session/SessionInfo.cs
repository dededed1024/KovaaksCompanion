using System.Text.Json;
using System.Text.Json.Serialization;
using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Session;

public sealed class SessionFormatException(string message) : Exception(message);

/// <summary>session.dat payload (SES-001). Times are local wall clock, as in the CSV.</summary>
public sealed record SessionInfo
{
    public const int CurrentVersion = 1;

    // Base video-lag compensation; 0 = no correction, adjust per user with SyncNudgeMs.
    public const double DefaultSyncMs = 0;

    public int FormatVersion { get; init; } = CurrentVersion;
    public string Scenario { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    /// <summary>Seconds of video time at which the run Start happens (video t = mouse t + VideoOffsetSec).</summary>
    public double VideoOffsetSec { get; init; }
    /// <summary>Stored for compatibility; playback uses the constant DefaultSyncMs (0 ms base offset).</summary>
    public double SyncDefaultMs { get; init; } = DefaultSyncMs;
    /// <summary>User nudge in ms (VIEW-005), added to the base offset at playback.</summary>
    public double SyncNudgeMs { get; init; }
    public double SampleRateHz { get; init; } = 120;
    public InputSettings Settings { get; init; } = new();
    public bool Partial { get; init; }
    public bool HasVideo { get; init; }
    public double Score { get; init; }
    public int Kills { get; init; }
    public int HitCount { get; init; }
    public int MissCount { get; init; }
    [JsonIgnore] public double Accuracy => HitCount + MissCount == 0 ? 0 : (double)HitCount / (HitCount + MissCount);
    /// <summary>False when the sens scale was unknown: trajectory.bin has no camera path.</summary>
    public bool DegreesAvailable { get; init; } = true;

    /// <summary>Video time (s) of a run-relative mouse time.</summary>
    public double VideoTime(double mouseSec) => mouseSec + VideoOffsetSec + (DefaultSyncMs + SyncNudgeMs) / 1000;

    /// <summary>Run-relative mouse time (s) shown at a video time.</summary>
    public double MouseTime(double videoSec) => videoSec - VideoOffsetSec - (DefaultSyncMs + SyncNudgeMs) / 1000;

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static SessionInfo FromJson(string json)
    {
        int? version;
        try { version = JsonDocument.Parse(json).RootElement.GetProperty("FormatVersion").GetInt32(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new SessionFormatException("session.json is unreadable or has no FormatVersion."); }
        if (version != CurrentVersion)
            throw new SessionFormatException($"Unsupported session format version {version} (this app reads version {CurrentVersion}).");
        return JsonSerializer.Deserialize<SessionInfo>(json, Options) ?? throw new SessionFormatException("session.json is empty.");
    }
}
