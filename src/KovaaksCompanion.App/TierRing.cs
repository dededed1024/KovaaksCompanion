using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Rank emblem: a circular progress ring (from the top, clockwise) with the tier name and the percent to the next tier in the middle.</summary>
public sealed class TierRing : FrameworkElement
{
    const double Stroke = 6;

    readonly string _label;
    readonly string? _percent;
    readonly double _fraction;
    readonly Brush _tone;
    readonly Pen _track;
    readonly Pen _arc;

    public TierRing(string label, string? percent, double fraction, Color from, Color to, Brush tone)
    {
        _label = label; _percent = percent; _fraction = Math.Clamp(fraction, 0, 1); _tone = tone;
        Width = Height = 104;
        _track = new Pen(new SolidColorBrush(Color.FromArgb(0x1A, 255, 255, 255)), Stroke);
        _track.Freeze();
        var g = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        g.Freeze();
        _arc = new Pen(g, Stroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _arc.Freeze();
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var r = Math.Min(ActualWidth, ActualHeight) / 2 - Stroke / 2 - 1;
        dc.DrawEllipse(null, _track, c, r, r);
        if (_fraction > 0.001)
        {
            if (_fraction >= 0.999) dc.DrawEllipse(null, _arc, c, r, r);
            else
            {
                var a = _fraction * 2 * Math.PI;
                var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
                var fig = new PathFigure { StartPoint = new Point(c.X, c.Y - r), IsClosed = false };
                fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise, true));
                var geo = new PathGeometry([fig]);
                geo.Freeze();
                dc.DrawGeometry(null, _arc, geo);
            }
        }

        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface((FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var inner = (r - Stroke / 2) * 2 * 0.86;
        var size = 26.0;
        FormattedText Make(double s) => new(_label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, s, _tone, dip);
        var ft = Make(size);
        while (ft.Width > inner && size > 9) { size -= 1; ft = Make(size); }
        FormattedText? pct = _percent == null ? null : new(_percent, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface((FontFamily)GetValue(System.Windows.Documents.TextElement.FontFamilyProperty), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, _tone, dip) { };
        if (pct != null)
        {
            var tc = _tone is SolidColorBrush sb ? sb.Color : Colors.White;
            pct.SetForegroundBrush(new SolidColorBrush(Color.FromArgb(0xBF, tc.R, tc.G, tc.B)));
        }
        var total = ft.Height + (pct != null ? pct.Height + 1 : 0);
        var y = c.Y - total / 2;
        dc.DrawText(ft, new Point(c.X - ft.Width / 2, y));
        if (pct != null) dc.DrawText(pct, new Point(c.X - pct.Width / 2, y + ft.Height + 1));
    }
}
