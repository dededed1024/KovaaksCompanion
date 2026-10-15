using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>One figure of the comparison: the run's running value and the average of all runs, per second, with the formats of its axis and tooltip.</summary>
public sealed record CompareLine(string Name, Brush Brush, string AxisFormat, string TipFormat, bool IsScore, double[] Run, double[] Avg)
{
    public bool Percent => AxisFormat.EndsWith('%');
}

/// <summary>
/// One run's running figures against the average of all runs over run time. Every figure has its own colour and scale: the run is a curved line,
/// the average a dashed line of the same colour. The score scale sits on the left, the others in columns on the right. With a single figure
/// the gap between the lines is shaded green where the run is ahead and red where it is behind.
/// </summary>
public sealed class CompareChart : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static Typeface BoldFace => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const double Top = 10, Bottom = 22, Right = 12, AxisGap = 10;

    double[] _t = [];
    IReadOnlyList<CompareLine> _lines = [];
    int _hover = -1;
    double _left = 50, _pw = 100;

    public void Set(double[] seconds, IReadOnlyList<CompareLine> lines)
    {
        _t = seconds; _lines = lines;
        _hover = -1;
        InvalidateVisual();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush WithAlpha(Brush b, double alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A figure's axis: nice bounds around both of its curves, and the ticks between them.</summary>
    static (double Lo, double Hi, List<double> Ticks) Scale(CompareLine l)
    {
        var all = l.Run.Concat(l.Avg).Where(v => !double.IsNaN(v)).ToList();
        if (all.Count == 0) return (0, 1, [0, 1]);
        double dMin = all.Min(), dMax = all.Max();
        var pad = dMax - dMin < 1e-9 ? (l.Percent ? 0.05 : Math.Max(Math.Abs(dMax) * 0.1, 1)) : (dMax - dMin) * 0.06;
        double lo = dMin - pad, hi = dMax + pad;
        if (dMin >= 0 && lo < 0) lo = 0;
        var ticks = ScoreChart.NiceTicks(lo, hi, l.Percent);
        if (dMin >= 0) ticks = ticks.Where(t => t >= -1e-9).ToList();
        if (ticks.Count < 2) ticks = [lo, hi];
        return (ticks[0], ticks[^1], ticks);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var n = _t.Length;
        var dim = Res("Dim");
        if (n < 2 || (_lines.Count > 0 && _lines.All(l => l.Run.Concat(l.Avg).All(double.IsNaN))))
        {
            var ft = Text(L.T("No data"), 12, dim);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }

        var scales = _lines.ToDictionary(l => l, Scale);
        var score = _lines.FirstOrDefault(l => l.IsScore);
        var others = _lines.Where(l => !l.IsScore).ToList();
        var labelW = score is null ? 0 : scales[score].Ticks.Max(t => Text(t.ToString(score.AxisFormat, CultureInfo.InvariantCulture), 11, Brushes.White).Width);
        var left = labelW + 10;
        double Mid(CompareLine l) => (scales[l].Lo + scales[l].Hi) / 2;
        var colW = others.ToDictionary(l => l, l => new[] { scales[l].Lo, Mid(l), scales[l].Hi }.Max(v => Text(v.ToString(l.AxisFormat, CultureInfo.InvariantCulture), 10, Brushes.White).Width));
        var pw = Math.Max(1, w - left - Right - colW.Values.Sum(c => c + AxisGap));
        var ph = Math.Max(1, h - Top - Bottom);
        _left = left; _pw = pw;
        var plotRight = left + pw;
        var span = Math.Max(1e-9, _t[^1] - _t[0]);
        double X(int i) => left + (_t[i] - _t[0]) / span * pw;
        double Y(CompareLine l, double v) => Top + (1 - (v - scales[l].Lo) / (scales[l].Hi - scales[l].Lo)) * ph;

        var sepBrush = WithAlpha(Res("Separator"), 0.5);
        var dashed = new Pen(sepBrush, 1) { DashStyle = new DashStyle([2, 3], 0) };
        var solid = new Pen(sepBrush, 1);
        if (score is not null)
        {
            var sc = scales[score];
            foreach (var t in sc.Ticks)
            {
                var y = Math.Round(Y(score, t)) + 0.5;
                dc.DrawLine(Math.Abs(t - sc.Lo) < 1e-9 ? solid : dashed, new Point(left, y), new Point(plotRight, y));
                var ft = Text(t.ToString(score.AxisFormat, CultureInfo.InvariantCulture), 11, dim);
                dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
            }
        }
        else
            foreach (var f in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var y = Math.Round(Top + (1 - f) * ph) + 0.5;
                dc.DrawLine(f == 0 ? solid : dashed, new Point(left, y), new Point(plotRight, y));
            }

        // One label column per other figure, in its colour, right of the plot.
        var ax = plotRight + 8;
        foreach (var l in others)
        {
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                var ft = Text((scales[l].Lo + (scales[l].Hi - scales[l].Lo) * f).ToString(l.AxisFormat, CultureInfo.InvariantCulture), 10, l.Brush);
                var y = Top + (1 - f) * ph;
                dc.DrawText(ft, new Point(ax, f >= 1 ? y + 1 : f <= 0 ? y - ft.Height - 1 : y - ft.Height / 2));
            }
            ax += colW[l] + AxisGap;
        }

        // Time labels: about five, whole seconds.
        var labels = Math.Min(5, n);
        var prevEnd = double.NegativeInfinity;
        for (var k = 0; k < labels; k++)
        {
            var i = labels == 1 ? 0 : (int)Math.Round(k * (n - 1) / (double)(labels - 1));
            var ft = Text(Clock(_t[i]), 11, dim);
            var x = Math.Clamp(X(i) - ft.Width / 2, left, Math.Max(left, plotRight - ft.Width));
            if (x < prevEnd + 8) continue;
            dc.DrawText(ft, new Point(x, h - Bottom + 6));
            prevEnd = x + ft.Width;
        }

        if (_lines.Count == 1) DrawGap(dc, _lines[0], X, v => Y(_lines[0], v), n);

        foreach (var l in _lines)
        {
            var color = ((SolidColorBrush)l.Brush).Color;
            var avgPts = Enumerable.Range(0, n).Where(i => !double.IsNaN(l.Avg[i])).Select(i => new Point(X(i), Y(l, l.Avg[i]))).ToList();
            if (avgPts.Count > 1)
                dc.DrawGeometry(null, new Pen(WithAlpha(l.Brush, 0.7), 1.5) { DashStyle = new DashStyle([4, 3], 0), LineJoin = PenLineJoin.Round }, ChartPaths.Monotone(avgPts));
            var runPts = Enumerable.Range(0, n).Where(i => !double.IsNaN(l.Run[i])).Select(i => new Point(X(i), Y(l, l.Run[i]))).ToList();
            if (runPts.Count < 2) continue;
            if (_lines.Count == 1)
            {
                var grad = new LinearGradientBrush(Color.FromArgb(0x40, color.R, color.G, color.B), Color.FromArgb(0, color.R, color.G, color.B), 90);
                grad.Freeze();
                dc.DrawGeometry(grad, null, ChartPaths.MonotoneArea(runPts, Top + ph));
            }
            dc.DrawGeometry(null, new Pen(l.Brush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, ChartPaths.Monotone(runPts));
            dc.DrawEllipse(l.Brush, new Pen(Res("Bg"), 2), runPts[^1], 4, 4);
        }

        if (_hover >= 0 && _hover < n) DrawHover(dc, w, h, X(_hover), _hover, Y);
    }

    /// <summary>Gap between the run and the average: green where the run leads, red where it trails.</summary>
    void DrawGap(DrawingContext dc, CompareLine l, Func<int, double> X, Func<double, double> Y, int n)
    {
        var green = ((SolidColorBrush)Res("Green")).Color;
        var red = ((SolidColorBrush)Res("Red")).Color;
        Brush Tint(Color c) { var b = new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B)); b.Freeze(); return b; }
        var ahead = Tint(green);
        var behind = Tint(red);
        for (var i = 0; i < n - 1; i++)
        {
            if (double.IsNaN(l.Run[i]) || double.IsNaN(l.Avg[i]) || double.IsNaN(l.Run[i + 1]) || double.IsNaN(l.Avg[i + 1])) continue;
            double d0 = l.Run[i] - l.Avg[i], d1 = l.Run[i + 1] - l.Avg[i + 1];
            void Quad(double xa, double ya0, double yb0, double xb, double ya1, double yb1, bool up)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(xa, Y(ya0)), true, true);
                    c.LineTo(new Point(xb, Y(ya1)), false, false);
                    c.LineTo(new Point(xb, Y(yb1)), false, false);
                    c.LineTo(new Point(xa, Y(yb0)), false, false);
                }
                g.Freeze();
                dc.DrawGeometry(up ? ahead : behind, null, g);
            }
            double x0 = X(i), x1 = X(i + 1);
            if (d0 * d1 >= 0) Quad(x0, l.Run[i], l.Avg[i], x1, l.Run[i + 1], l.Avg[i + 1], d0 + d1 >= 0);
            else
            {
                // The lines cross inside this step: two triangles meeting at the crossing.
                var f = d0 / (d0 - d1);
                var xc = x0 + (x1 - x0) * f;
                var yc = l.Run[i] + (l.Run[i + 1] - l.Run[i]) * f;
                Quad(x0, l.Run[i], l.Avg[i], xc, yc, yc, d0 > 0);
                Quad(xc, yc, yc, x1, l.Run[i + 1], l.Avg[i + 1], d1 > 0);
            }
        }
    }

    void DrawHover(DrawingContext dc, double w, double h, double x, int i, Func<CompareLine, double, double> Y)
    {
        var fg = Res("Fg");
        dc.DrawLine(new Pen(WithAlpha(fg, 0.25), 1), new Point(x, Top), new Point(x, h - Bottom));
        var rows = new List<(Brush? Dot, FormattedText Name, FormattedText? Delta)> { (null, Text(Clock(_t[i]), 10, Res("Dim")), null) };
        foreach (var l in _lines)
        {
            double run = l.Run[i], avg = l.Avg[i];
            if (!double.IsNaN(avg)) dc.DrawEllipse(WithAlpha(l.Brush, 0.8), new Pen(Res("Bg"), 1), new Point(x, Y(l, avg)), 3.5, 3.5);
            if (!double.IsNaN(run)) dc.DrawEllipse(l.Brush, new Pen(Brushes.White, 1.5), new Point(x, Y(l, run)), 5, 5);
            string F(double v) => double.IsNaN(v) ? "–" : v.ToString(l.TipFormat, CultureInfo.InvariantCulture);
            FormattedText? delta = null;
            if (!double.IsNaN(run) && !double.IsNaN(avg))
            {
                var d = run - avg;
                var sign = d >= 0 ? "+" : "−";
                var text = l.Percent ? $"{sign}{Math.Abs(d) * 100:0.0} pt" : $"{sign}{Math.Abs(d):0.#}";
                delta = Text(text, 11, d >= 0 ? Res("Green") : Res("Red"), true);
            }
            rows.Add((l.Brush, Text($"{l.Name}  {F(run)} / {F(avg)}", 11, Res("Fg")), delta));
        }
        const double DeltaGap = 8;
        double bw = rows.Max(r => r.Name.Width + (r.Delta is null ? 0 : DeltaGap + r.Delta.Width) + (r.Dot is null ? 0 : 12)) + 16;
        double bh = rows.Sum(r => r.Name.Height) + 12;
        var bx = x + 12;
        if (bx + bw > w - 2) bx = x - 12 - bw;
        bx = Math.Max(2, bx);
        Brush fill = Res("Surface"), stroke = Res("Edge");
        dc.DrawRoundedRectangle(fill, new Pen(stroke, 1), new Rect(bx, Top, bw, bh), 8, 8);
        var ty = Top + 6;
        foreach (var r in rows)
        {
            var tx = bx + 8;
            if (r.Dot is not null) { dc.DrawEllipse(r.Dot, null, new Point(tx + 3.5, ty + r.Name.Height / 2), 3.5, 3.5); tx += 12; }
            dc.DrawText(r.Name, new Point(tx, ty));
            if (r.Delta is not null) dc.DrawText(r.Delta, new Point(tx + r.Name.Width + DeltaGap, ty));
            ty += r.Name.Height;
        }
    }

    static string Clock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_t.Length < 2 || ActualWidth <= 0) return;
        var f = Math.Clamp((e.GetPosition(this).X - _left) / Math.Max(1, _pw), 0, 1);
        var target = _t[0] + f * (_t[^1] - _t[0]);
        var idx = 0;
        for (var i = 1; i < _t.Length; i++) if (Math.Abs(_t[i] - target) < Math.Abs(_t[idx] - target)) idx = i;
        if (idx == _hover) return;
        _hover = idx;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }
}
