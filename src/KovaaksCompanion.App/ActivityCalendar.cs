using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>GitHub-style activity graph: one column per week (Monday first), one cell per day shaded by plays. Shows as many weeks as fit, up to <see cref="MaxWeeks"/>, ending with the current week.</summary>
public sealed class ActivityCalendar : FrameworkElement
{
    static Typeface Face => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static Typeface BoldFace => new((FontFamily)Application.Current.FindResource("UiFont"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    const double Gap = 3, LeftW = 30, Right = 4;
    IReadOnlyDictionary<DateTime, int> _plays = new Dictionary<DateTime, int>();
    Brush _tone = Brushes.Gray;
    (int Week, int Row)? _hover;

    /// <summary>Most weeks shown (53 = a full year). Fewer weeks make the cells grow to fill the width (10..18px).</summary>
    public int MaxWeeks { get; set; } = 53;

    /// <summary>Largest cell size in px (cells otherwise grow with the width, from 10). Applies in fixed mode too; the grid is then centred.</summary>
    public double MaxCell { get; set; } = 18;

    /// <summary>When set, always shows exactly this many weeks; the cells (and so the height and the label sizes) scale with the width, keeping the aspect ratio.</summary>
    public int? FixedWeeks { get; set; }

    double CellFor(double w) => FixedWeeks is { } n ? Math.Clamp((w - LeftW - Right) / n - Gap, 6, MaxCell) : Math.Clamp((w - LeftW - Right) / MaxWeeks - Gap, 10, MaxCell);

    double ScaleFor(double cell) => FixedWeeks == null ? 1 : Math.Clamp(cell / 12, 0.85, 1.3);

    double Cell => CellFor(ActualWidth);

    double Scale => ScaleFor(Cell);

    /// <summary>Width of the whole grid (labels, cells, right margin) at <paramref name="cell"/>.</summary>
    double GridW(double cell) => LeftW + Right + Weeks * (cell + Gap);

    /// <summary>Horizontal offset centring the grid when the control is wider than it (fixed mode).</summary>
    double OffX => FixedWeeks == null ? 0 : Math.Max(0, (ActualWidth - GridW(Cell)) / 2);

    double MonthH => 16 * Scale;

    double FootH => 22 * Scale;

    protected override Size MeasureOverride(Size avail)
    {
        var w = double.IsInfinity(avail.Width) ? 600 : avail.Width;
        var cell = CellFor(w);
        return new Size(FixedWeeks == null ? w : Math.Min(w, GridW(cell)), (16 + 22) * ScaleFor(cell) + 7 * (cell + Gap));
    }

    public void Set(IReadOnlyDictionary<DateTime, int> playsPerDay, Brush tone)
    {
        _plays = playsPerDay; _tone = tone;
        _hover = null;
        InvalidateVisual();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush Alpha(Brush b, double a)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    FormattedText Text(string t, double size, Brush b, bool bold = false) =>
        new(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? BoldFace : Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    int Weeks => FixedWeeks ?? Math.Clamp((int)((ActualWidth - LeftW - Right + Gap) / (Cell + Gap)), 1, MaxWeeks);

    DateTime FirstDay => WeekStart(DateTime.Today).AddDays(-7 * (Weeks - 1));

    int CountOf(DateTime d) => _plays.TryGetValue(d.Date, out var n) ? n : 0;

    /// <summary>Alpha per level 1..4 from the quartiles of the non-zero counts.</summary>
    Brush LevelBrush(int n, int[] q)
    {
        if (n <= 0) return Argb(0x12, 255, 255, 255);
        var level = n <= q[0] ? 0 : n <= q[1] ? 1 : n <= q[2] ? 2 : 3;
        return Alpha(_tone, new[] { 0.30, 0.50, 0.75, 1.0 }[level]);
    }

    static Brush Argb(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    int[] Quartiles()
    {
        var v = _plays.Values.Where(x => x > 0).Order().ToList();
        if (v.Count == 0) return [1, 2, 3];
        int At(double f) => v[Math.Min(v.Count - 1, (int)(f * v.Count))];
        return [At(0.25), At(0.5), At(0.75)];
    }

    Rect CellRect(int week, int row) => new(OffX + LeftW + week * (Cell + Gap), MonthH + row * (Cell + Gap), Cell, Cell);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dim = Res("Dim");
        var fg = Res("Fg");
        var weeks = Weeks;
        var first = FirstDay;
        var today = DateTime.Today;
        var q = Quartiles();
        var sc = Scale;

        // Weekday labels.
        foreach (var (row, name) in new[] { (0, "Mon"), (2, "Wed"), (4, "Fri") })
        {
            var ft = Text(name, 10 * sc, dim);
            dc.DrawText(ft, new Point(OffX, CellRect(0, row).Y + (Cell - ft.Height) / 2));
        }

        // Month labels.
        var lastEnd = double.NegativeInfinity;
        for (var wk = 0; wk < weeks; wk++)
        {
            var start = first.AddDays(wk * 7);
            var firstOfMonth = Enumerable.Range(0, 7).Select(i => start.AddDays(i)).FirstOrDefault(d => d.Day == 1);
            if (firstOfMonth == default && wk != 0) continue;
            var label = (firstOfMonth == default ? start : firstOfMonth).ToString("MMM", CultureInfo.InvariantCulture);
            if (wk == 0 && firstOfMonth == default && start.Day > 21 && FixedWeeks == null) continue; // a partial first month would be misleading
            var ft = Text(label, 10 * sc, dim);
            var x = CellRect(wk, 0).X;
            if (x < lastEnd + 6) continue;
            dc.DrawText(ft, new Point(x, 0));
            lastEnd = x + ft.Width;
        }

        // Cells.
        var tone = _tone is SolidColorBrush ts ? ts.Color : Colors.Gray;
        for (var wk = 0; wk < weeks; wk++)
            for (var row = 0; row < 7; row++)
            {
                var day = first.AddDays(wk * 7 + row);
                if (day > today) continue;
                var n = CountOf(day);
                var hov = _hover == (wk, row);
                var r = CellRect(wk, row);
                if (hov) r.Inflate(1, 1);
                Brush fill = LevelBrush(n, q);
                if (n > 0 && fill is SolidColorBrush fb)
                {
                    var c = fb.Color;
                    var top = Color.FromArgb(c.A, (byte)Math.Min(255, c.R + 25), (byte)Math.Min(255, c.G + 25), (byte)Math.Min(255, c.B + 25));
                    var g = new LinearGradientBrush(top, c, 90);
                    g.Freeze();
                    fill = g;
                }
                Pen? pen = hov ? new Pen(Alpha(fg, 0.8), 1) : _selected == day ? new Pen(fg, 1.5) : day == today ?new Pen(Alpha(fg, 0.5), 1) : null;
                dc.DrawRoundedRectangle(fill, pen, r, 3, 3);
            }

        // Legend, bottom right.
        var more = Text("More", 10 * sc, dim);
        var less = Text("Less", 10 * sc, dim);
        var y = h - FootH + 6 * sc;
        var sw = 12 * sc;
        var x0 = OffX + GridW(Cell) - Right - more.Width;
        dc.DrawText(more, new Point(x0, y));
        x0 -= 4;
        for (var i = 4; i >= 0; i--)
        {
            x0 -= sw;
            dc.DrawRoundedRectangle(i == 0 ? Argb(0x12, 255, 255, 255) : Alpha(_tone, new[] { 0, 0.30, 0.50, 0.75, 1.0 }[i]), null, new Rect(x0, y, sw, sw), 3, 3);
            x0 -= 3;
        }
        x0 -= 1;
        dc.DrawText(less, new Point(x0 - less.Width, y));

        if (_hover is { } hv)
        {
            var day = first.AddDays(hv.Week * 7 + hv.Row);
            var cr = CellRect(hv.Week, hv.Row);
            var t1 = Text($"{CountOf(day)} plays", 13 * sc, fg, true);
            var t2 = Text(day.ToString("ddd, yyyy-MM-dd", CultureInfo.InvariantCulture), 10 * sc, dim);
            double bw = Math.Max(t1.Width, t2.Width) + 16, bh = t1.Height + t2.Height + 12;
            var bx = Math.Clamp(cr.X + Cell / 2 - bw / 2, 2, Math.Max(2, w - bw - 2));
            var by = cr.Y - bh - 6;
            if (by < 2) by = cr.Bottom + 6;
            var fill = Argb(0xF0, 0x22, 0x22, 0x26);
            dc.DrawRoundedRectangle(fill, new Pen(Argb(0x33, 255, 255, 255), 1), new Rect(bx, by, bw, bh), 8, 8);
            dc.DrawText(t1, new Point(bx + 8, by + 6));
            dc.DrawText(t2, new Point(bx + 8, by + 6 + t1.Height));
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        (int, int)? hit = null;
        var wk = (int)Math.Floor((p.X - OffX - LeftW) / (Cell + Gap));
        var row = (int)Math.Floor((p.Y - MonthH) / (Cell + Gap));
        if (p.X >= OffX + LeftW && wk >= 0 && wk < Weeks && row >= 0 && row < 7 && FirstDay.AddDays(wk * 7 + row) <= DateTime.Today) hit = (wk, row);
        if (hit == _hover) return;
        _hover = hit;
        InvalidateVisual();
    }

    /// <summary>Raised when a day with plays is clicked.</summary>
    public event Action<DateTime>? DayClicked;

    DateTime? _selected;

    /// <summary>The day outlined as selected, or null.</summary>
    public DateTime? SelectedDay
    {
        get => _selected;
        set { _selected = value?.Date; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_hover is not { } h) return;
        var day = FirstDay.AddDays(h.Week * 7 + h.Row);
        if (CountOf(day) > 0) DayClicked?.Invoke(day);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        InvalidateVisual();
    }
}
