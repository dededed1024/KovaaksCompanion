using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KovaaksCompanion.App;

/// <summary>Spinning icon element.</summary>
static class Spinner
{
    /// <summary>Creates a spinning icon of the given size and color.</summary>
    public static FrameworkElement Create(Brush brush, double size)
    {
        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };

        var rotation = (RotateTransform)icon.RenderTransform;
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rotation.BeginAnimation(RotateTransform.AngleProperty, animation);

        return icon;
    }
}
