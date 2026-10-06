using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Value over time: a line with dots, optional highlighted points (personal bests) and a step line (best so far).</summary>
public sealed class ScoreChart : FrameworkElement
{
    static readonly Typeface Face = new("Segoe UI Variable Text");
    IReadOnlyList<(DateTime When, double Value)> _points = [];
    IReadOnlyList<int> _marked = [];
    IReadOnlyList<double>? _step;
    string _format = "0.#";

    public void Set(IReadOnlyList<(DateTime When, double Value)> points, string format = "0.#",
        IReadOnlyList<int>? marked = null, IReadOnlyList<double>? stepLine = null)
    {
        _points = points; _format = format; _marked = marked ?? []; _step = stepLine;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var dim = (Brush)Application.Current.FindResource("Dim");
        if (w <= 0 || h <= 0) return;
        if (_points.Count == 0) { Label(dc, "No data", w / 2 - 20, h / 2 - 8, dim); return; }

        var accent = (Brush)Application.Current.FindResource("Accent");
        var green = (Brush)Application.Current.FindResource("Green");
        var sep = new Pen((Brush)Application.Current.FindResource("Separator"), 1);

        const double left = 46, bottom = 18, top = 6, right = 8;
        double pw = w - left - right, ph = h - top - bottom;
        var t0 = _points[0].When.Ticks;
        var span = Math.Max(1, _points[^1].When.Ticks - t0);
        var lo = _points.Min(p => p.Value);
        var hi = _points.Max(p => p.Value);
        if (_step != null) hi = Math.Max(hi, _step.Max());
        if (hi - lo < 1e-9) { lo -= 1; hi += 1; }
        var pad = (hi - lo) * 0.08;
        lo -= pad; hi += pad;

        // Single point: centre it. Otherwise spread by time.
        double X(DateTime t) => _points.Count == 1 ? left + pw / 2 : left + (t.Ticks - t0) / (double)span * pw;
        double Y(double v) => top + (1 - (v - lo) / (hi - lo)) * ph;

        foreach (var f in new[] { 0.0, 0.5, 1.0 })
        {
            var v = lo + (hi - lo) * f;
            dc.DrawLine(sep, new Point(left, Y(v)), new Point(w - right, Y(v)));
            Label(dc, v.ToString(_format, CultureInfo.InvariantCulture), 2, Y(v) - 7, dim);
        }
        Label(dc, _points[0].When.ToString("yyyy-MM-dd"), left, h - 15, dim);
        var last = _points[^1].When.ToString("yyyy-MM-dd");
        Label(dc, last, w - right - 62, h - 15, dim);

        if (_step is { Count: > 0 } step && step.Count == _points.Count)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(_points[0].When), Y(step[0])), false, false);
                for (var i = 1; i < step.Count; i++)
                {
                    c.LineTo(new Point(X(_points[i].When), Y(step[i - 1])), true, false);
                    c.LineTo(new Point(X(_points[i].When), Y(step[i])), true, false);
                }
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(green, 1.5) { DashStyle = DashStyles.Dash }, g);
        }

        if (_points.Count > 1)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(_points[0].When), Y(_points[0].Value)), false, false);
                c.PolyLineTo(_points.Skip(1).Select(p => new Point(X(p.When), Y(p.Value))).ToList(), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, g);
        }

        var marked = new HashSet<int>(_marked);
        for (var i = 0; i < _points.Count; i++)
        {
            var pt = new Point(X(_points[i].When), Y(_points[i].Value));
            if (marked.Contains(i)) dc.DrawEllipse(green, new Pen(Brushes.White, 1), pt, 4, 4);
            else if (_points.Count <= 200) dc.DrawEllipse(accent, null, pt, 2.5, 2.5);
        }
    }

    static void Label(DrawingContext dc, string text, double x, double y, Brush brush) =>
        dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, brush, 1.0), new Point(x, y));
}
