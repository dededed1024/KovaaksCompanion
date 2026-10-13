using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>One bar: the label under it (null for none), its height and the hover text.</summary>
public sealed record BarItem(string? Label, double Value, string Tip);

static class InsightDraw
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush WithAlpha(Brush b, double alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    public static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    public static FormattedText Text(Visual v, string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    /// <summary>The floating value box the other charts use.</summary>
    public static void Tooltip(DrawingContext dc, Visual v, double w, double h, double x, double y, string line1, string line2, Brush fg)
    {
        var t1 = Text(v, line1, 10, Res("Dim"));
        var t2 = Text(v, line2, 13, fg, true);
        double bw = Math.Max(t1.Width, t2.Width) + 16, bh = t1.Height + t2.Height + 12;
        var bx = x + 12;
        if (bx + bw > w - 2) bx = x - 12 - bw;
        bx = Math.Max(2, bx);
        var by = Math.Clamp(y - bh / 2, 2, Math.Max(2, h - bh - 2));
        var fill = new SolidColorBrush(Color.FromArgb(0xF0, 0x22, 0x22, 0x26));
        fill.Freeze();
        var stroke = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        stroke.Freeze();
        dc.DrawRoundedRectangle(fill, new Pen(stroke, 1), new Rect(bx, by, bw, bh), 8, 8);
        dc.DrawText(t1, new Point(bx + 8, by + 6));
        dc.DrawText(t2, new Point(bx + 8, by + 6 + t1.Height));
    }
}

/// <summary>Vertical bars with gradient fill and rounded tops, y ticks, labels under the bars and an optional vertical marker (the average of a histogram).</summary>
public sealed class BarChart : FrameworkElement
{
    const double Top = 10, Bottom = 22, Right = 8;
    IReadOnlyList<BarItem> _bars = [];
    string _format = "0.#";
    double? _mark;
    string _markLabel = "";
    bool _fromZero = true;
    int _hover = -1;

    /// <param name="fromZero">False starts the y axis just below the lowest bar, for values that differ by a few percent.</param>
    /// <param name="mark">Position of a vertical marker as a fraction (0..1) of the bar area, or null.</param>
    public void Set(IReadOnlyList<BarItem> bars, string format = "0.#", double? mark = null, string markLabel = "", bool fromZero = true)
    {
        _bars = bars; _format = format; _mark = mark; _markLabel = markLabel; _fromZero = fromZero; _hover = -1;
        InvalidateVisual();
    }

    (double Left, double Pw, double Ph, double Lo, double Hi, List<double> Ticks) Layout(double w, double h)
    {
        var max = Math.Max(1e-9, _bars.Max(b => b.Value));
        var min = _bars.Min(b => b.Value);
        var lo = _fromZero ? 0 : Math.Max(0, min - Math.Max(max - min, max * 0.02) * 0.8);
        var ticks = ScoreChart.NiceTicks(lo, max + (max - lo) * 0.06, _format.EndsWith('%')).Where(t => t >= -1e-9).ToList();
        if (ticks.Count < 2) ticks = [lo, max];
        var labelW = ticks.Max(t => InsightDraw.Text(this, t.ToString(_format, CultureInfo.InvariantCulture), 11, Brushes.White).Width);
        var left = labelW + 10;
        return (left, Math.Max(1, w - left - Right), Math.Max(1, h - Top - Bottom), ticks[0], ticks[^1], ticks);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dim = InsightDraw.Res("Dim");
        if (_bars.Count == 0)
        {
            var ft = InsightDraw.Text(this, L.T("No data"), 12, dim);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        var fg = InsightDraw.Res("Fg");
        var accent = InsightDraw.Res("Accent");
        var sep = InsightDraw.WithAlpha(InsightDraw.Res("Separator"), 0.5);
        var (left, pw, ph, lo, hi, ticks) = Layout(w, h);
        double Y(double v) => Top + (1 - (v - lo) / (hi - lo)) * ph;
        var dashed = new Pen(sep, 1) { DashStyle = new DashStyle([2, 3], 0) };
        foreach (var t in ticks)
        {
            var y = Math.Round(Y(t)) + 0.5;
            dc.DrawLine(Math.Abs(t - lo) < 1e-9 ? new Pen(sep, 1) : dashed, new Point(left, y), new Point(w - Right, y));
            var ft = InsightDraw.Text(this, t.ToString(_format, CultureInfo.InvariantCulture), 11, dim);
            dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
        }

        var n = _bars.Count;
        var slot = pw / n;
        var bw = Math.Max(1, Math.Min(48, slot * 0.78));
        var ac = InsightDraw.ColorOf(accent);
        var fill = new LinearGradientBrush(ac, Color.FromArgb(0x59, ac.R, ac.G, ac.B), 90);
        fill.Freeze();
        var hot = new LinearGradientBrush(Color.FromRgb((byte)Math.Min(255, ac.R + 40), (byte)Math.Min(255, ac.G + 40), (byte)Math.Min(255, ac.B + 40)), Color.FromArgb(0x8C, ac.R, ac.G, ac.B), 90);
        hot.Freeze();
        var baseY = Y(lo);
        var rad = Math.Min(4, bw / 2);
        var prevEnd = double.NegativeInfinity;
        for (var i = 0; i < n; i++)
        {
            var cx = left + (i + 0.5) * slot;
            var y = Y(_bars[i].Value);
            if (baseY - y >= 1) dc.DrawRoundedRectangle(i == _hover ? hot : fill, null, new Rect(cx - bw / 2, y, bw, baseY - y), rad, rad);
            if (_bars[i].Label is { } label)
            {
                var ft = InsightDraw.Text(this, label, 11, dim);
                var x = Math.Clamp(cx - ft.Width / 2, left, Math.Max(left, w - Right - ft.Width));
                if (x < prevEnd + 6) continue;
                dc.DrawText(ft, new Point(x, h - Bottom + 6));
                prevEnd = x + ft.Width;
            }
        }
        if (_mark is { } m)
        {
            var x = left + Math.Clamp(m, 0, 1) * pw;
            dc.DrawLine(new Pen(InsightDraw.WithAlpha(fg, 0.6), 1.5) { DashStyle = new DashStyle([3, 3], 0) }, new Point(x, Top), new Point(x, baseY));
            if (_markLabel.Length > 0)
            {
                var ft = InsightDraw.Text(this, _markLabel, 10.5, InsightDraw.WithAlpha(fg, 0.8), true);
                dc.DrawText(ft, new Point(Math.Clamp(x + 4, left, Math.Max(left, w - Right - ft.Width)), Top));
            }
        }
        if (_hover >= 0 && _hover < n)
        {
            var cx = left + (_hover + 0.5) * slot;
            InsightDraw.Tooltip(dc, this, w, h, cx, Y(_bars[_hover].Value), _bars[_hover].Tip, _bars[_hover].Value.ToString(_format, CultureInfo.InvariantCulture), fg);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_bars.Count == 0 || ActualWidth <= 0) return;
        var (left, pw, _, _, _, _) = Layout(ActualWidth, ActualHeight);
        var idx = (int)Math.Floor((e.GetPosition(this).X - left) / pw * _bars.Count);
        idx = idx < 0 || idx >= _bars.Count ? -1 : idx;
        if (idx == _hover) return;
        _hover = idx;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        Cursor = null;
        InvalidateVisual();
    }
}

/// <summary>Dots of (x, y) pairs with both axes ticked; the highest dot of each group is emphasised. Hover shows the nearest dot. Dots of one group share a <see cref="GroupColors"/> colour.</summary>
public sealed class ScatterChart : FrameworkElement
{
    const double Top = 10, Bottom = 22, Right = 12;
    public static readonly Color[] GroupColors =
    [
        Color.FromRgb(0x4C, 0x9B, 0xFF), Color.FromRgb(0xFF, 0x9F, 0x43), Color.FromRgb(0x3D, 0xD6, 0x8C),
        Color.FromRgb(0xE0, 0x5C, 0xC9), Color.FromRgb(0xF5, 0xD0, 0x4A),
    ];
    IReadOnlyList<(double X, double Y, string Tip)> _points = [];
    IReadOnlyList<int>? _groups;
    string _xFormat = "0.#", _yFormat = "0.#";
    int _hover = -1;

    /// <summary>Extra tooltip text for the group of the hovered dot (e.g. its DPI and a hint to click).</summary>
    public Func<int, string>? GroupNote { get; set; }
    /// <summary>Raised with the group index when a dot is clicked.</summary>
    public event Action<int>? GroupClick;

    public void Set(IReadOnlyList<(double X, double Y, string Tip)> points, string xFormat, string yFormat, IReadOnlyList<int>? groups = null)
    {
        _points = points; _xFormat = xFormat; _yFormat = yFormat; _groups = groups; _hover = -1;
        InvalidateVisual();
    }

    Color DotColor(int i, Color fallback) => _groups is { } g && i < g.Count ? GroupColors[g[i] % GroupColors.Length] : fallback;

    (double Left, double Pw, double Ph, double XLo, double XHi, double YLo, double YHi, List<double> XTicks, List<double> YTicks) Layout(double w, double h)
    {
        double xMin = _points.Min(p => p.X), xMax = _points.Max(p => p.X), yMax = _points.Max(p => p.Y);
        // The axis starts at the lowest group median, not the lowest dot: one bad run would squash everything near the top flat. Dots below are drawn on the axis line.
        var yMin = Enumerable.Range(0, _points.Count).GroupBy(i => _groups is { } gl && i < gl.Count ? gl[i] : 0)
            .Min(g => { var ys = g.Select(i => _points[i].Y).OrderBy(v => v).ToList(); return ys[ys.Count / 2]; });
        double Pad(double lo, double hi) => hi - lo < 1e-9 ? Math.Max(Math.Abs(hi) * 0.05, 1e-3) : (hi - lo) * 0.08;
        var xt = ScoreChart.NiceTicks(xMin - Pad(xMin, xMax), xMax + Pad(xMin, xMax), _xFormat.EndsWith('%'));
        var yt = ScoreChart.NiceTicks(yMin - Pad(yMin, yMax), yMax + Pad(yMin, yMax), _yFormat.EndsWith('%'));
        if (yMin >= 0) yt = yt.Where(t => t >= -1e-9).ToList();
        if (xMin >= 0) xt = xt.Where(t => t >= -1e-9).ToList();
        if (xt.Count < 2) xt = [xMin, xMax + 1e-3];
        if (yt.Count < 2) yt = [yMin, yMax + 1e-3];
        var labelW = yt.Max(t => InsightDraw.Text(this, t.ToString(_yFormat, CultureInfo.InvariantCulture), 11, Brushes.White).Width);
        var left = labelW + 10;
        return (left, Math.Max(1, w - left - Right), Math.Max(1, h - Top - Bottom), xt[0], xt[^1], yt[0], yt[^1], xt, yt);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dim = InsightDraw.Res("Dim");
        if (_points.Count == 0)
        {
            var ft = InsightDraw.Text(this, L.T("No data"), 12, dim);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        var fg = InsightDraw.Res("Fg");
        var accent = InsightDraw.Res("Accent");
        var sep = InsightDraw.WithAlpha(InsightDraw.Res("Separator"), 0.5);
        var (left, pw, ph, xLo, xHi, yLo, yHi, xt, yt) = Layout(w, h);
        double X(double v) => left + (v - xLo) / (xHi - xLo) * pw;
        double Y(double v) => Top + (1 - (Math.Max(v, yLo) - yLo) / (yHi - yLo)) * ph;
        var dashed = new Pen(sep, 1) { DashStyle = new DashStyle([2, 3], 0) };
        foreach (var t in yt)
        {
            var y = Math.Round(Y(t)) + 0.5;
            dc.DrawLine(Math.Abs(t - yLo) < 1e-9 ? new Pen(sep, 1) : dashed, new Point(left, y), new Point(w - Right, y));
            var ft = InsightDraw.Text(this, t.ToString(_yFormat, CultureInfo.InvariantCulture), 11, dim);
            dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
        }
        var prevEnd = double.NegativeInfinity;
        foreach (var t in xt)
        {
            var ft = InsightDraw.Text(this, t.ToString(_xFormat, CultureInfo.InvariantCulture), 11, dim);
            var x = Math.Clamp(X(t) - ft.Width / 2, left, Math.Max(left, w - Right - ft.Width));
            if (x < prevEnd + 8) continue;
            dc.DrawText(ft, new Point(x, h - Bottom + 6));
            prevEnd = x + ft.Width;
        }
        var ac = InsightDraw.ColorOf(accent);
        var ring = new Pen(InsightDraw.Res("Bg"), 1);
        // The highest dot of each group is emphasised.
        var bestOf = new Dictionary<int, int>();
        for (var i = 0; i < _points.Count; i++)
        {
            var key = _groups is { } gs && i < gs.Count ? gs[i] : 0;
            if (!bestOf.TryGetValue(key, out var j) || _points[i].Y > _points[j].Y) bestOf[key] = i;
        }
        var bests = bestOf.Values.ToHashSet();
        // A curve through those best dots, left to right, shows how the best score moves with sensitivity.
        var ridge = bests.Select(i => new Point(X(_points[i].X), Y(_points[i].Y))).OrderBy(q => q.X).ToList();
        ridge = ridge.Where((q, k) => k == 0 || q.X - ridge[k - 1].X > 0.5).ToList();
        if (ridge.Count >= 2)
        {
            var fillTop = Color.FromArgb(0x38, ac.R, ac.G, ac.B);
            var area = new LinearGradientBrush(fillTop, Color.FromArgb(0, ac.R, ac.G, ac.B), 90);
            area.Freeze();
            dc.DrawGeometry(area, null, ChartPaths.MonotoneArea(ridge, Top + ph));
            var line = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, ac.R, ac.G, ac.B)), 1.5) { LineJoin = PenLineJoin.Round };
            line.Freeze();
            dc.DrawGeometry(null, line, ChartPaths.Monotone(ridge));
        }
        for (var i = 0; i < _points.Count; i++)
        {
            var p = new Point(X(_points[i].X), Y(_points[i].Y));
            var c = DotColor(i, ac);
            if (bests.Contains(i))
            {
                var halo = new RadialGradientBrush(Color.FromArgb(0x60, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
                halo.Freeze();
                var solid = new SolidColorBrush(c); solid.Freeze();
                dc.DrawEllipse(halo, null, p, 9, 9);
                dc.DrawEllipse(solid, ring, p, 4, 4);
            }
            else
            {
                var fill = new SolidColorBrush(Color.FromArgb(0xC8, c.R, c.G, c.B)); fill.Freeze();
                dc.DrawEllipse(fill, ring, p, 3.5, 3.5);
            }
        }
        if (_hover >= 0 && _hover < _points.Count)
        {
            var p = _points[_hover];
            var pt = new Point(X(p.X), Y(p.Y));
            // The sensitivity axis line through the hovered dot.
            dc.DrawLine(new Pen(InsightDraw.WithAlpha(fg, 0.35), 1) { DashStyle = new DashStyle([3, 3], 0) }, new Point(pt.X, Top), new Point(pt.X, Top + ph));
            var hc = new SolidColorBrush(DotColor(_hover, ac)); hc.Freeze();
            dc.DrawEllipse(hc, new Pen(Brushes.White, 1.5), pt, 5, 5);
            InsightDraw.Tooltip(dc, this, w, h, pt.X, pt.Y, GroupNote is { } note && _groups is { } gl && _hover < gl.Count ? p.Tip + " · " + note(gl[_hover]) : p.Tip, p.Y.ToString(_yFormat, CultureInfo.InvariantCulture) + " · " + p.X.ToString(_xFormat, CultureInfo.InvariantCulture), fg);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_points.Count == 0 || ActualWidth <= 0) return;
        var (left, pw, ph, xLo, xHi, yLo, yHi, _, _) = Layout(ActualWidth, ActualHeight);
        var m = e.GetPosition(this);
        var best = -1; var bestD = 18.0 * 18.0;
        for (var i = 0; i < _points.Count; i++)
        {
            var px = left + (_points[i].X - xLo) / (xHi - xLo) * pw;
            var py = Top + (1 - (Math.Max(_points[i].Y, yLo) - yLo) / (yHi - yLo)) * ph;
            var d = (px - m.X) * (px - m.X) + (py - m.Y) * (py - m.Y);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best == _hover) return;
        _hover = best;
        Cursor = best >= 0 && GroupClick is not null ? Cursors.Hand : null;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover >= 0 && _groups is { } g && _hover < g.Count) GroupClick?.Invoke(g[_hover]);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }
}
