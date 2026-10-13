using System.Globalization;

namespace KovaaksCompanion.App;

/// <summary>UI time text: 24-hour clock, culture-independent.</summary>
static class ClockFormat
{
    public static string Clock(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string Range(DateTime a, DateTime b) => $"{Clock(a)} – {Clock(b)}";
    public static string DateClock(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " " + Clock(t);
}
