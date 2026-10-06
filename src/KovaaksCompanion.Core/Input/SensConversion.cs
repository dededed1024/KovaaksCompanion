using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Input;

/// <summary>Counts to degrees for KovaaK's sens scales. Degrees per count at sens 1.0 (cm/360 uses Dpi).</summary>
public static class SensConversion
{
    // Yaw degrees per count at sensitivity 1. Overwatch/CS:GO/Valorant per spec; Fortnite and Rainbow Six are
    // the commonly quoted constants (unverified against KovaaK's, adjust if a run disagrees).
    static readonly (string Key, double Yaw)[] Table =
    [
        ("overwatch", 0.0066),
        ("cs:go", 0.022), ("csgo", 0.022), ("cs2", 0.022), ("source", 0.022), ("apex", 0.022),
        ("valorant", 0.07),
        ("fortnite", 0.005555),
        ("rainbow", 0.00572957795), ("r6", 0.00572957795),
    ];

    /// <summary>False for an unknown scale or missing data: keep raw counts, degrees are unavailable.</summary>
    public static bool TryGetDegreesPerCount(InputSettings s, out double yawPerCount, out double pitchPerCount)
    {
        yawPerCount = pitchPerCount = 0;
        var scale = s.SensScale.Trim().ToLowerInvariant();
        if (s.HorizSens <= 0 || s.VertSens <= 0) return false;
        if (scale.Contains("cm/360") || scale.Contains("cm per 360"))
        {
            if (s.Dpi <= 0) return false;
            yawPerCount = 360.0 / (s.HorizSens / 2.54 * s.Dpi);
            pitchPerCount = 360.0 / (s.VertSens / 2.54 * s.Dpi);
            return true;
        }
        foreach (var (key, yaw) in Table)
        {
            if (!scale.Contains(key)) continue;
            yawPerCount = yaw * s.HorizSens;
            pitchPerCount = yaw * s.VertSens;
            return true;
        }
        return false;
    }
}
