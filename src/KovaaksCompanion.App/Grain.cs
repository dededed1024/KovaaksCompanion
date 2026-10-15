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

    /// <summary>
    /// Paints <paramref name="layer"/> with grain that moves with the content scrolled inside <paramref name="scope"/>, so the grain reads as part of the background the content sits on.
    /// The layer itself stays put; the tile is shifted by the scroll offset of the largest scroll viewer that <paramref name="accept"/> lets through.
    /// </summary>
    public static void Follow(Border layer, UIElement scope, Func<ScrollViewer, bool>? accept = null)
    {
        var move = new TranslateTransform();
        var brush = (ImageBrush)Tile.Value.Clone(); // an own copy: each layer has its own shift
        brush.Transform = move;
        layer.Background = brush;
        ScrollViewer? main = null;
        static double Area(ScrollViewer s) => s.ViewportWidth * s.ViewportHeight;
        scope.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.OriginalSource is not ScrollViewer sv || accept?.Invoke(sv) == false) return;
            if (main == null || !main.IsLoaded || sv == main || Area(sv) > Area(main)) main = sv;
            if (sv != main) return;
            move.X = -sv.HorizontalOffset; move.Y = -sv.VerticalOffset;
        }));
    }

    /// <summary>True when <paramref name="e"/> lies inside a popup card (<see cref="CardProperty"/>).</summary>
    public static bool InCard(DependencyObject e)
    {
        for (var p = VisualTreeHelper.GetParent(e); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is Border b && GetCard(b)) return true;
        return false;
    }

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
            var layer = new Border { IsHitTestVisible = false, CornerRadius = inner };
            Follow(layer, card);
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
