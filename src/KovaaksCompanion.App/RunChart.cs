using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Running totals at one moment, placed on the video time axis. Every value but <see cref="Accuracy"/> is NaN when unknown.</summary>
public readonly record struct ChartPoint(double VideoSec, double Accuracy, double Score = double.NaN, double Kills = double.NaN,
    double Shots = double.NaN, double Hits = double.NaN, double MouseSpeed = double.NaN)
{
    public double this[ChartSeries s] => s switch
    {
        ChartSeries.Accuracy => Accuracy, ChartSeries.Score => Score, ChartSeries.Kills => Kills,
        ChartSeries.Shots => Shots, ChartSeries.Hits => Hits, _ => MouseSpeed,
    };
}

public enum ChartSeries { Accuracy, Score, Kills, Shots, Hits, MouseSpeed }

public static class ChartSeriesInfo
{
    public static readonly ChartSeries[] All = Enum.GetValues<ChartSeries>();

    public static string Name(ChartSeries s) => s switch { ChartSeries.MouseSpeed => L.T("Mouse speed"), _ => L.T(s.ToString()) };

    public static string BrushKey(ChartSeries s) => s switch
    {
        ChartSeries.Accuracy => "Accent", ChartSeries.Score => "Green", ChartSeries.Kills => "Orange",
        ChartSeries.Shots => "Purple", ChartSeries.Hits => "Teal", _ => "Yellow",
    };

    public static Brush Brush(ChartSeries s) => (Brush)Application.Current.FindResource(BrushKey(s));

    /// <summary>Percent series share the left axis; count series each scale from 0 to their own maximum.</summary>
    public static bool IsPercent(ChartSeries s) => s == ChartSeries.Accuracy;

    public static string Format(ChartSeries s, double v) => double.IsNaN(v) ? "–" : IsPercent(s) ? $"{v:P0}" : s == ChartSeries.MouseSpeed ? $"{v:0}°/s" : $"{v:0}";
}

/// <summary>
/// Selected run series and kill ticks over the video time axis, with a playhead. Shares its x axis with the seek bar.
/// Percent series share a left axis fitted to their data; each count series scales from 0 to its own round maximum,
/// and the right axis labels the first shown one. Axis labels sit inside the plot so the x axis stays aligned with the seek bar.
/// </summary>
public sealed class RunChart : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Pen PlayheadPen = MakePlayheadPen();
    static Pen MakePlayheadPen()
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0x3A, 0xB8)), 2);
        pen.Freeze();
        return pen;
    }
    IReadOnlyList<ChartPoint> _points = [];
    IReadOnlyList<double> _kills = [];
    readonly HashSet<ChartSeries> _visible = [];
    IReadOnlyList<ChartPoint> _ghost = [];
    double _duration, _playhead;

    public bool HasData => _points.Count > 0;

    public bool Has(ChartSeries s) => _points.Any(p => !double.IsNaN(p[s]));

    /// <summary>Another run's points on the same time axis, drawn dashed behind the run; empty clears it.</summary>
    public void SetGhost(IReadOnlyList<ChartPoint> points)
    {
        _ghost = points;
        InvalidateVisual();
    }

    public void Set(IReadOnlyList<ChartPoint> points, double duration, IReadOnlyList<double>? killVideoSecs = null)
    {
        _points = points; _duration = duration; _kills = killVideoSecs ?? [];
        InvalidateVisual();
    }

    public void SetVisible(IEnumerable<ChartSeries> shown)
    {
        _visible.Clear();
        _visible.UnionWith(shown);
        InvalidateVisual();
    }

    public void SetPlayhead(double videoSec)
    {
        _playhead = videoSec;
        InvalidateVisual();
    }

    /// <summary>Running totals at a video time, read off the same monotone curve the chart draws, or null before the first point.</summary>
    public ChartPoint? At(double videoSec)
    {
        if (_points.Count == 0 || _points[0].VideoSec > videoSec) return null;
        double V(ChartSeries s) => ChartPaths.YAt(_points.Where(p => !double.IsNaN(p[s])).Select(p => new Point(p.VideoSec, p[s])).ToList(), videoSec);
        return new ChartPoint(videoSec, V(ChartSeries.Accuracy), V(ChartSeries.Score), V(ChartSeries.Kills), V(ChartSeries.Shots), V(ChartSeries.Hits), V(ChartSeries.MouseSpeed));
    }

    bool Shown(ChartSeries s) => _visible.Contains(s) && Has(s);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_duration <= 0 || _points.Count == 0)
        {
            // nothing to plot: the empty frame keeps the same guide lines
            var guide = new Pen((Brush)Application.Current.FindResource("Separator"), 1);
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                var gy = h - 4 - f * (h - 8);
                dc.DrawLine(guide, new Point(0, gy), new Point(w, gy));
            }
            return;
        }

        var dim = (Brush)Application.Current.FindResource("Dim");
        var red = (Brush)Application.Current.FindResource("Red");
        var sep = new Pen((Brush)Application.Current.FindResource("Separator"), 1);
        var shown = ChartSeriesInfo.All.Where(Shown).ToList();

        // Left axis: fitted to the percent series that are shown.
        var ghost = _ghost.Where(p => p.VideoSec <= _duration).ToList();
        var all = _points.Concat(ghost).ToList(); // both runs share the scale so they compare directly
        var pct = shown.Where(ChartSeriesInfo.IsPercent).SelectMany(s => all.Select(p => p[s]).Where(v => !double.IsNaN(v))).ToList();
        var (lo, hi) = PercentRange(pct);

        // Count series: 0 to a round maximum each.
        var max = shown.Where(s => !ChartSeriesInfo.IsPercent(s)).ToDictionary(s => s, s => NiceCeil(all.Select(p => p[s]).Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Max()));

        double X(double sec) => Math.Clamp(sec / _duration, 0, 1) * w;
        double Y(double frac) => h - 4 - Math.Clamp(frac, 0, 1) * (h - 8);
        double YOf(ChartSeries s, double v) => ChartSeriesInfo.IsPercent(s)
            ? Y(hi > lo ? (v - lo) / (hi - lo) : 0)
            : Y(max[s] > 0 ? v / max[s] : 0);

        foreach (var f in new[] { 0.0, 0.5, 1.0 })
            dc.DrawLine(sep, new Point(0, Y(f)), new Point(w, Y(f)));

        foreach (var k in _kills)
        {
            var kx = X(k);
            dc.DrawRoundedRectangle(red, null, new Rect(kx - 1, h - 6, 2, 6), 1, 1);
        }

        foreach (var s in shown.OrderByDescending(s => s))
        {
            var brush = ChartSeriesInfo.Brush(s);
            var ghostBrush = brush.CloneCurrentValue();
            ghostBrush.Opacity = 0.5;
            var pen = new Pen(ghostBrush, 1.5) { LineJoin = PenLineJoin.Round, DashStyle = new DashStyle([3, 3], 0) };
            Line(dc, ghost.Where(p => !double.IsNaN(p[s])).Select(p => new Point(X(p.VideoSec), YOf(s, p[s]))), pen);
        }

        // Accuracy last so it sits on top, as before.
        foreach (var s in shown.OrderByDescending(s => s))
            Line(dc, _points.Where(p => !double.IsNaN(p[s])).Select(p => new Point(X(p.VideoSec), YOf(s, p[s]))),
                new Pen(ChartSeriesInfo.Brush(s), s == ChartSeries.Accuracy ? 2 : 1.5) { LineJoin = PenLineJoin.Round }, s == shown[0] ? Y(0) : null);

        var x = X(_playhead);
        dc.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, h));
        if (At(_playhead) is { } cur)
            foreach (var s in shown.OrderByDescending(s => s))
                if (!double.IsNaN(cur[s]))
                    dc.DrawEllipse(ChartSeriesInfo.Brush(s), new Pen(Brushes.White, 1), new Point(x, YOf(s, cur[s])), s == ChartSeries.Accuracy ? 4 : 3, s == ChartSeries.Accuracy ? 4 : 3);

        // Axis labels: percent at the left edge, the first shown count series at the right edge in its colour.
        if (pct.Count > 0)
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
                Label(dc, $"{(lo + (hi - lo) * f) * 100:0}%", 4, Y(f), f, dim, left: true);
        // Each count series gets its own column, filling in from the right edge.
        var rx = w - 4;
        foreach (var (cs, cmax) in max)
        {
            if (cmax <= 0) continue;
            var colW = 0.0;
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
                colW = Math.Max(colW, Label(dc, Compact(cmax * f), rx, Y(f), f, ChartSeriesInfo.Brush(cs), left: false));
            rx -= colW + 8;
        }
    }

    /// <summary>Percent axis bounds in 5 % steps around the data, at least 20 points tall and never past 0..100 %.</summary>
    static (double Lo, double Hi) PercentRange(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return (0, 1);
        var lo = Math.Max(0, Math.Floor((values.Min() - 0.02) * 20) / 20);
        var hi = Math.Min(1, Math.Ceiling((values.Max() + 0.02) * 20) / 20);
        if (hi - lo < 0.2) { hi = Math.Min(1, lo + 0.2); lo = Math.Max(0, hi - 0.2); }
        return (lo, hi);
    }

    /// <summary>Smallest 1, 2, 2.5, 5 or 10 times a power of ten that is at least <paramref name="v"/>.</summary>
    static double NiceCeil(double v)
    {
        if (v <= 0) return 0;
        var p = Math.Pow(10, Math.Floor(Math.Log10(v)));
        var f = v / p;
        return p * (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10);
    }

    static string Compact(double v) => v >= 1000 ? $"{v / 1000:0.##}k" : $"{v:0}";

    static void Line(DrawingContext dc, IEnumerable<Point> pts, Pen pen, double? areaBase = null)
    {
        var list = pts.ToList();
        if (list.Count == 0) return;
        if (list.Count == 1) { dc.DrawEllipse(pen.Brush, null, list[0], 1.5, 1.5); return; }
        if (areaBase is { } by && pen.Brush is SolidColorBrush sb)
        {
            var c = sb.Color;
            var grad = new LinearGradientBrush(Color.FromArgb(0x38, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, ChartPaths.MonotoneArea(list, by));
        }
        dc.DrawGeometry(null, pen, ChartPaths.Monotone(list));
    }

    /// <summary>Axis label at <paramref name="y"/>, nudged inward at the top and bottom gridlines so it stays inside the plot.</summary>
    static double Label(DrawingContext dc, string text, double x, double y, double frac, Brush brush, bool left)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, brush, 1.0);
        var ty = frac >= 1 ? y + 1 : frac <= 0 ? y - ft.Height - 1 : y - ft.Height / 2;
        dc.DrawText(ft, new Point(left ? x : x - ft.Width, ty));
        return ft.Width;
    }
}
