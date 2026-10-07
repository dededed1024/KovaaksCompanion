using System.Windows;
using System.Windows.Media;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

/// <summary>Draws the VIEW-003 trail over the letterboxed video rect; redrawn every frame from the viewer. Projects at the game's render aspect and assumes the rendered image is stretched to fill the video frame. Trail segments at a shot are coloured by outcome.</summary>
public sealed class TrailOverlay : FrameworkElement
{
    Trail? _trail;
    ViewDirection _current;
    HeldAccuracy? _held;
    double _hfov, _aspect, _renderAspect;

    public void Set(Trail? trail, ViewDirection current, double hfovDeg, double videoAspect, double renderAspect, HeldAccuracy? held = null)
    {
        _held = held; _trail = trail; _current = current; _hfov = hfovDeg; _aspect = videoAspect; _renderAspect = renderAspect;
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

        var segs = new List<(Point A, Point B, Color C, double O)>();
        Point? prev = null;
        TrailPoint prevP = default;
        foreach (var p in trail.Points)
        {
            var proj = ViewProjection.Project(p.Dir, _current, _hfov, _renderAspect);
            if (!proj.Visible) { prev = null; continue; }
            var (x, y) = proj.ToPixels(w, h);
            var pt = new Point(ox + x, oy + y);
            if (prev is { } a) segs.Add((a, pt, SegmentColor(trail.FireMarks, prevP.TSec, p.TSec, _held), (p.Opacity + prevP.Opacity) / 2));
            prev = pt; prevP = p;
        }

        foreach (var s in segs) dc.DrawLine(Pen(s.C, s.O), s.A, s.B);
        dc.Pop();
    }

    /// <summary>Slack around a Fire event so the segment holding the shot is coloured despite sample spacing.</summary>
    const double ShotSlackSec = 0.04;

    /// <summary>Fully saturated HSV hue sweep: red (0) to green (1) through orange and yellow; the ends are the per-shot Miss/Hit colours.</summary>
    public static (byte R, byte G, byte B) AccuracyRgb(double accuracy) => HsvToRgb(Math.Clamp(accuracy, 0, 1) * 120, 1, 1);

    public static (byte R, byte G, byte B) HsvToRgb(double hueDeg, double s, double v)
    {
        var h = (hueDeg % 360 + 360) % 360 / 60;
        var c = v * s;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)h switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        byte B(double d) => (byte)Math.Round((d + m) * 255);
        return (B(r), B(g), B(b));
    }

    /// <summary>Per-shot Hit (green) / Miss (red) win; otherwise continuous red-to-green colour by interpolated bucket accuracy while the button is held (tracking); grey when not firing or no data.</summary>
    public static Color SegmentColor(IReadOnlyList<TrailPoint> fireMarks, double t0, double t1, HeldAccuracy? held = null)
    {
        Color? color = null;
        foreach (var m in fireMarks)
        {
            if (m.TSec < t0 - ShotSlackSec || m.TSec > t1 + ShotSlackSec) continue;
            if (m.Outcome == ShotOutcome.Hit) { var (r, g, b) = AccuracyRgb(1); return Color.FromRgb(r, g, b); }
            if (m.Outcome == ShotOutcome.Miss) { var (r, g, b) = AccuracyRgb(0); color = Color.FromRgb(r, g, b); }
        }
        if (color is { } c) return c;
        if (held?.AccuracyAt((t0 + t1) / 2) is { } acc)
        {
            var (r, g, b) = AccuracyRgb(acc);
            return Color.FromRgb(r, g, b);
        }
        return NeutralGrey;
    }

    /// <summary>Not firing, or no data.</summary>
    public static readonly Color NeutralGrey = Color.FromRgb(0x8E, 0x8E, 0x93);

    const double Thickness = 3;
    static readonly Dictionary<(Color, byte), Pen> Pens = [];

    static Pen Pen(Color c, double opacity)
    {
        var key = (c, (byte)(255 * Math.Clamp(opacity, 0, 1)));
        if (!Pens.TryGetValue(key, out var pen))
        {
            var b = new SolidColorBrush(Color.FromArgb(key.Item2, c.R, c.G, c.B)); b.Freeze();
            pen = new Pen(b, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            if (Pens.Count > 4096) Pens.Clear();
            Pens[key] = pen;
        }
        return pen;
    }
}
