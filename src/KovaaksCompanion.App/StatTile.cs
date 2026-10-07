namespace KovaaksCompanion.App;

/// <summary>One labelled metric. <see cref="Tone"/> is "Up", "Down" or "" and colours the value.</summary>
public sealed record StatTile(string Label, string Value, string Tone = "", System.Windows.Media.Brush? Accent = null)
{
    /// <summary>Tone for a signed change: positive Up, negative Down, zero or null none.</summary>
    public static string ToneOf(double? change) => change is > 0 ? "Up" : change is < 0 ? "Down" : "";
}
