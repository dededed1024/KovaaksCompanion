using System.Windows.Controls;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Icon buttons. The implicit TextBlock style overrides an inherited foreground, so the glyph is an explicit TextBlock that carries its own colour.</summary>
static class GlyphButton
{
    public static void Set(Button button, string glyph, Brush brush)
    {
        if (button.Content is TextBlock t) { t.Text = glyph; t.Foreground = brush; }
        else button.Content = new TextBlock { Text = glyph, Foreground = brush };
    }
}
