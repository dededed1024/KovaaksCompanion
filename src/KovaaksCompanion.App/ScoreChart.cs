using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

public enum ChartKind { Line, Bars }

/// <summary>A horizontal reference line (e.g. a tier threshold) drawn across the chart.</summary>
public sealed record ChartBand(double Value, string Label, Brush Brush);

/// <summary>Value per run in time order: line (or bars) with points evenly spaced by index, not by date, nice y ticks, optional moving average / PB markers, tier bands and a hover tooltip.</summary>
public sealed class ScoreChart : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static Typeface BoldFace => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const double Top = 10, Bottom = 22, Right = 12;
    IReadOnlyList<(DateTime When, double Value)> _points = [];
    IReadOnlyList<ChartBand> _bands = [];
    string _format = "0.#";
    ChartKind _kind;
    bool _overlays;
    int _hover = -1;

    public void Set(IReadOnlyList<(DateTime When, double Value)> points, string format = "0.#", ChartKind kind = ChartKind.Line, bool overlays = false, IReadOnlyList<ChartBand>? bands = null)
    {
        _points = points; _format = format; _kind = kind; _overlays = overlays; _bands = bands ?? []; _miniBrush = null;
        _hover = -1;
        InvalidateVisual();
    }

    Brush? _miniBrush;
    string _watermark = "";

    /// <summary>Card mode: only the line over a gradient (tier colour at the bottom, transparent on top), with the tier name as a card-high watermark behind it. No axes, bands, labels or hover.</summary>
    public void SetMini(IReadOnlyList<(DateTime When, double Value)> points, Brush tier, string tierName)
    {
        _points = points; _bands = []; _miniBrush = tier; _watermark = tierName; _hover = -1;
        IsHitTestVisible = false;
        InvalidateVisual();
    }

    void RenderMini(DrawingContext dc, double w, double h, Brush tier)
    {
        var c = ColorOf(tier);
        if (_watermark.Length > 0)
        {
            var face = new Typeface(new FontFamily("Segoe UI Black, Arial Black, Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
            var probe = new FormattedText(_watermark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 100, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var size = 100 * Math.Min(h * 1.7 / probe.Height, w * 1.1 / probe.Width);
            var mark = new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B));
            mark.Freeze();
            var ft = new FormattedText(_watermark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, mark, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(w - ft.Width + size * 0.15, h - ft.Baseline));
        }
        if (_points.Count < 2) return;
        var lo = _points.Min(p => p.Value);
        var hi = _points.Max(p => p.Value);
        var pad = Math.Max((hi - lo) * 0.08, 1e-6);
        lo -= pad; hi += pad;
        const double m = 6;
        var n = _points.Count;
        var pts = _points.Select((p, i) => new Point(i / (double)(n - 1) * w, m + (1 - (p.Value - lo) / (hi - lo)) * (h - 2 * m))).ToList();
        // 90° runs top-to-bottom: transparent on top, tier colour at the bottom.
        var grad = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(0x99, c.R, c.G, c.B), 90);
        grad.Freeze();
        dc.DrawGeometry(grad, null, ChartPaths.MonotoneArea(pts, h));
        dc.DrawGeometry(null, new Pen(tier, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, ChartPaths.Monotone(pts));
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush WithAlpha(Brush b, double alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    static readonly Brush LabelBack = Frozen(Color.FromArgb(0xB0, 0x14, 0x14, 0x16));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static List<double> NiceTicks(double lo, double hi, bool percent)
    {
        var range = Math.Max(hi - lo, 1e-9);
        var rough = range / 4;
        var mag = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = mag * 10;
        foreach (var m in percent ? new[] { 1.0, 2, 5, 10 } : [1.0, 2, 2.5, 5, 10])
            if (m * mag >= rough) { step = m * mag; break; }
        var first = Math.Floor(lo / step + 1e-9) * step;
        var last = Math.Ceiling(hi / step - 1e-9) * step;
        var ticks = new List<double>();
        for (var v = first; v <= last + step * 1e-6; v += step) ticks.Add(Math.Round(v / step) * step);
        return ticks;
    }

    (double Left, double Pw, double Ph, double Lo, double Hi, List<double> Ticks, List<ChartBand> Bands) Layout(double w, double h)
    {
        var dMin = _points.Min(p => p.Value);
        var dMax = _points.Max(p => p.Value);
        var bands = new List<ChartBand>();
        if (_bands.Count > 0)
        {
            bands.AddRange(_bands.Where(b => b.Value >= dMin && b.Value <= dMax));
            var above = _bands.Where(b => b.Value > dMax).OrderBy(b => b.Value).FirstOrDefault();
            var below = _bands.Where(b => b.Value < dMin).OrderByDescending(b => b.Value).FirstOrDefault();
            if (above != null) bands.Add(above);
            if (below != null) bands.Add(below);
        }
        var lo = Math.Min(dMin, bands.Count > 0 ? bands.Min(b => b.Value) : dMin);
        var hi = Math.Max(dMax, bands.Count > 0 ? bands.Max(b => b.Value) : dMax);
        var percent = _format.EndsWith('%');
        var tiny = hi - lo <= Math.Max(Math.Abs(hi), Math.Abs(lo)) * 1e-3 + 1e-9;
        var pad = tiny ? (percent ? 0.05 : Math.Max(Math.Abs(hi) * 0.1, 1e-6 + (Math.Abs(hi) < 1e-6 ? 1 : 0))) : (hi - lo) * 0.06;
        if (_kind == ChartKind.Bars) { lo = 0; hi += pad; }
        else { lo -= pad; hi += pad; }
        if (dMin >= 0 && lo < 0) lo = 0;
        var ticks = NiceTicks(lo, hi, percent);
        if (dMin >= 0) ticks = ticks.Where(t => t >= -1e-9).ToList();
        if (ticks.Count < 2) ticks = [lo, hi];
        lo = ticks[0]; hi = ticks[^1];
        var labelW = ticks.Max(t => Text(t.ToString(_format, CultureInfo.InvariantCulture), 11, Brushes.White).Width);
        var left = labelW + 10;
        return (left, Math.Max(1, w - left - Right), Math.Max(1, h - Top - Bottom), lo, hi, ticks, bands);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_miniBrush != null) { RenderMini(dc, w, h, _miniBrush); return; }
        var dim = Res("Dim");
        if (_points.Count == 0)
        {
            var ft = Text("No data", 12, dim);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }

        var accent = Res("Accent");
        var bg = Res("Bg");
        var fg = Res("Fg");
        var green = Res("Green");
        var pbBrush = WithAlpha(green, 0.95);
        var sepBrush = WithAlpha(Res("Separator"), 0.5);
        var (left, pw, ph, lo, hi, ticks, bands) = Layout(w, h);
        var n = _points.Count;
        double X(int i) => n == 1 ? left + pw / 2 : _kind == ChartKind.Bars ? left + (i + 0.5) / n * pw : left + i / (double)(n - 1) * pw;
        double Y(double v) => Top + (1 - (v - lo) / (hi - lo)) * ph;

        // Grid and y labels.
        var dashed = new Pen(sepBrush, 1) { DashStyle = new DashStyle([2, 3], 0) };
        var solid = new Pen(sepBrush, 1);
        foreach (var t in ticks)
        {
            var y = Math.Round(Y(t)) + 0.5;
            dc.DrawLine(Math.Abs(t - lo) < 1e-9 ? solid : dashed, new Point(left, y), new Point(w - Right, y));
            var ft = Text(t.ToString(_format, CultureInfo.InvariantCulture), 11, dim);
            dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
        }

        // X labels (line only: bars are one per day, labelled the same way).
        var span = _points[^1].When - _points[0].When;
        var fmt = span.TotalDays > 365 ? "MMM d, yy" : "MMM d";
        var labels = Math.Min(5, n);
        string? prevText = null;
        var prevEnd = double.NegativeInfinity;
        for (var k = 0; k < labels; k++)
        {
            var i = labels == 1 ? 0 : (int)Math.Round(k * (n - 1) / (double)(labels - 1));
            var txt = _points[i].When.ToString(fmt, CultureInfo.InvariantCulture);
            if (txt == prevText) continue;
            var ft = Text(txt, 11, dim);
            var x = Math.Clamp(X(i) - ft.Width / 2, left, Math.Max(left, w - Right - ft.Width));
            if (x < prevEnd + 8) continue;
            dc.DrawText(ft, new Point(x, h - Bottom + 6));
            prevText = txt; prevEnd = x + ft.Width;
        }

        if (_kind == ChartKind.Bars)
        {
            var slot = pw / n;
            var bw = Math.Max(1, Math.Min(18, slot * 0.7));
            var acb = ColorOf(accent);
            var fill = new LinearGradientBrush(acb, Color.FromArgb(0x59, acb.R, acb.G, acb.B), 90);
            fill.Freeze();
            var rad = Math.Min(3, bw / 2);
            var baseY = Y(lo);
            for (var i = 0; i < n; i++)
            {
                var y = Y(_points[i].Value);
                if (baseY - y < 1) continue;
                dc.DrawRoundedRectangle(fill, null, new Rect(X(i) - bw / 2, y, bw, baseY - y), rad, rad);
            }
        }
        else if (n > 1)
        {
            var pts = _points.Select((p, i) => new Point(X(i), Y(p.Value))).ToList();
            var line = ChartPaths.Monotone(pts);
            var area = ChartPaths.MonotoneArea(pts, Y(lo));
            var ac = ColorOf(accent);
            var grad = new LinearGradientBrush(Color.FromArgb(0x59, ac.R, ac.G, ac.B), Color.FromArgb(0, ac.R, ac.G, ac.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);

            // Bands sit under the line.
            DrawBands(dc, bands, left, w, Y);

            if (_overlays && n >= 6)
            {
                var win = Math.Clamp(n / 8, 3, 15);
                var avg = new List<Point>();
                for (var i = 0; i < n; i++)
                {
                    var from = Math.Max(0, i - win + 1);
                    var mean = _points.Skip(from).Take(i - from + 1).Average(p => p.Value);
                    avg.Add(new Point(X(i), Y(mean)));
                }
                dc.DrawGeometry(null, new Pen(WithAlpha(fg, 0.55), 1.5) { LineJoin = PenLineJoin.Round }, ChartPaths.Monotone(avg));
            }
            var strokeBrush = new LinearGradientBrush(Color.FromArgb(0x8C, ac.R, ac.G, ac.B), ac, 0);
            strokeBrush.Freeze();
            dc.DrawGeometry(null, new Pen(strokeBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

            if (_overlays)
            {
                var best = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    if (_points[i].Value > best)
                    {
                        if (i > 0) dc.DrawEllipse(pbBrush, null, pts[i], 3, 3);
                        best = _points[i].Value;
                    }
                }
            }
        }
        if (_kind == ChartKind.Bars || n == 1) DrawBands(dc, bands, left, w, Y);

        if (_kind == ChartKind.Line)
        {
            var lp = new Point(X(n - 1), Y(_points[^1].Value));
            dc.DrawEllipse(accent, new Pen(bg, 2), lp, 4, 4);
        }

        if (_hover >= 0 && _hover < n) DrawHover(dc, w, h, X(_hover), Y(_points[_hover].Value), accent, fg, left);
    }

    void DrawBands(DrawingContext dc, List<ChartBand> bands, double left, double w, Func<double, double> Y)
    {
        foreach (var b in bands)
        {
            var y = Math.Round(Y(b.Value)) + 0.5;
            dc.DrawLine(new Pen(WithAlpha(b.Brush, 0.7), 1) { DashStyle = new DashStyle([4, 4], 0) }, new Point(left, y), new Point(w - Right, y));
            var ft = Text(b.Label, 10, ChartPaths.TextTone(b.Brush), true);
            var rect = new Rect(left + 4, y - ft.Height - 2, ft.Width + 8, ft.Height + 2);
            dc.DrawRoundedRectangle(LabelBack, null, rect, 3, 3);
            dc.DrawText(ft, new Point(rect.X + 4, rect.Y + 1));
        }
    }

    void DrawHover(DrawingContext dc, double w, double h, double x, double y, Brush accent, Brush fg, double left)
    {
        var p = _points[_hover];
        if (_kind == ChartKind.Line)
        {
            dc.DrawLine(new Pen(WithAlpha(fg, 0.25), 1), new Point(x, Top), new Point(x, h - Bottom));
            dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5), new Point(x, y), 5, 5);
        }
        else dc.DrawLine(new Pen(WithAlpha(fg, 0.25), 1), new Point(x, Top), new Point(x, h - Bottom));

        var date = _kind == ChartKind.Line ? ClockFormat.DateClock(p.When) : p.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var t1 = Text(date, 10, Res("Dim"));
        var t2 = Text(p.Value.ToString(_format, CultureInfo.InvariantCulture), 13, fg, true);
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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var n = _points.Count;
        if (n == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (left, pw, _, _, _, _, _) = Layout(ActualWidth, ActualHeight);
        var mx = e.GetPosition(this).X;
        int idx;
        if (n == 1) idx = 0;
        else if (_kind == ChartKind.Bars) idx = (int)Math.Floor((mx - left) / pw * n);
        else idx = (int)Math.Round((mx - left) / pw * (n - 1));
        idx = Math.Clamp(idx, 0, n - 1);
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
