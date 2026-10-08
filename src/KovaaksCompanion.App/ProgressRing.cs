using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Circular progress for a pending run's clip encode; follows <see cref="AppHost.EncodeProgress"/> while on screen.</summary>
sealed class ProgressRing : FrameworkElement
{
    readonly Pen _track, _arc;
    double _value;

    ProgressRing(Brush brush, double size, double thickness, double value)
    {
        _value = value;
        Width = Height = size;
        var track = new SolidColorBrush(Colors.White) { Opacity = 0.14 };
        track.Freeze();
        _track = new Pen(track, thickness);
        _arc = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        IsHitTestVisible = false;
    }

    /// <summary>A ring of the given size that tracks the run's encode progress until it is removed from the tree.</summary>
    public static ProgressRing Create(AppHost host, string scenario, DateTime end, Brush brush, double size, double thickness = 2)
    {
        var ring = new ProgressRing(brush, size, thickness, Math.Max(0, host.PendingProgress(scenario, end)));
        void On(string s, DateTime e, double f)
        {
            if (s.Equals(scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((e - end).TotalSeconds) < Core.Session.SessionStore.RunMatchTolerance.TotalSeconds)
                ring.Dispatcher.BeginInvoke(() => ring.Set(f));
        }
        ring.Loaded += (_, _) => { host.EncodeProgress -= On; host.EncodeProgress += On; ring.Set(Math.Max(0, host.PendingProgress(scenario, end))); };
        ring.Unloaded += (_, _) => host.EncodeProgress -= On;
        return ring;
    }

    void Set(double v)
    {
        v = Math.Clamp(v, 0, 1);
        if (Math.Abs(v - _value) < 0.005) return;
        _value = v;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var r = (Math.Min(ActualWidth, ActualHeight) - _track.Thickness) / 2;
        if (r <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, _track, c, r, r);
        var f = Math.Clamp(_value, 0.03, 0.999);
        var a = f * 2 * Math.PI - Math.PI / 2;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(c.X, c.Y - r), false, false);
            g.ArcTo(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)), new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, _arc, geo);
    }
}
