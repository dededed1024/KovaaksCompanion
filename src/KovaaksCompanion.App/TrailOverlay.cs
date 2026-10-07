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

        // Recolor grey segments near colored ones with gradient
        var origColors = segs.Select(s => s.C).ToList();
        const int K = 2;
        for (int i = 0; i < segs.Count; i++)
        {
            if (segs[i].C != NeutralGrey) continue;
            // Find nearest non-grey segment within K segments
            Color? nearest = null;
            int nearestDist = int.MaxValue;
            for (int d = 1; d <= K; d++)
            {
                if (i - d >= 0 && (i - d == 0 || segs[i - d].A == segs[i - d + 1].A))
                {
                    if (segs[i - d].C != NeutralGrey)
                    {
                        nearest = segs[i - d].C;
                        nearestDist = d;
                        break;
                    }
                }
                if (i + d < segs.Count && (i + d == segs.Count - 1 || segs[i + d].A == segs[i + d - 1].B))
                {
                    if (segs[i + d].C != NeutralGrey)
                    {
                        nearest = segs[i + d].C;
                        nearestDist = d;
                        break;
                    }
                }
            }
            if (nearest is { } nc)
            {
                var t = nearestDist / (K + 1.0);
                var r = (byte)((nc.R * (1 - t)) + (NeutralGrey.R * t));
                var g = (byte)((nc.G * (1 - t)) + (NeutralGrey.G * t));
                var b = (byte)((nc.B * (1 - t)) + (NeutralGrey.B * t));
                var seg = segs[i];
                segs[i] = (seg.A, seg.B, Color.FromRgb(r, g, b), seg.O);
            }
        }

        // Draw with gradient transitions
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            var startColor = origColors[i];
            var endColor = origColors[i];

            if (i > 0 && segs[i - 1].B == s.A)
                startColor = Blend(origColors[i - 1], origColors[i]);
            if (i + 1 < segs.Count && segs[i + 1].A == s.B)
                endColor = Blend(origColors[i], origColors[i + 1]);

            if (startColor == endColor)
            {
                dc.DrawLine(Pen(startColor, s.O), s.A, s.B);
            }
            else
            {
                var brush = new LinearGradientBrush(
                    Color.FromArgb((byte)(255 * Math.Clamp(s.O, 0, 1)), startColor.R, startColor.G, startColor.B),
                    Color.FromArgb((byte)(255 * Math.Clamp(s.O, 0, 1)), endColor.R, endColor.G, endColor.B),
                    s.A, s.B);
                brush.MappingMode = BrushMappingMode.Absolute;
                brush.Freeze();
                var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                dc.DrawLine(pen, s.A, s.B);
            }
        }
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

    static Color Blend(Color a, Color b)
    {
        var r = (byte)((a.R + b.R) / 2);
        var g = (byte)((a.G + b.G) / 2);
        var bl = (byte)((a.B + b.B) / 2);
        return Color.FromRgb(r, g, bl);
    }

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
