using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KovaaksCompanion.App;

/// <summary>Film grain: a small tile of black and white specks that repeats over the window background and the popup cards.</summary>
public static class Grain
{
    const int Size = 192;

    static readonly Lazy<ImageBrush> Tile = new(Build);

    static ImageBrush Build()
    {
        var rng = new Random(7); // fixed seed: the same grain every start
        var px = new byte[Size * Size * 4];
        for (var i = 0; i < Size * Size; i++)
        {
            var v = rng.NextDouble() * 2 - 1;
            var a = (byte)Math.Round(Math.Abs(v) * 255);
            var c = v > 0 ? a : (byte)0; // premultiplied: white speck is (a,a,a,a), black speck is (0,0,0,a)
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = c;
            px[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Pbgra32, null, px, Size * 4);
        bmp.Freeze();
        var brush = new ImageBrush(bmp)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, Size, Size), Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>The grain tile as a brush; give the element that carries it an Opacity for the strength.</summary>
    public static Brush Brush => Tile.Value;

    /// <summary>Opacity of the grain layer for a strength of 0-100: a faint speckle at the top.</summary>
    public static double Opacity(int strength) => Math.Clamp(strength, 0, 100) / 100.0 * 0.05;

    static int _strength;
    static event Action? Changed;

    /// <summary>Sets the strength (0-100) of every grain layer: the window background and the popup cards.</summary>
    public static void Set(int strength)
    {
        _strength = strength;
        Changed?.Invoke();
    }

    public static readonly DependencyProperty CardProperty = DependencyProperty.RegisterAttached("Card", typeof(bool), typeof(Grain), new PropertyMetadata(false, OnCard));

    public static bool GetCard(DependencyObject o) => (bool)o.GetValue(CardProperty);
    public static void SetCard(DependencyObject o, bool value) => o.SetValue(CardProperty, value);

    /// <summary>A popup card shows the grain between its background and its content: the content moves into a grid under which the grain layer lies.</summary>
    static void OnCard(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border card || e.NewValue is not true) return;
        card.Loaded += (_, _) =>
        {
            if (card.Child is not { } child || card.Tag is "grain") return;
            card.Tag = "grain";
            var inner = new CornerRadius(Math.Max(0, card.CornerRadius.TopLeft - card.BorderThickness.Left));
            var layer = new Border { IsHitTestVisible = false, CornerRadius = inner, Background = Brush };
            void Sync()
            {
                layer.Visibility = _strength > 0 ? Visibility.Visible : Visibility.Collapsed;
                layer.Opacity = Opacity(_strength);
            }
            Sync();
            Changed += Sync;
            card.Unloaded += (_, _) => Changed -= Sync;
            card.Child = null;
            var host = new Grid();
            host.Children.Add(layer);
            host.Children.Add(child);
            card.Child = host;
        };
    }
}
