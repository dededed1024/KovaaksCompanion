using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Color themes and accent presets. Applying rewrites the shared brushes in the application resources.</summary>
public static class Theme
{
    /// <summary>A theme sets surfaces and accent together.</summary>
    sealed record Look(string Bg, string Surface, string Raised, string Accent, bool Light = false);

    /// <summary>The colored themes are the profile card colors (first six of the profile palette), in palette order.</summary>
    static readonly (string Name, string Accent)[] Colored =
    [
        ("Rose", "#FF375F"), ("Ember", "#FF9F0A"), ("Gold", "#FFD60A"), ("Forest", "#30D158"), ("Ocean", "#0A84FF"), ("Violet", "#BF5AF2"),
    ];

    /// <summary>Accent hex of every colored theme; the profile palette starts with these.</summary>
    public static readonly string[] ColoredAccents = [.. Colored.Select(c => c.Accent)];

    static Look Tinted(string accent)
    {
        var a = Parse(accent);
        var bg = Mix(Color.FromRgb(0x1C, 0x1C, 0x1E), a, 0.09);
        var surface = Mix(bg, Colors.White, 0.04);
        var raised = Mix(Mix(bg, Colors.White, 0.07), a, 0.08);
        return new(Hex(bg), "#F2" + Hex(surface)[1..], Hex(raised), accent);
    }

    static readonly Dictionary<string, Look> Looks = BuildLooks();

    static Dictionary<string, Look> BuildLooks()
    {
        var d = new Dictionary<string, Look>
        {
            ["Dark"] = new("#1C1C1E", "#F2232326", "#2C2C2E", "#0A84FF"),
            ["Light"] = new("#F2F2F7", "#F2FFFFFF", "#FFFFFF", "#0A84FF", true),
            ["Black"] = new("#000000", "#F20A0A0C", "#151517", "#0A84FF"),
        };
        foreach (var (name, accent) in Colored) d[name] = Tinted(accent);
        return d;
    }

    public static readonly string[] Names = [.. Looks.Keys];

    static readonly Dictionary<string, string> Dark = new()
    {
        ["Bg"] = "#1C1C1E", ["Panel"] = "#0FFFFFFF", ["Card"] = "#14FFFFFF", ["Hover"] = "#1FFFFFFF", ["Selected"] = "#29FFFFFF",
        ["Separator"] = "#4D545458", ["Fg"] = "#FFFFFF", ["Dim"] = "#A1A1A8",
        ["Green"] = "#30D158", ["Orange"] = "#FF9F0A", ["Red"] = "#FF453A", ["Purple"] = "#BF5AF2", ["Teal"] = "#64D2FF",
        ["Surface"] = "#F2232326", ["Raised"] = "#2C2C2E", ["Edge"] = "#1FFFFFFF", ["EdgeFaint"] = "#14FFFFFF", ["Scrim"] = "#99000000",
        ["Tint"] = "#0DFFFFFF", ["Wash"] = "#14FFFFFF", ["WashPress"] = "#24FFFFFF", ["Highlight"] = "#1AFFFFFF",
        ["Thumb"] = "#66FFFFFF", ["ThumbHot"] = "#99FFFFFF", ["BgFadeColor"] = "#E61C1C1E", ["BgClearColor"] = "#001C1C1E",
    };

    static readonly Dictionary<string, string> Light = new()
    {
        ["Bg"] = "#F2F2F7", ["Panel"] = "#0A000000", ["Card"] = "#12000000", ["Hover"] = "#14000000", ["Selected"] = "#1F000000",
        ["Separator"] = "#4D3C3C43", ["Fg"] = "#1C1C1E", ["Dim"] = "#6C6C70",
        ["Green"] = "#248A3D", ["Orange"] = "#C46A00", ["Red"] = "#D70015", ["Purple"] = "#8944AB", ["Teal"] = "#0E8FA8",
        ["Surface"] = "#F2FFFFFF", ["Raised"] = "#FFFFFF", ["Edge"] = "#1F000000", ["EdgeFaint"] = "#14000000", ["Scrim"] = "#66000000",
        ["Tint"] = "#0A000000", ["Wash"] = "#0F000000", ["WashPress"] = "#1A000000", ["Highlight"] = "#14000000",
        ["Thumb"] = "#55000000", ["ThumbHot"] = "#88000000", ["BgFadeColor"] = "#E6F2F2F7", ["BgClearColor"] = "#00F2F2F7",
    };

    static ResourceDictionary? _dict;

    public static string Current { get; private set; } = "Dark";

    /// <summary>True when overlays that are white-on-dark must be drawn black-on-light.</summary>
    public static bool IsLight => Looks[Current].Light;

    /// <summary>The color as drawn on the current theme: translucent white washes turn into translucent black on the light theme.</summary>
    public static Color Ink(Color c) => IsLight && c.A < 0xFF && c.R == 0xFF && c.G == 0xFF && c.B == 0xFF ? Color.FromArgb(c.A, 0, 0, 0) : c;

    public static string Normalize(string? theme) => Names.FirstOrDefault(n => n.Equals(theme, StringComparison.OrdinalIgnoreCase)) ?? "Dark";

    /// <summary>Background and accent of a theme, for its swatch.</summary>
    public static (Color Bg, Color Accent) Swatch(string theme)
    {
        var l = Looks[Normalize(theme)];
        return (Parse(l.Bg), Parse(l.Accent));
    }

    static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>WCAG contrast ratio of two opaque colors.</summary>
    public static double Contrast(Color a, Color b)
    {
        double x = ChartPaths.Lum(a), y = ChartPaths.Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    static Color Mix(Color c, Color to, double t) => Color.FromRgb(
        (byte)Math.Round(c.R + (to.R - c.R) * t), (byte)Math.Round(c.G + (to.G - c.G) * t), (byte)Math.Round(c.B + (to.B - c.B) * t));

    public static void Apply(string theme)
    {
        theme = Normalize(theme);
        Current = theme;
        var look = Looks[theme];
        var pal = new Dictionary<string, string>(look.Light ? Light : Dark)
        {
            ["Bg"] = look.Bg, ["Surface"] = look.Surface, ["Raised"] = look.Raised,
        };
        if (!look.Light) { pal["BgFadeColor"] = "#E6" + look.Bg[1..]; pal["BgClearColor"] = "#00" + look.Bg[1..]; }
        if (_dict == null) Application.Current.Resources.MergedDictionaries.Add(_dict = new ResourceDictionary { Source = new Uri("pack://application:,,,/Styles.xaml") });
        var res = _dict;
        foreach (var (key, hex) in pal) Set(res, key, Parse(hex));

        var ac = Parse(look.Accent);
        // Fill carries white text or icons: darken just until they reach 3:1 (the light theme needs none for blue, so it stays bright).
        var fc = look.Light ? ac : Mix(ac, Colors.Black, 0.2);
        while (Contrast(fc, Colors.White) < 3 && fc != Colors.Black) fc = Mix(fc, Colors.Black, 0.08);
        if (look.Light) ac = Mix(ac, Colors.Black, 0.15); // accent text on white
        var bg = Parse(look.Bg);
        // Mica shows through the wash on Dark and Light; the colored themes are opaque so the theme color is what shows.
        var colored = theme is not ("Dark" or "Light" or "Black");
        Set(res, "BgWash", Color.FromArgb(colored ? (byte)0xFF : (byte)0x99, bg.R, bg.G, bg.B));
        Set(res, "Accent", ac); Set(res, "AccentFill", fc); Set(res, "SelectedBg", fc);
        Set(res, "AccentSoft", Color.FromArgb(0x33, ac.R, ac.G, ac.B));
    }

    static void Set(ResourceDictionary res, string key, Color c)
    {
        // A new instance each time: DynamicResource users pick it up, frozen brushes included.
        if (res[key] is SolidColorBrush) res[key] = new SolidColorBrush(c);
        else res[key] = c;
    }
}
