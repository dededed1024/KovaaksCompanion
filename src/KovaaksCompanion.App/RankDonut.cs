using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Scenarios per tier as a donut with a legend (beside it when wide enough, else below). Slice 0 is Unranked.</summary>
public sealed class RankDonut : UserControl
{
    readonly Grid _root = new();
    readonly DonutVisual _donut = new();
    readonly StackPanel _legend = new() { VerticalAlignment = VerticalAlignment.Center };
    bool _beside = true;

    public RankDonut()
    {
        Grid.SetIsSharedSizeScope(_legend, true);
        _root.Children.Add(_donut);
        _root.Children.Add(_legend);
        Content = _root;
        SizeChanged += (_, _) => Arrange();
        Arrange();
    }

    void Arrange()
    {
        _beside = ActualWidth >= 380;
        _root.ColumnDefinitions.Clear();
        _root.RowDefinitions.Clear();
        if (_beside)
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
            Grid.SetRow(_donut, 0); Grid.SetColumn(_donut, 0); Grid.SetRow(_legend, 0); Grid.SetColumn(_legend, 1);
            _donut.Height = Math.Clamp(ActualWidth * 0.3, 200, 320); _legend.Margin = new Thickness(12, 0, 0, 0);
        }
        else
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_donut, 0); Grid.SetColumn(_donut, 0); Grid.SetRow(_legend, 1); Grid.SetColumn(_legend, 0);
            _donut.Height = Math.Clamp(ActualWidth * 0.55, 180, 260); _legend.Margin = new Thickness(0, 8, 0, 0);
        }
    }

    public void Set(IReadOnlyList<(string Name, Brush Brush, int Count)> slices)
    {
        _donut.Set(slices);
        _legend.Children.Clear();
        var dim = (Brush)Application.Current.FindResource("Dim");
        var max = Math.Max(1, slices.Max(s => s.Count));
        var order = Enumerable.Range(1, Math.Max(0, slices.Count - 1)).Reverse().Append(0);
        foreach (var i in order)
        {
            if (i >= slices.Count) continue;
            var (name, brush, count) = slices[i];
            var tint = i == 0 ? dim : ChartPaths.TextTone(brush);
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), Opacity = count == 0 ? 0.5 : 1 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "n" });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 24 });
            row.Children.Add(new TextBlock { Text = name, FontSize = 12, Foreground = tint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            var track = new Grid { VerticalAlignment = VerticalAlignment.Center, Height = 8 };
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(count, 0.0001), GridUnitType.Star) });
            track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(max - count, 0.0001), GridUnitType.Star) });
            track.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = i == 0 ? Argb(0x3A, 255, 255, 255) : brush, Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible });
            Grid.SetColumn(track, 1);
            row.Children.Add(track);
            var c = new TextBlock { Text = count.ToString(), FontSize = 12, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(c, 2);
            row.Children.Add(c);
            _legend.Children.Add(row);
        }
    }

    static Brush Argb(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    sealed class DonutVisual : FrameworkElement
    {
        static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        static Typeface BoldFace => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        const double Thick = 22, GapDeg = 2;
        IReadOnlyList<(string Name, Brush Brush, int Count)> _slices = [];
        int _hover = -1;

        public void Set(IReadOnlyList<(string Name, Brush Brush, int Count)> slices)
        {
            _slices = slices;
            _hover = -1;
            InvalidateVisual();
        }

        (Point C, double R) Geo() => (new Point(ActualWidth / 2, ActualHeight / 2), Math.Min(ActualWidth * 0.42, ActualHeight / 2) - 4);

        /// <summary>Start angle and sweep (degrees, clockwise from the top) per slice; sweep 0 for empty slices.</summary>
        List<(double Start, double Sweep)> Angles()
        {
            var total = _slices.Sum(s => s.Count);
            var shown = _slices.Count(s => s.Count > 0);
            var gap = shown > 1 ? GapDeg : 0;
            var res = new List<(double, double)>();
            var a = 0.0;
            foreach (var s in _slices)
            {
                var sweep = total == 0 || s.Count == 0 ? 0 : 360.0 * s.Count / total;
                res.Add((a + gap / 2, Math.Max(0, sweep - gap)));
                a += sweep;
            }
            return res;
        }

        static Point At(Point c, double r, double deg) => new(c.X + r * Math.Sin(deg * Math.PI / 180), c.Y - r * Math.Cos(deg * Math.PI / 180));

        FormattedText Text(string t, double size, Brush b, bool bold) =>
            new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var fg = (Brush)Application.Current.FindResource("Fg");
            var dim = (Brush)Application.Current.FindResource("Dim");
            var (c, r) = Geo();
            var total = _slices.Sum(s => s.Count);
            var angles = Angles();
            for (var i = 0; i < _slices.Count; i++)
            {
                var (start, sweep) = angles[i];
                if (sweep <= 0) continue;
                var grow = i == _hover ? 2 : 0;
                var ro = r + grow;
                var ri = r - Thick - grow;
                var col = i == 0 ? Color.FromArgb(0x3A, 255, 255, 255) : _slices[i].Brush is SolidColorBrush sb ? sb.Color : Colors.Gray;
                var fill = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = ro, RadiusY = ro };
                fill.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(col.A * 0.75), col.R, col.G, col.B), (ri / ro)));
                fill.GradientStops.Add(new GradientStop(col, 1));
                fill.Freeze();
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    var large = sweep > 180;
                    if (sweep >= 359.9)
                    {
                        ctx.BeginFigure(At(c, ro, 0), true, true);
                        ctx.ArcTo(At(c, ro, 180), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, false);
                        ctx.ArcTo(At(c, ro, 360), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, false);
                        ctx.BeginFigure(At(c, ri, 0), true, true);
                        ctx.ArcTo(At(c, ri, 180), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true, false);
                        ctx.ArcTo(At(c, ri, 360), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true, false);
                    }
                    else
                    {
                        ctx.BeginFigure(At(c, ro, start), true, true);
                        ctx.ArcTo(At(c, ro, start + sweep), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true, false);
                        ctx.LineTo(At(c, ri, start + sweep), true, false);
                        ctx.ArcTo(At(c, ri, start), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true, false);
                    }
                }
                g.Freeze();
                dc.DrawGeometry(fill, new Pen(fill, 1.5) { LineJoin = PenLineJoin.Round }, g);
            }

            string big, small;
            Brush bigBrush = fg;
            if (_hover >= 0 && _hover < _slices.Count) { big = _slices[_hover].Count.ToString(); small = _slices[_hover].Name; bigBrush = _hover == 0 ? fg : ChartPaths.TextTone(_slices[_hover].Brush); }
            else { big = total.ToString(); small = "scenarios"; }
            var f1 = Text(big, 28, bigBrush, true);
            var f2 = Text(small, 12, dim, false);
            var y0 = c.Y - (f1.Height + f2.Height) / 2;
            dc.DrawText(f1, new Point(c.X - f1.Width / 2, y0));
            dc.DrawText(f2, new Point(c.X - f2.Width / 2, y0 + f1.Height - 2));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var (c, r) = Geo();
            var m = e.GetPosition(this);
            var d = (m - c).Length;
            var idx = -1;
            if (d <= r + 4 && d >= r - Thick - 4)
            {
                var deg = Math.Atan2(m.X - c.X, -(m.Y - c.Y)) * 180 / Math.PI;
                if (deg < 0) deg += 360;
                var ang = Angles();
                for (var i = 0; i < ang.Count; i++)
                    if (ang[i].Sweep > 0 && deg >= ang[i].Start - GapDeg / 2 && deg <= ang[i].Start + ang[i].Sweep + GapDeg / 2) { idx = i; break; }
            }
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
}
