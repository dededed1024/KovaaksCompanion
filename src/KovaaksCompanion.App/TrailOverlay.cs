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

        // Connected = the segment starts where the previous one ended, so colours can blend across the shared point.
        var segs = new List<Seg>();
        Point? prev = null;
        TrailPoint prevP = default;
        var connected = false;
        foreach (var p in trail.Points)
        {
            var proj = ViewProjection.Project(p.Dir, _current, _hfov, _renderAspect);
            if (!proj.Visible) { prev = null; connected = false; continue; }
            var (x, y) = proj.ToPixels(w, h);
            var pt = new Point(ox + x, oy + y);
            if (prev is { } a) { segs.Add(new(a, pt, prevP.TSec, p.TSec, SegmentColor(trail.FireMarks, prevP.TSec, p.TSec, _held), prevP.Opacity, p.Opacity, connected)); connected = true; }
            prev = pt; prevP = p;
        }

        for (var i = 0; i < segs.Count; i++) dc.DrawLine(SegmentPen(segs, i), segs[i].A, segs[i].B);
        dc.Pop();
    }

    readonly record struct Seg(Point A, Point B, double T0, double T1, Color C, double OA, double OB, bool Connected);

    /// <summary>Colour changes fade over this much trail time, whatever the mouse polling rate: a slow mouse gets long segments, so the fade runs along part of a segment instead of stretching over it.</summary>
    const double BlendSec = 0.002;

    /// <summary>Colour of segment <paramref name="i"/>'s run at time t: segment colours averaged over a window of <see cref="BlendSec"/> around t, the run's end colours extended past its ends.</summary>
    static Color ColorAt(List<Seg> segs, int i, double t)
    {
        const double H = BlendSec / 2;
        double r = 0, g = 0, b = 0, covered = 0;
        void Add(Color c, double len) { r += c.R * len; g += c.G * len; b += c.B * len; covered += len; }

        var j = i;
        while (true)
        {
            var s = segs[j];
            Add(s.C, Math.Max(0, Math.Min(s.T1, t + H) - Math.Max(s.T0, t - H)));
            if (s.T0 <= t - H || !s.Connected) { if (s.T0 > t - H) Add(s.C, s.T0 - (t - H)); break; }
            j--;
        }
        j = i;
        while (true)
        {
            var s = segs[j];
            if (j != i) Add(s.C, Math.Max(0, Math.Min(s.T1, t + H) - Math.Max(s.T0, t - H)));
            if (s.T1 >= t + H || j + 1 >= segs.Count || !segs[j + 1].Connected) { if (s.T1 < t + H) Add(s.C, t + H - s.T1); break; }
            j++;
        }
        if (covered <= 0) return segs[i].C;
        return Color.FromRgb((byte)Math.Round(r / covered), (byte)Math.Round(g / covered), (byte)Math.Round(b / covered));
    }

    static Pen SegmentPen(List<Seg> segs, int i)
    {
        var s = segs[i];
        var dt = s.T1 - s.T0;
        var d = s.B - s.A;
        if (dt <= 0 || d.LengthSquared < 0.01) return Pen(s.C, (s.OA + s.OB) / 2);

        // Stops where the colour is linear: the ends, plus the fade edges inside a segment longer than the blend time.
        var fracs = dt > BlendSec ? new[] { 0, BlendSec / 2 / dt, 1 - BlendSec / 2 / dt, 1 } : new[] { 0.0, 1 };
        var stops = new GradientStopCollection(fracs.Length);
        var first = ColorAt(segs, i, s.T0);
        var same = true;
        foreach (var f in fracs)
        {
            var c = ColorAt(segs, i, s.T0 + f * dt);
            if (c != first) same = false;
            stops.Add(new GradientStop(Color.FromArgb((byte)(255 * Math.Clamp(s.OA + (s.OB - s.OA) * f, 0, 1)), c.R, c.G, c.B), f));
        }
        if (same) return Pen(first, (s.OA + s.OB) / 2);

        var brush = new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0)) { MappingMode = BrushMappingMode.Absolute, StartPoint = s.A, EndPoint = s.B };
        brush.Freeze();
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
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

    const double Thickness = 3.5;
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
