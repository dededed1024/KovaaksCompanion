using KovaaksCompanion.Core.Stats;

namespace KovaaksCompanion.Core.Input;

/// <summary>Counts to degrees for KovaaK's sens scales, mirroring the IncrementFormula of each scale in FovSensConfig.json.</summary>
public static class SensConversion
{
    const double Pi = Math.PI;

    static Func<double, double, double> Linear(double k) => (sens, _) => sens * k;
    static Func<double, double, double> LinearFov(double k) => (sens, fov) => sens * k * fov * 0.001;
    static Func<double, double, double> Affine(double k, double c) => (sens, _) => sens * k + c;

    // Degrees per count from (sens, fov), keyed by KovaaK's ScaleName. cm/360 and in/360 are handled separately (they need Dpi).
    static readonly Dictionary<string, Func<double, double, double>> Scales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Quake/Source"] = Linear(0.022),
        ["Quake Champions"] = Linear(0.022),
        ["Overwatch"] = Linear(0.0066),
        ["Valorant"] = Linear(0.06996),
        ["Apex Legends"] = Linear(0.022),
        ["Fortnite"] = Linear(0.005555),
        ["Splitgate"] = LinearFov(0.104156),
        ["Counter-Strike"] = Linear(0.022),
        ["CSGO"] = Linear(0.022),
        ["Call of Duty"] = Linear(0.0066),
        ["Rainbow 6: Siege"] = Linear(0.018 / Pi),
        ["Diabotical"] = Linear(1.0 / 60),
        ["Destiny 2"] = Linear(0.0066),
        ["Battlefield V/1/Hardline"] = Affine(0.0034377486, 0.011459162),
        ["Paladins"] = LinearFov(0.091176),
        ["Halo"] = Linear(0.022222),
        ["Rust"] = Linear(0.1125),
        ["Reflex Arena"] = Linear(0.018 / Pi),
        ["Batallion"] = Linear(0.017501),
        ["UE4"] = Linear(0.07),
        ["PUBG"] = (sens, fov) => 0.00005555 * Math.Pow(10, sens / 50) * fov,
        ["Hunt: Showdown"] = Linear(0.0429718162181364),
        ["Gundam Evolution"] = Linear(0.0003888500001),
        ["The FINALS"] = Linear(0.001),
        ["Roblox"] = Linear(1.01061008),
        ["Roblox Arsenal"] = Linear(0.375),
        ["Marvel Rivals"] = Linear(0.0175),
        ["Deadlock"] = Linear(0.044),
        ["Fragpunk"] = Linear(0.05555),
        ["GTA 5"] = Affine(0.005446, 0.032809),
        ["Strinova"] = Linear(0.01388194363),
        ["Delta Force"] = Linear(0.03),
        ["Battlefield 6"] = Affine(0.0017362342283, 0.002314981532),
        ["counts/360"] = (sens, _) => 360.0 / sens,
    };

    // Older / abbreviated spellings, matched by substring after the exact names.
    static readonly (string Key, double Yaw)[] Aliases =
    [
        ("overwatch", 0.0066), ("valorant", 0.06996),
        ("cs:go", 0.022), ("csgo", 0.022), ("cs2", 0.022), ("source", 0.022), ("apex", 0.022),
        ("rainbow", 0.018 / Pi), ("r6", 0.018 / Pi),
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
        if (scale.Contains("in/360") || scale.Contains("in per 360") || scale.Contains("inches/360"))
        {
            if (s.Dpi <= 0) return false;
            yawPerCount = 360.0 / (s.HorizSens * s.Dpi);
            pitchPerCount = 360.0 / (s.VertSens * s.Dpi);
            return true;
        }
        if (Scales.TryGetValue(s.SensScale.Trim(), out var f))
        {
            yawPerCount = f(s.HorizSens, s.Fov);
            pitchPerCount = f(s.VertSens, s.Fov);
            return yawPerCount > 0 && pitchPerCount > 0 && double.IsFinite(yawPerCount) && double.IsFinite(pitchPerCount);
        }
        foreach (var (key, yaw) in Aliases)
        {
            if (!scale.Contains(key)) continue;
            yawPerCount = yaw * s.HorizSens;
            pitchPerCount = yaw * s.VertSens;
            return true;
        }
        return false;
    }
}
