using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Running totals at one moment, placed on the video time axis. <see cref="Score"/> is NaN when unknown.</summary>
public readonly record struct ChartPoint(double VideoSec, double Accuracy, double RecentAccuracy = double.NaN, double Score = double.NaN);

/// <summary>Accuracy (running and last 10 s) and running score over the video time axis, with a playhead. Shares its x axis with the seek bar.</summary>
public sealed class RunChart : FrameworkElement
{
    static readonly Typeface Face = new("Segoe UI Variable Text");
    IReadOnlyList<ChartPoint> _points = [];
    double _duration, _playhead;

    public void Set(IReadOnlyList<ChartPoint> points, double duration)
    {
        _points = points; _duration = duration;
        InvalidateVisual();
    }

    public void SetPlayhead(double videoSec)
    {
        _playhead = videoSec;
        InvalidateVisual();
    }

    /// <summary>Running totals at a video time, or null before the first kill.</summary>
    public ChartPoint? At(double videoSec)
    {
        ChartPoint? last = null;
        foreach (var p in _points) { if (p.VideoSec <= videoSec) last = p; else break; }
        return last;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _duration <= 0 || _points.Count == 0) return;

        var dim = (Brush)Application.Current.FindResource("Dim");
        var accent = (Brush)Application.Current.FindResource("Accent");
        var sep = new Pen((Brush)Application.Current.FindResource("Separator"), 1);

        double X(double sec) => Math.Clamp(sec / _duration, 0, 1) * w;
        double YAcc(double a) => h - 4 - Math.Clamp(a, 0, 1) * (h - 8);

        foreach (var f in new[] { 0.0, 0.5, 1.0 })
            dc.DrawLine(sep, new Point(0, YAcc(f)), new Point(w, YAcc(f)));

        var orange = (Brush)Application.Current.FindResource("Orange");
        var green = (Brush)Application.Current.FindResource("Green");
        var maxScore = _points.Where(p => !double.IsNaN(p.Score)).Select(p => p.Score).DefaultIfEmpty(0).Max();
        double YScore(double s) => YAcc(maxScore > 0 ? s / maxScore : 0);

        if (maxScore > 0)
            Line(dc, _points.Select(p => new Point(X(p.VideoSec), YScore(p.Score))), new Pen(green, 1.5) { LineJoin = PenLineJoin.Round }, h, null);
        if (_points.Any(p => !double.IsNaN(p.RecentAccuracy)))
            Line(dc, _points.Select(p => new Point(X(p.VideoSec), YAcc(p.RecentAccuracy))), new Pen(orange, 1.5) { LineJoin = PenLineJoin.Round }, h, null);
        Line(dc, _points.Select(p => new Point(X(p.VideoSec), YAcc(p.Accuracy))), new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, h, accent);

        var x = X(_playhead);
        dc.DrawLine(new Pen(Brushes.White, 1.5), new Point(x, 0), new Point(x, h));
        if (At(_playhead) is { } cur)
        {
            if (maxScore > 0 && !double.IsNaN(cur.Score)) dc.DrawEllipse(green, new Pen(Brushes.White, 1), new Point(x, YScore(cur.Score)), 3, 3);
            if (!double.IsNaN(cur.RecentAccuracy)) dc.DrawEllipse(orange, new Pen(Brushes.White, 1), new Point(x, YAcc(cur.RecentAccuracy)), 3, 3);
            dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5), new Point(x, YAcc(cur.Accuracy)), 4, 4);
        }

        var lx = w - 4;
        if (maxScore > 0) lx = Legend(dc, "score", lx, green);
        if (_points.Any(p => !double.IsNaN(p.RecentAccuracy))) lx = Legend(dc, "last 10s", lx, orange);
        Legend(dc, "accuracy", lx, accent);
        Label(dc, "100%", 4, 0, dim);
        Label(dc, "0%", 4, h - 15, dim);
    }

    static void Line(DrawingContext dc, IEnumerable<Point> pts, Pen pen, double h, Brush? fill)
    {
        var list = pts.ToList();
        if (list.Count == 0) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(list[0], false, false);
            c.PolyLineTo(list.Skip(1).ToList(), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    /// <summary>Right-aligned legend entry ending at <paramref name="right"/>; returns where the next one ends.</summary>
    static double Legend(DrawingContext dc, string text, double right, Brush color)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, color, 1.0);
        var x = right - ft.Width;
        dc.DrawText(ft, new Point(x, 0));
        return x - 10;
    }

    static void Label(DrawingContext dc, string text, double x, double y, Brush brush)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, brush, 1.0);
        dc.DrawText(ft, new Point(x, y));
    }
}
