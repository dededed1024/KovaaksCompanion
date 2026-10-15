using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

public enum ChartKind { Line, Bars }

/// <summary>A horizontal reference line (e.g. a tier threshold) drawn across the chart.</summary>
public sealed record ChartBand(double Value, string Label, Brush Brush);

/// <summary>One more figure per run drawn over the score line, each on its own scale. <see cref="Values"/> lines up with the chart's points; NaN where a run has no value.</summary>
public sealed record ChartLine(string Name, Brush Brush, string Format, IReadOnlyList<double> Values);

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
        _all = points; _format = format; _kind = kind; _overlays = overlays; _bands = bands ?? []; _miniBrush = null;
        _manual = false;
        ApplyWindow();
        AttachVisuals(false);
        _hover = -1;
        InvalidateVisual();
    }

    IReadOnlyList<(DateTime When, double Value)> _all = [];
    int _off, _len;
    bool _manual;
    const int AutoMax = 70, ZoomMin = 5;

    /// <summary>Line mode: only a window of the runs is shown. It starts on the recent trend; <see cref="SetView"/> moves and resizes it.</summary>
    public bool Zoomable { get; set; }

    /// <summary>Fewest runs the window can be narrowed to.</summary>
    public const int MinWindow = ZoomMin;

    /// <summary>Raised after the window changed.</summary>
    public event Action? ViewChanged;

    public int Total => _all.Count;

    /// <summary>First run shown and how many are shown.</summary>
    public (int Off, int Len) View => (_off, _len);

    /// <summary>True when the window can be moved at all.</summary>
    public bool CanZoom => Zoomable && _kind == ChartKind.Line && _all.Count > ZoomMin;

    /// <summary>Last N runs to show: the recent runs inside the current tier and the one below it, at most <see cref="AutoMax"/>. All runs when there are no more than that.</summary>
    int AutoWindow()
    {
        var p = _all;
        var n = p.Count;
        if (n <= AutoMax) return n;
        var t = _bands.Count(b => b.Value <= p[n - 1].Value);
        if (t < 2) return AutoMax;
        var floor = _bands.OrderBy(b => b.Value).ElementAt(t - 2).Value;
        var w = 0;
        while (w < n && w < AutoMax && p[n - 1 - w].Value >= floor) w++;
        return Math.Max(w, Math.Min(ZoomMin, n));
    }

    void ApplyWindow()
    {
        var n = _all.Count;
        if (!CanZoom) { _points = _all; _off = 0; _len = n; return; }
        if (!_manual) { _len = AutoWindow(); _off = n - _len; }
        _len = Math.Clamp(_len, Math.Min(ZoomMin, n), n);
        _off = Math.Clamp(_off, 0, n - _len);
        _points = _len == n ? _all : _all.Skip(_off).Take(_len).ToList();
    }

    /// <summary>Shows <paramref name="len"/> runs from run <paramref name="off"/>.</summary>
    public void SetView(int off, int len)
    {
        if (!CanZoom) return;
        _manual = true;
        _off = off; _len = len;
        ApplyWindow();
        _hover = -1;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    /// <summary>Back to the automatic window.</summary>
    public void ResetView()
    {
        if (!CanZoom) return;
        _manual = false;
        ApplyWindow();
        _hover = -1;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    IReadOnlyList<ChartLine> _lines = [];
    ISet<string> _shown = new HashSet<string>();

    /// <summary>The name the score line goes by in the shown set; the score is drawn only while this is in it (always, when there are no extra lines).</summary>
    public string ScoreName { get; set; } = "Score";

    bool ScoreOn => _lines.Count == 0 || _shown.Contains(ScoreName);

    /// <summary>The scale of one extra line: its own value range and the width of its label column on the right.</summary>
    sealed record Axis(ChartLine Line, double Lo, double Hi, double ColW);

    List<Axis> _axes = [];
    const double AxisGap = 10, AxisLabelSize = 10;

    List<Axis> ComputeAxes()
    {
        var res = new List<Axis>();
        if (_kind != ChartKind.Line) return res;
        var n = _points.Count;
        foreach (var line in _lines)
        {
            if (!_shown.Contains(line.Name) || line.Values.Count != _all.Count) continue;
            var vals = Enumerable.Range(_off, n).Select(i => line.Values[i]).Where(v => !double.IsNaN(v)).ToList();
            if (vals.Count == 0) continue;
            double min = vals.Min(), max = vals.Max();
            var percent = line.Format.EndsWith('%');
            var pad = max - min < 1e-9 ? (percent ? 0.05 : Math.Max(Math.Abs(max) * 0.1, 1)) : (max - min) * 0.06;
            double lo = min - pad, hi = max + pad;
            if (min >= 0 && lo < 0) lo = 0;
            var ticks = NiceTicks(lo, hi, percent);
            if (min >= 0) ticks = ticks.Where(t => t >= -1e-9).ToList();
            if (ticks.Count < 2) ticks = [lo, hi];
            lo = ticks[0]; hi = ticks[^1];
            var colW = new[] { lo, (lo + hi) / 2, hi }.Max(v => Text(v.ToString(line.Format, CultureInfo.InvariantCulture), AxisLabelSize, Brushes.White).Width);
            res.Add(new Axis(line, lo, hi, colW));
        }
        return res;
    }

    /// <summary>The extra figures to draw over the score line: only those named in <paramref name="shown"/>.</summary>
    public void SetLines(IReadOnlyList<ChartLine> lines, ISet<string> shown)
    {
        _lines = lines; _shown = shown;
        InvalidateVisual();
    }

    double _miniTextCap = 200, _miniStackAbove = double.PositiveInfinity;
    /// <summary>Card mode: the watermark is sized from at most this card height.</summary>
    public double MiniTextCap { get => _miniTextCap; set { if (Math.Abs(_miniTextCap - value) < 0.01) return; _miniTextCap = value; InvalidateVisual(); } }
    /// <summary>Card mode: above this card height the watermark repeats upward to fill the card.</summary>
    public double MiniStackAbove { get => _miniStackAbove; set { if (_miniStackAbove.Equals(value)) return; _miniStackAbove = value; InvalidateVisual(); } }
    bool _darkMark;
    /// <summary>Card mode: the watermark is drawn in a darkened tier color, for a card on a light background where the tier color alone disappears.</summary>
    public bool DarkMark { get => _darkMark; set { if (_darkMark == value) return; _darkMark = value; InvalidateVisual(); } }
    bool _marquee;
    /// <summary>Card mode: the watermark always repeats and drifts downward in a loop. Turn off for a static image.</summary>
    public bool Marquee
    {
        get => _marquee;
        set
        {
            if (_marquee == value) return;
            _marquee = value;
            InvalidateVisual();
        }
    }

    // The watermark lives in its own retained visual so the drift is a transform animation run by the compositor, not a redraw per frame.
    // Visual order: watermark (clipped to the card) below, line and tags above. OnRender then only paints the hit-test rect.
    readonly ContainerVisual _markClip = new();
    readonly DrawingVisual _mark = new();
    readonly DrawingVisual _lineVis = new();
    readonly TranslateTransform _markShift = new();
    bool _visualsAttached;
    double _animPitch = -1;

    protected override int VisualChildrenCount => _visualsAttached ? 2 : 0;

    protected override Visual GetVisualChild(int index) => index == 0 ? _markClip : _lineVis;

    void AttachVisuals(bool attach)
    {
        if (_visualsAttached == attach) return;
        _visualsAttached = attach;
        if (attach) { AddVisualChild(_markClip); AddVisualChild(_lineVis); }
        else { RemoveVisualChild(_markClip); RemoveVisualChild(_lineVis); StopDrift(); }
    }

    void StartDrift(double pitch)
    {
        if (Math.Abs(_animPitch - pitch) < 0.01) return;
        _animPitch = pitch;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, pitch, TimeSpan.FromSeconds(7)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        // Locked to 60 fps. The animation is clock-based (position = elapsed time / period), so a dropped frame skips ahead instead of slowing the drift, and the compositor interpolates the sub-pixel position of every frame.
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, 60);
        _markShift.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    void StopDrift()
    {
        if (_animPitch < 0) return;
        _animPitch = -1;
        _markShift.BeginAnimation(TranslateTransform.YProperty, null);
    }

    static readonly Typeface MarkFace = new(new FontFamily("Segoe UI Black, Arial Black, Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);

    public ScoreChart()
    {
        // Display mode + ClearType snap glyphs to whole pixels, which makes the drifting watermark step. Ideal + grayscale lets it move smoothly.
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        _mark.Transform = _markShift;
        _markClip.Children.Add(_mark);
        Loaded += (_, _) => InvalidateVisual();
        Unloaded += (_, _) => StopDrift();
    }

    Brush? _miniBrush;
    double _miniLineDown;
    /// <summary>Card mode: pushes the line (not the watermark or tag) this many px down from the top.</summary>
    public double MiniLineDown { get => _miniLineDown; set { if (Math.Abs(_miniLineDown - value) < 0.01) return; _miniLineDown = value; InvalidateVisual(); } }
    string _watermark = "";
    bool _complete;
    string? _progress;

    /// <summary>Card mode: only the line over a gradient (tier colour at the bottom, transparent on top), with the tier name as a card-high watermark behind it. No axes, bands, labels or hover.</summary>
    public void SetMini(IReadOnlyList<(DateTime When, double Value)> points, Brush tier, string tierName, bool complete = false, string? progress = null)
    {
        _points = _all = points; _off = 0; _bands = []; _miniBrush = tier; _watermark = tierName; _complete = complete; _progress = progress; _hover = -1;
        IsHitTestVisible = false;
        AttachVisuals(true);
        InvalidateVisual();
    }

    /// <summary>Watermark rows into the retained visual: one row sits on the card's bottom edge, more stack upward. The marquee animates the whole visual down by one pitch, so the loop is seamless.</summary>
    void RenderMark(double w, double h, Color c)
    {
        using var dc = _mark.RenderOpen();
        _markClip.Clip = new RectangleGeometry(new Rect(0, 0, w, h));
        if (_watermark.Length == 0) { StopDrift(); return; }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = new FormattedText(_watermark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, 100, Brushes.White, dpi);
        var he = Math.Min(h, MiniTextCap);
        var size = 100 * he * 1.7 / probe.Height;
        // Dark mark: each row runs from a darkened tier color at its top to almost the plain tier color at its bottom.
        Brush mark = _darkMark
            ? new LinearGradientBrush(Color.FromArgb(0xA0, (byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55)), Color.FromArgb(0xC0, (byte)(c.R * 0.95), (byte)(c.G * 0.95), (byte)(c.B * 0.95)), new Point(0, 0), new Point(0, 1))
            : new SolidColorBrush(Color.FromArgb(0x80, c.R, c.G, c.B));
        mark.Freeze();
        var ft = new FormattedText(_watermark, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, size, mark, dpi);
        var x = w - ft.Width + size * 0.15;
        var pitch = he * 0.95;
        dc.DrawText(ft, new Point(x, h - ft.Baseline));
        // A card taller than the threshold repeats the name upward, the top row cut off by the card edge.
        if (_marquee || h > MiniStackAbove)
            for (var y = h - pitch; y > -pitch; y -= pitch)
                dc.DrawText(ft, new Point(x, y - ft.Baseline));
        _pitch = pitch;
        if (_manualDrift) { StopDrift(); _markShift.Y = _phase * pitch; }
        else if (_marquee && IsLoaded) StartDrift(pitch);
        else StopDrift();
    }

    bool _manualDrift;
    double _phase, _pitch;

    /// <summary>Frame-by-frame capture: stops the clock-driven drift so <see cref="SetDriftPhase"/> sets the watermark position.</summary>
    public void BeginManualDrift()
    {
        _manualDrift = true;
        _phase = 0;
        StopDrift();
        _markShift.Y = 0;
    }

    /// <summary>Watermark position as a fraction of one loop (0..1); 1 equals 0.</summary>
    public void SetDriftPhase(double fraction)
    {
        _phase = fraction;
        if (_pitch > 0) _markShift.Y = fraction * _pitch;
    }

    public void EndManualDrift()
    {
        _manualDrift = false;
        _phase = 0;
        _markShift.Y = 0;
        InvalidateVisual();
    }

    /// <summary>Bottom right tag: COMPLETE once the top tier is reached, else the progress number plus a "%" at 3/4 size on the same baseline. Null when there is nothing to show.</summary>
    (FormattedText Text, Point TextAt, FormattedText? Pct, Point PctAt)? TagLayout(double w, double h)
    {
        var tagText = _watermark.Length > 0 && _complete ? L.T("Complete").ToUpperInvariant() : _progress;
        if (string.IsNullOrEmpty(tagText)) return null;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var probe = new FormattedText(_watermark.Length > 0 ? _watermark : "A", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, 100, Brushes.White, dpi);
        var tagBrush = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)); tagBrush.Freeze();
        // Scales with the card height, up to twice the watermark's height cap.
        var tagSize = 100 * Math.Min(h, MiniTextCap * 2) * 1.7 / probe.Height / 4.5;
        var tag = new FormattedText(tagText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, tagSize, tagBrush, dpi);
        FormattedText? pct = null;
        var width = tag.Width;
        if (tagText == _progress)
        {
            pct = new FormattedText("%", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, MarkFace, tagSize * 0.75, tagBrush, dpi);
            width += pct.Width;
        }
        var x0 = w - width - 8;
        var at = new Point(x0, h - tag.Baseline - 6);
        var pctAt = pct == null ? default : new Point(x0 + tag.Width, h - pct.Baseline - 6);
        return (tag, at, pct, pctAt);
    }

    void RenderMini(DrawingContext dc, double w, double h, Brush tier)
    {
        var c = ColorOf(tier);
        // Drawn last, so the line's gradient never tints it (a card without a line would otherwise show it brighter).
        void DrawTag()
        {
            if (TagLayout(w, h) is not { } t) return;
            dc.DrawText(t.Text, t.TextAt);
            if (t.Pct != null) dc.DrawText(t.Pct, t.PctAt);
        }
        if (_points.Count < 2) { DrawTag(); return; }
        var lo = _points.Min(p => p.Value);
        var hi = _points.Max(p => p.Value);
        var pad = Math.Max((hi - lo) * 0.08, 1e-6);
        lo -= pad; hi += pad;
        const double m = 6;
        var top = m + _miniLineDown;
        var n = _points.Count;
        var pts = _points.Select((p, i) => new Point(i / (double)(n - 1) * w, top + (1 - (p.Value - lo) / (hi - lo)) * (h - top - m))).ToList();
        // 90° runs top-to-bottom: transparent on top, tier colour at the bottom.
        var grad = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), Color.FromArgb(0x99, c.R, c.G, c.B), 90);
        grad.Freeze();
        dc.DrawGeometry(grad, null, ChartPaths.MonotoneArea(pts, h));
        var line = ChartPaths.Monotone(pts);
        // Static glow: wide translucent strokes layered under the line.
        foreach (var (thick, alpha) in new[] { (9.0, 0x0A), (6.0, 0x14), (4.0, 0x20) })
        {
            var glow = new Pen(Frozen(Color.FromArgb((byte)alpha, c.R, c.G, c.B)), thick) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            glow.Freeze();
            dc.DrawGeometry(null, glow, line);
        }
        dc.DrawGeometry(null, new Pen(tier, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        // Halo on the latest point.
        var last = pts[^1];
        var halo = new RadialGradientBrush(Color.FromArgb(0x60, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
        halo.Freeze();
        dc.DrawEllipse(halo, null, last, 8, 8);
        dc.DrawEllipse(tier, null, last, 2.5, 2.5);
        DrawTag();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush WithAlpha(Brush b, double alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }


    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    internal static List<double> NiceTicks(double lo, double hi, bool percent)
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
        var labelW = ScoreOn ? ticks.Max(t => Text(t.ToString(_format, CultureInfo.InvariantCulture), 11, Brushes.White).Width) : 0;
        var left = labelW + 10;
        _axes = ComputeAxes();
        var axesW = _axes.Sum(a => a.ColW + AxisGap);
        return (left, Math.Max(1, w - left - Right - axesW), Math.Max(1, h - Top - Bottom), lo, hi, ticks, ScoreOn ? bands : new List<ChartBand>());
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_miniBrush != null)
        {
            RenderMark(w, h, ColorOf(_miniBrush));
            using var ldc = _lineVis.RenderOpen();
            RenderMini(ldc, w, h, _miniBrush);
            return;
        }
        var dim = Res("Dim");
        if (_points.Count == 0)
        {
            var ft = Text(L.T("No data"), 12, dim);
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
        var plotRight = left + pw;
        var scoreOn = ScoreOn;
        if (scoreOn)
            foreach (var t in ticks)
            {
                var y = Math.Round(Y(t)) + 0.5;
                dc.DrawLine(Math.Abs(t - lo) < 1e-9 ? solid : dashed, new Point(left, y), new Point(plotRight, y));
                var ft = Text(t.ToString(_format, CultureInfo.InvariantCulture), 11, dim);
                dc.DrawText(ft, new Point(left - 8 - ft.Width, y - ft.Height / 2));
            }
        else
            foreach (var f in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
            {
                var y = Math.Round(Top + (1 - f) * ph) + 0.5;
                dc.DrawLine(f == 0 ? solid : dashed, new Point(left, y), new Point(plotRight, y));
            }

        // One label column per extra line, in its colour, right of the plot.
        var ax = plotRight + 8;
        foreach (var a in _axes)
        {
            foreach (var f in new[] { 0.0, 0.5, 1.0 })
            {
                var ft = Text((a.Lo + (a.Hi - a.Lo) * f).ToString(a.Line.Format, CultureInfo.InvariantCulture), AxisLabelSize, a.Line.Brush);
                var y = Top + (1 - f) * ph;
                var ty = f >= 1 ? y + 1 : f <= 0 ? y - ft.Height - 1 : y - ft.Height / 2;
                dc.DrawText(ft, new Point(ax, ty));
            }
            ax += a.ColW + AxisGap;
        }

        // X labels (line only: bars are one per day, labelled the same way).
        var span = _points[^1].When - _points[0].When;
        var yearly = span.TotalDays > 365;
        string Label(DateTime t) => L.Code == "en" ? t.ToString(yearly ? "MMM d, yy" : "MMM d", L.DateCulture) : yearly ? L.ShortDate(t) : L.MonthDay(t);
        var labels = Math.Min(5, n);
        string? prevText = null;
        var prevEnd = double.NegativeInfinity;
        for (var k = 0; k < labels; k++)
        {
            var i = labels == 1 ? 0 : (int)Math.Round(k * (n - 1) / (double)(labels - 1));
            var txt = Label(_points[i].When);
            if (txt == prevText) continue;
            var ft = Text(txt, 11, dim);
            var x = Math.Clamp(X(i) - ft.Width / 2, left, Math.Max(left, plotRight - ft.Width));
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
        else if (n > 1 && scoreOn)
        {
            var pts = _points.Select((p, i) => new Point(X(i), Y(p.Value))).ToList();
            var line = ChartPaths.Monotone(pts);
            var area = ChartPaths.MonotoneArea(pts, Y(lo));
            var ac = ColorOf(accent);
            var grad = new LinearGradientBrush(Color.FromArgb(0x59, ac.R, ac.G, ac.B), Color.FromArgb(0, ac.R, ac.G, ac.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, area);

            // Bands sit under the line.
            DrawBands(dc, bands, left, plotRight + Right, Y);

            var strokeBrush = new LinearGradientBrush(Color.FromArgb(0x8C, ac.R, ac.G, ac.B), ac, 0);
            strokeBrush.Freeze();
            dc.DrawGeometry(null, new Pen(strokeBrush, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

            // Every run is a dot, whatever the density.
            var dotPen = new Pen(bg, 1);
            foreach (var pt in pts) dc.DrawEllipse(accent, dotPen, pt, 2.5, 2.5);

            if (_overlays)
            {
                var best = double.NegativeInfinity;
                for (var i = 0; i < _off; i++) best = Math.Max(best, _all[i].Value);
                for (var i = 0; i < n; i++)
                {
                    if (_points[i].Value > best)
                    {
                        if (i + _off > 0) dc.DrawEllipse(pbBrush, null, pts[i], 3, 3);
                        best = _points[i].Value;
                    }
                }
            }
        }
        if (_kind == ChartKind.Bars || n == 1) DrawBands(dc, bands, left, plotRight + Right, Y);

        foreach (var a in _axes) DrawLine(dc, a, left, pw, ph, n);

        if (_kind == ChartKind.Line && scoreOn)
        {
            var lp = new Point(X(n - 1), Y(_points[^1].Value));
            dc.DrawEllipse(accent, new Pen(bg, 2), lp, 4, 4);
        }

        if (_hover >= 0 && _hover < n)
        {
            var extra = new List<(Brush Brush, string Text)>();
            foreach (var a in _axes)
            {
                var v = a.Line.Values[_off + _hover];
                if (double.IsNaN(v)) continue;
                extra.Add((a.Line.Brush, a.Line.Name + "  " + v.ToString(a.Line.Format, CultureInfo.InvariantCulture)));
                dc.DrawEllipse(a.Line.Brush, new Pen(bg, 1), new Point(X(_hover), LineY(a, v, ph)), 3.5, 3.5);
            }
            DrawHover(dc, w, h, X(_hover), scoreOn ? Y(_points[_hover].Value) : Top + ph / 2, accent, fg, left, extra, scoreOn);
        }
    }

    /// <summary>Y of a value on its own line's scale.</summary>
    static double LineY(Axis a, double v, double ph)
    {
        var frac = a.Hi - a.Lo < 1e-12 ? 0.5 : (v - a.Lo) / (a.Hi - a.Lo);
        return Top + ph * (1 - frac);
    }

    void DrawLine(DrawingContext dc, Axis a, double left, double pw, double ph, int n)
    {
        var line = a.Line;
        double X(int i) => n == 1 ? left + pw / 2 : left + i / (double)(n - 1) * pw;
        var pen = new Pen(line.Brush, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var run = new List<Point>();
        void Flush()
        {
            if (run.Count == 1) dc.DrawEllipse(line.Brush, null, run[0], 2, 2);
            else if (run.Count > 1) dc.DrawGeometry(null, pen, ChartPaths.Monotone(run));
            run.Clear();
        }
        // A run without a value leaves a gap; the estimate across it is a dashed line from the last known value to the next.
        var guess = new Pen(line.Brush, 1.4) { DashStyle = new DashStyle([3, 3], 0), StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        Point? lastKnown = null;
        var gap = false;
        for (var i = 0; i < n; i++)
        {
            var v = line.Values[_off + i];
            if (double.IsNaN(v)) { Flush(); gap = true; continue; }
            var pt = new Point(X(i), LineY(a, v, ph));
            if (gap && lastKnown is { } from) dc.DrawLine(guess, from, pt);
            gap = false;
            lastKnown = pt;
            run.Add(pt);
        }
        Flush();
    }

    void DrawBands(DrawingContext dc, List<ChartBand> bands, double left, double w, Func<double, double> Y)
    {
        foreach (var b in bands)
        {
            var y = Math.Round(Y(b.Value)) + 0.5;
            dc.DrawLine(new Pen(WithAlpha(b.Brush, 0.7), 1) { DashStyle = new DashStyle([4, 4], 0) }, new Point(left, y), new Point(w - Right, y));
            // The chip follows the theme (white on light, dark on dark), and the text falls back to the plain foreground if the tier colour still reads poorly on it.
            var chip = Theme.IsLight ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xB0, 0x14, 0x14, 0x16);
            var chipBack = new SolidColorBrush(chip);
            chipBack.Freeze();
            var tone = ChartPaths.TextTone(b.Brush);
            if (tone is SolidColorBrush ts && Theme.Contrast(ts.Color, Color.FromRgb(chip.R, chip.G, chip.B)) < 4.5) tone = Res("Fg");
            var ft = Text(b.Label, 10, tone, true);
            var rect = new Rect(left + 4, y - ft.Height - 2, ft.Width + 8, ft.Height + 2);
            dc.DrawRoundedRectangle(chipBack, null, rect, 3, 3);
            dc.DrawText(ft, new Point(rect.X + 4, rect.Y + 1));
        }
    }

    void DrawHover(DrawingContext dc, double w, double h, double x, double y, Brush accent, Brush fg, double left, IReadOnlyList<(Brush Brush, string Text)> extra, bool scoreOn)
    {
        var p = _points[_hover];
        if (_kind == ChartKind.Line)
        {
            dc.DrawLine(new Pen(WithAlpha(fg, 0.25), 1), new Point(x, Top), new Point(x, h - Bottom));
            if (scoreOn) dc.DrawEllipse(accent, new Pen(Brushes.White, 1.5), new Point(x, y), 5, 5);
        }
        else dc.DrawLine(new Pen(WithAlpha(fg, 0.25), 1), new Point(x, Top), new Point(x, h - Bottom));

        var date = _kind == ChartKind.Line ? ClockFormat.DateClock(p.When) : p.When.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var t1 = Text(date, 10, Res("Dim"));
        var t2 = Text(scoreOn ? p.Value.ToString(_format, CultureInfo.InvariantCulture) : "", 13, fg, true);
        var rows = extra.Select(e => Text(e.Text, 11, Res("Fg"))).ToList();
        var rowH = rows.Count == 0 ? 0 : rows[0].Height;
        double bw = Math.Max(Math.Max(t1.Width, t2.Width), rows.Count == 0 ? 0 : rows.Max(r => r.Width + 14)) + 16, bh = t1.Height + t2.Height + 12 + rows.Count * rowH + (rows.Count > 0 ? 4 : 0);
        var bx = x + 12;
        if (bx + bw > w - 2) bx = x - 12 - bw;
        bx = Math.Max(2, bx);
        var by = Math.Clamp(y - bh / 2, 2, Math.Max(2, h - bh - 2));
        Brush fill = Res("Surface"), stroke = Res("Edge");
        dc.DrawRoundedRectangle(fill, new Pen(stroke, 1), new Rect(bx, by, bw, bh), 8, 8);
        dc.DrawText(t1, new Point(bx + 8, by + 6));
        dc.DrawText(t2, new Point(bx + 8, by + 6 + t1.Height));
        var ry = by + 10 + t1.Height + t2.Height;
        for (var i = 0; i < rows.Count; i++)
        {
            dc.DrawEllipse(extra[i].Brush, null, new Point(bx + 12, ry + rowH / 2), 3.5, 3.5);
            dc.DrawText(rows[i], new Point(bx + 20, ry));
            ry += rowH;
        }
    }

    /// <summary>Raised with the point index when the user clicks the chart.</summary>
    public event Action<int>? PointClicked;

    int IndexAt(MouseEventArgs e)
    {
        var n = _points.Count;
        if (n == 0 || ActualWidth <= 0 || ActualHeight <= 0) return -1;
        var (left, pw, _, _, _, _, _) = Layout(ActualWidth, ActualHeight);
        var mx = e.GetPosition(this).X;
        int idx;
        if (n == 1) idx = 0;
        else if (_kind == ChartKind.Bars) idx = (int)Math.Floor((mx - left) / pw * n);
        else idx = (int)Math.Round((mx - left) / pw * (n - 1));
        return Math.Clamp(idx, 0, n - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var idx = IndexAt(e);
        if (idx < 0 || idx == _hover) return;
        _hover = idx;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (PointClicked is null) return;
        var idx = IndexAt(e);
        if (idx >= 0) PointClicked(idx + _off);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }
}
