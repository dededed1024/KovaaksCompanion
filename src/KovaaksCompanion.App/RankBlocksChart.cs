using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>
/// One column of stacked blocks per scenario (one block per tier reached, plus a faint partial block towards the next tier),
/// sorted from best to worst. Rows are tiers, labelled on the left.
/// </summary>
public sealed class RankBlocksChart : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static Typeface BoldFace => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const double Top = 8, Bottom = 8, Right = 4;
    IReadOnlyList<(string Scenario, int Rank, double Fraction)> _items = [];
    IReadOnlyList<string> _names = [];
    IReadOnlyList<Brush> _brushes = [];
    int _hover = -1;

    public RankBlocksChart() => Height = 260;

    public void Set(IReadOnlyList<(string Scenario, int Rank, double Fraction)> items, IReadOnlyList<string> tierNames, IReadOnlyList<Brush> tierBrushes)
    {
        _items = items.OrderByDescending(i => i.Rank).ThenByDescending(i => i.Fraction).ToList();
        _names = tierNames; _brushes = tierBrushes;
        _hover = -1;
        InvalidateVisual();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Color ColorOf(Brush b) => b is SolidColorBrush s ? s.Color : Colors.Gray;

    static Brush WithAlpha(Brush b, double alpha)
    {
        var c = ColorOf(b);
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    (double Left, double Slot, double RowH) Layout()
    {
        var left = _names.Count == 0 ? 0 : _names.Max(n => Text(n, 10.5, Brushes.White, true).Width) + 12;
        var plotW = Math.Max(1, ActualWidth - left - Right);
        return (left, _items.Count == 0 ? plotW : plotW / _items.Count, Math.Max(1, (ActualHeight - Top - Bottom) / Math.Max(1, _names.Count)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dim = Res("Dim");
        var fg = Res("Fg");
        var t = _names.Count;
        if (_items.Count == 0 || t == 0)
        {
            var ft = Text("No data", 12, dim);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (h - ft.Height) / 2));
            return;
        }
        var (left, slot, rh) = Layout();
        var grid = new Pen(WithAlpha(Res("Separator"), 0.5), 1);
        double RowTop(int k) => h - Bottom - k * rh; // top edge of tier k's row (k >= 1)

        for (var k = 1; k <= t; k++)
        {
            var b = k <= _brushes.Count ? _brushes[k - 1] : dim;
            var y = Math.Round(RowTop(k) + rh) + 0.5;
            dc.DrawLine(grid, new Point(left, y), new Point(w - Right, y));
            var ft = Text(_names[k - 1], 10.5, ChartPaths.TextTone(b), true);
            dc.DrawText(ft, new Point(left - 8 - ft.Width, RowTop(k) + (rh - ft.Height) / 2));
        }

        if (_hover >= 0) dc.DrawRectangle(WithAlpha(fg, 0.08), null, new Rect(left + _hover * slot, Top, slot, h - Top - Bottom));

        var size = Math.Max(1, Math.Min(slot - 3, rh - 3));
        for (var i = 0; i < _items.Count; i++)
        {
            var (_, rank, frac) = _items[i];
            var x = left + i * slot + (slot - size) / 2;
            var rb = rank >= 1 && rank <= _brushes.Count ? _brushes[rank - 1] : dim;
            var c = ColorOf(rb);
            var fill = new LinearGradientBrush(Color.FromArgb(0xE6, c.R, c.G, c.B), Color.FromArgb((byte)(0.7 * 0.9 * 255), c.R, c.G, c.B), 90);
            fill.Freeze();
            for (var k = 1; k <= Math.Min(rank, t); k++)
                dc.DrawRoundedRectangle(fill, null, new Rect(x, RowTop(k) + (rh - size) / 2, size, size), 2, 2);
            if (rank == 0 && frac <= 0.01) dc.DrawRoundedRectangle(WithAlpha(fg, 0.18), null, new Rect(x, RowTop(1) + rh - 4, size, 3), 1.5, 1.5);
            if (rank < t && frac > 0.01)
            {
                var gh = Math.Max(1, size * frac);
                var nb = rank >= 1 ? rb : (_brushes.Count > 0 ? _brushes[0] : dim);
                dc.DrawRoundedRectangle(WithAlpha(nb, 0.25), null, new Rect(x, RowTop(rank + 1) + (rh + size) / 2 - gh, size, gh), 2, 2);
            }
        }

        if (_hover >= 0 && _hover < _items.Count) DrawTip(dc, w, h, left + (_hover + 0.5) * slot, fg, dim);
    }

    void DrawTip(DrawingContext dc, double w, double h, double x, Brush fg, Brush dim)
    {
        var (name, rank, frac) = _items[_hover];
        var tier = rank >= 1 && rank <= _names.Count ? _names[rank - 1] : "Unranked";
        var tb = rank >= 1 && rank <= _brushes.Count ? _brushes[rank - 1] : dim;
        var t1 = Text(name, 13, fg, true);
        var t2 = Text(tier, 11, ChartPaths.TextTone(tb), true);
        var t3 = Text(rank >= _names.Count ? "Max tier" : $"+{frac * 100:0}% to next", 10, dim);
        double bw = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 16, bh = t1.Height + t2.Height + t3.Height + 12;
        var bx = x + 14;
        if (bx + bw > w - 2) bx = x - 14 - bw;
        bx = Math.Max(2, bx);
        var by = Math.Clamp(Top + 6, 2, Math.Max(2, h - bh - 2));
        var fill = new SolidColorBrush(Color.FromArgb(0xF0, 0x22, 0x22, 0x26));
        fill.Freeze();
        var stroke = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        stroke.Freeze();
        dc.DrawRoundedRectangle(fill, new Pen(stroke, 1), new Rect(bx, by, bw, bh), 8, 8);
        dc.DrawText(t1, new Point(bx + 8, by + 6));
        dc.DrawText(t2, new Point(bx + 8, by + 6 + t1.Height));
        dc.DrawText(t3, new Point(bx + 8, by + 6 + t1.Height + t2.Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_items.Count == 0 || ActualWidth <= 0) return;
        var (left, slot, _) = Layout();
        var mx = e.GetPosition(this).X;
        var idx = mx < left ? -1 : Math.Min(_items.Count - 1, (int)((mx - left) / slot));
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
