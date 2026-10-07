using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary><see cref="Value"/> and <see cref="Previous"/> are tier progress (0..max rank); <see cref="Previous"/> is null when not compared.</summary>
public sealed record RadarAxis(string Label, double Value, double? Previous = null);

/// <summary>
/// Radar chart of tier progress: circular grid (25/50/75/100% of the top tier), faint spokes, the current values as a smooth
/// accent-filled shape and optionally an earlier snapshot as a pale filled shape underneath. Labels show the axis and its tier;
/// hovering an axis shows its dot and a tooltip.
/// </summary>
public sealed class RadarChart : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    const double LabelW = 110, LabelH = 44;
    int _hover = -1;
    IReadOnlyList<RadarAxis> _axes = [];
    IReadOnlyList<string> _tierNames = [];
    IReadOnlyList<Brush> _tierBrushes = [];
    bool _compare;

    /// <param name="tierNames">Name of tier k (1-based) at index k-1; its length is the scale maximum.</param>
    /// <param name="tierBrushes">Label colour per tier, same indexing as <paramref name="tierNames"/>.</param>
    public void Set(IReadOnlyList<RadarAxis> axes, IReadOnlyList<string> tierNames, IReadOnlyList<Brush> tierBrushes, bool compare)
    {
        _axes = axes; _tierNames = tierNames; _tierBrushes = tierBrushes; _compare = compare;
        _hover = -1;
        InvalidateVisual();
    }

    int Rings => Math.Max(_tierNames.Count, 1);

    /// <summary>Axis labels are drawn only for a few axes; with more, the ranking list names them and the hover tooltip identifies one.</summary>
    public const int MaxLabelled = 8;

    bool Labelled => _axes.Count <= MaxLabelled;

    (Point Center, double Radius) Geometry() =>
        (new Point(ActualWidth / 2, ActualHeight / 2), Math.Max(10, Labelled ? Math.Min(ActualWidth / 2 - LabelW, ActualHeight / 2 - LabelH) : Math.Min(ActualWidth, ActualHeight) / 2 - 12));

    Point PointAt(int i, double value)
    {
        var (c, r) = Geometry();
        var a = -Math.PI / 2 + 2 * Math.PI * i / _axes.Count;
        var d = r * Math.Clamp(value / Rings, 0, 1);
        return new Point(c.X + d * Math.Cos(a), c.Y + d * Math.Sin(a));
    }

    /// <summary>Tier name and "+NN%" progress towards the next one for a tier-progress value.</summary>
    (string Tier, string Plus, Brush Brush) Describe(double value, Brush dim)
    {
        var v = Math.Clamp(value, 0, Rings);
        var rank = (int)Math.Floor(v + 1e-9);
        var name = rank >= 1 && rank <= _tierNames.Count ? _tierNames[rank - 1] : "UNRANKED";
        return (name, rank >= Rings ? "" : $"+{(v - rank) * 100:0}%", rank >= 1 && rank <= _tierBrushes.Count ? ChartPaths.TextTone(_tierBrushes[rank - 1]) : dim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dim = (Brush)Application.Current.FindResource("Dim");
        var accent = (Brush)Application.Current.FindResource("Accent");
        if (_axes.Count < 3) { Label(dc, "Need at least 3 axes", w / 2, h / 2 - 7, dim, true); return; }

        var (c, r) = Geometry();
        var n = _axes.Count;
        var fg = (Brush)Application.Current.FindResource("Fg");
        var bg = (Brush)Application.Current.FindResource("Bg");

        var ringPen = new Pen(Argb(0x14, 255, 255, 255), 1);
        for (var k = 1; k <= 4; k++) dc.DrawEllipse(null, ringPen, c, r * k / 4, r * k / 4);
        var spoke = new Pen(Argb(0x0F, 255, 255, 255), 1);
        for (var i = 0; i < n; i++) dc.DrawLine(i == _hover ? new Pen(Argb(0x40, 255, 255, 255), 1) : spoke, c, PointAt(i, Rings));

        for (var i = 0; i < n && Labelled; i++)
        {
            var p = PointAt(i, Rings);
            var a = -Math.PI / 2 + 2 * Math.PI * i / n;
            var cos = Math.Cos(a);
            var text = _axes[i].Label.Length > 18 ? _axes[i].Label[..17] + "…" : _axes[i].Label;
            var (tier, _, tb) = Describe(_axes[i].Value, dim);
            var f1 = Fmt(text, 12.5, fg, true);
            var f2 = Fmt(tier, 11.5, tb, false);
            var x = p.X + 16 * cos;
            var y = p.Y + 22 * Math.Sin(a) - (f1.Height + f2.Height) / 2;
            double X(FormattedText f) => Math.Abs(cos) < 0.2 ? x - f.Width / 2 : cos < 0 ? x - f.Width : x;
            dc.DrawText(f1, new Point(X(f1), y));
            dc.DrawText(f2, new Point(X(f2), y + f1.Height));
        }

        var tension = Tension(n);
        if (_compare && _axes.All(x => x.Previous != null))
            dc.DrawGeometry(Argb(0x1F, 255, 255, 255), null, ChartPaths.ClosedSpline(_axes.Select((x, i) => PointAt(i, x.Previous!.Value)).ToList(), c, r, tension));

        var ac = accent is SolidColorBrush sb ? sb.Color : Colors.DodgerBlue;
        var pts = _axes.Select((x, i) => PointAt(i, x.Value)).ToList();
        var reach = Math.Max(10, pts.Max(q => (q - c).Length));
        var fill = new RadialGradientBrush(Color.FromArgb(0x1A, ac.R, ac.G, ac.B), Color.FromArgb(0x73, ac.R, ac.G, ac.B))
        {
            MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = reach, RadiusY = reach,
        };
        fill.Freeze();
        dc.DrawGeometry(fill, new Pen(accent, 2.5) { LineJoin = PenLineJoin.Round }, ChartPaths.ClosedSpline(pts, c, r, tension));

        if (_hover >= 0 && _hover < n)
        {
            dc.DrawEllipse(accent, new Pen(bg, 2), pts[_hover], 5, 5);
            var (tier, plus, tb) = Describe(_axes[_hover].Value, dim);
            var t1 = Fmt(_axes[_hover].Label, 13, fg, true);
            var t2 = Fmt(plus.Length > 0 ? $"{tier} {plus}" : tier, 11.5, tb, true);
            double bw = Math.Max(t1.Width, t2.Width) + 16, bh = t1.Height + t2.Height + 12;
            var bx = pts[_hover].X + 12;
            if (bx + bw > w - 2) bx = pts[_hover].X - 12 - bw;
            bx = Math.Max(2, bx);
            var by = Math.Clamp(pts[_hover].Y - bh / 2, 2, Math.Max(2, h - bh - 2));
            dc.DrawRoundedRectangle(Argb(0xF0, 0x22, 0x22, 0x26), new Pen(Argb(0x33, 255, 255, 255), 1), new Rect(bx, by, bw, bh), 8, 8);
            dc.DrawText(t1, new Point(bx + 8, by + 6));
            dc.DrawText(t2, new Point(bx + 8, by + 6 + t1.Height));
        }
    }

    static Brush Argb(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    static double Tension(int n) => n <= 4 ? 0.12 : n <= 6 ? 0.3 : 0.5;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_axes.Count < 3) return;
        var m = e.GetPosition(this);
        var best = -1;
        var bd = 22.0;
        for (var i = 0; i < _axes.Count; i++)
        {
            var d = (PointAt(i, _axes[i].Value) - m).Length;
            if (d < bd) { bd = d; best = i; }
        }
        if (best != _hover) { _hover = best; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover < 0) return;
        _hover = -1;
        InvalidateVisual();
    }

    static FormattedText Fmt(string text, double size, Brush brush, bool bold) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? new Typeface(Face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : Face, size, brush, 1.0);

    static void Label(DrawingContext dc, string text, double x, double y, Brush brush, bool centered, bool rightAligned = false)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10, brush, 1.0);
        dc.DrawText(ft, new Point(centered ? x - ft.Width / 2 : rightAligned ? x - ft.Width : x, y));
    }
}
