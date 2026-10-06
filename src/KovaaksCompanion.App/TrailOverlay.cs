using System.Windows;
using System.Windows.Media;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

/// <summary>Draws the VIEW-003 trail over the letterboxed video rect; redrawn every frame from the viewer.</summary>
public sealed class TrailOverlay : FrameworkElement
{
    Trail? _trail;
    ViewDirection _current;
    double _hfov, _aspect;

    public void Set(Trail? trail, ViewDirection current, double hfovDeg, double videoAspect)
    {
        _trail = trail; _current = current; _hfov = hfovDeg; _aspect = videoAspect;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_trail is not { } trail || _aspect <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        // Uniform-stretch video rect inside this element.
        var w = Math.Min(ActualWidth, ActualHeight * _aspect);
        var h = w / _aspect;
        var ox = (ActualWidth - w) / 2;
        var oy = (ActualHeight - h) / 2;
        dc.PushClip(new RectangleGeometry(new Rect(ox, oy, w, h)));

        Point? prev = null;
        double prevOpacity = 0;
        foreach (var p in trail.Points)
        {
            var proj = ViewProjection.Project(p.Dir, _current, _hfov, _aspect);
            if (!proj.Visible) { prev = null; continue; }
            var (x, y) = proj.ToPixels(w, h);
            var pt = new Point(ox + x, oy + y);
            if (prev is { } a)
            {
                var o = (p.Opacity + prevOpacity) / 2;
                dc.DrawLine(Pen(Colors.Cyan, o, 3), a, pt);
            }
            prev = pt; prevOpacity = p.Opacity;
        }

        foreach (var m in trail.FireMarks)
        {
            var proj = ViewProjection.Project(m.Dir, _current, _hfov, _aspect);
            if (!proj.Visible) continue;
            var (x, y) = proj.ToPixels(w, h);
            var brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(m.Opacity, 0, 1)), 255, 70, 70));
            dc.DrawEllipse(brush, new Pen(Brushes.White, 1) { Brush = new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(m.Opacity, 0, 1)), 255, 255, 255)) },
                new Point(ox + x, oy + y), 5, 5);
        }
        dc.Pop();
    }

    static Pen Pen(Color c, double opacity, double thickness) =>
        new(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B)), thickness)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
}
