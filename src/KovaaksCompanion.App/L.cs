using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace KovaaksCompanion.App;

/// <summary>UI text lookup. The English source text is the key; a language file (Resources/Strings_xx.resx) maps it to that language and a missing entry shows the English text.</summary>
public static class L
{
    public static readonly (string Code, string Name)[] Languages = [("en", "English"), ("ko", "한국어")];

    static ResourceManager? _rm;
    public static string Code { get; private set; } = "en";
    public static CultureInfo Culture { get; private set; } = CultureInfo.InvariantCulture;

    /// <summary>Picks the language: a known code, or Windows' UI language when empty/unknown.</summary>
    public static void Set(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        if (!Languages.Any(l => l.Code == c)) c = Languages.Any(l => l.Code == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : "en";
        Code = c;
        Culture = c == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(c);
        _rm = c == "en" ? null : new ResourceManager("KovaaksCompanion.App.Resources.Strings_" + c, typeof(L).Assembly);
    }

    /// <summary>Culture for dates and times: the selected language's, with US English for English (the invariant culture has no real date patterns).</summary>
    public static CultureInfo DateCulture => Code == "en" ? CultureInfo.GetCultureInfo("en-US") : Culture;

    public static string ShortDate(DateTime t) => t.ToString("d", DateCulture);
    public static string MonthDay(DateTime t) => t.ToString("M", DateCulture);
    public static string Time(DateTime t) => t.ToString("HH:mm", CultureInfo.InvariantCulture);
    public static string Stamp(DateTime t) => ShortDate(t) + " " + Time(t);

    public static string T(string key)
    {
        if (_rm == null) return key;
        try { return _rm.GetString(key, CultureInfo.InvariantCulture) ?? key; }
        catch (MissingManifestResourceException) { return key; }
    }

    /// <summary>Translated format string filled with <paramref name="args"/>.</summary>
    public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);
}

/// <summary>XAML: <c>Text="{app:Loc Settings}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}
