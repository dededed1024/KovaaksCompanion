using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KovaaksCompanion.App;

/// <summary>A color disc: the angle is the hue, the distance from the center the saturation. A dot marks the pick while it is in use.</summary>
public sealed class ColorWheel : FrameworkElement
{
    const int Px = 320; // bitmap size; drawn smaller, so the edge stays sharp on high DPI

    static readonly Lazy<BitmapSource> Disc = new(Build);

    static BitmapSource Build()
    {
        var px = new byte[Px * Px * 4];
        double c = (Px - 1) / 2.0, radius = Px / 2.0 - 1;
        for (var y = 0; y < Px; y++)
            for (var x = 0; x < Px; x++)
            {
                double dx = x - c, dy = y - c, d = Math.Sqrt(dx * dx + dy * dy);
                var a = Math.Clamp(radius - d + 0.5, 0, 1); // one pixel of antialiasing at the rim
                if (a <= 0) continue;
                var hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                var col = Theme.FromHsv(hue, Math.Min(1, d / radius), 1);
                var i = (y * Px + x) * 4;
                px[i] = (byte)Math.Round(col.B * a); px[i + 1] = (byte)Math.Round(col.G * a); px[i + 2] = (byte)Math.Round(col.R * a); px[i + 3] = (byte)Math.Round(255 * a);
            }
        var bmp = BitmapSource.Create(Px, Px, 96, 96, PixelFormats.Pbgra32, null, px, Px * 4);
        bmp.Freeze();
        return bmp;
    }

    double _hue, _saturation, _brightness = 1;
    bool _active, _dragging;

    /// <summary>The pick: hue in degrees (0-360) and saturation (0-1).</summary>
    public (double Hue, double Saturation) Pick
    {
        get => (_hue, _saturation);
        set { (_hue, _saturation) = value; InvalidateVisual(); }
    }

    /// <summary>Brightness (0-1) of the background the pick makes; the dot shows that color, the disc stays at full brightness.</summary>
    public double Brightness
    {
        get => _brightness;
        set { _brightness = value; InvalidateVisual(); }
    }

    /// <summary>True when the pick is the theme in use; the dot is drawn only then.</summary>
    public bool Active
    {
        get => _active;
        set { _active = value; InvalidateVisual(); }
    }

    /// <summary>The pick moves (while dragging).</summary>
    public event Action? Picking;
    /// <summary>The pick is final (mouse released).</summary>
    public event Action? Picked;

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawImage(Disc.Value, rect);
        if (Math.Min(ActualWidth, ActualHeight) >= 40) // a hairline rim, so the disc keeps its edge on any background
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), 1), new Point(ActualWidth / 2, ActualHeight / 2), ActualWidth / 2 - 0.5, ActualHeight / 2 - 0.5);
        if (!_active) return;
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        var a = _hue * Math.PI / 180;
        var p = new Point(r + Math.Cos(a) * _saturation * (r - 1), r + Math.Sin(a) * _saturation * (r - 1));
        var k = _dragging ? 1.15 : 1; // the thumb swells while it is held
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0)), null, new Point(p.X, p.Y + 1.5), 11 * k, 11 * k);
        dc.DrawEllipse(new SolidColorBrush(Theme.FromHsv(_hue, _saturation, _brightness)), new Pen(Brushes.White, 3), p, 9 * k, 9 * k);
        dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), 1), p, 10.5 * k, 10.5 * k);
    }

    /// <summary>A gradient track for a brightness slider: the pick's color from brightness <paramref name="min"/> to <paramref name="max"/> (0-1).</summary>
    public static Brush BrightnessTrack(double hue, double saturation, double min, double max) =>
        new LinearGradientBrush(Theme.FromHsv(hue, saturation, min), Theme.FromHsv(hue, saturation, max), new Point(0, 0), new Point(1, 0));

    void PickAt(Point p)
    {
        var r = Math.Min(ActualWidth, ActualHeight) / 2;
        double dx = p.X - r, dy = p.Y - r;
        _hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
        _saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / r);
        _active = true;
        InvalidateVisual();
        Picking?.Invoke();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        PickAt(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) PickAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        InvalidateVisual();
        ReleaseMouseCapture();
        Picked?.Invoke();
    }

    protected override void OnMouseEnter(MouseEventArgs e) { base.OnMouseEnter(e); Cursor = Cursors.Cross; }
}
