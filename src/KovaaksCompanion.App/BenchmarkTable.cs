using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.App;

public sealed record BenchRow(string Scenario, double? Best, bool FromServer, int Plays, DateTime? LastPlayed, IReadOnlyList<double> Maxes);
public sealed record BenchSub(string Name, Brush Color, IReadOnlyList<BenchRow> Rows);
public sealed record BenchCategory(string Name, Brush Color, IReadOnlyList<BenchSub> Subs);

/// <summary>Aggregate of one category: mean tier progress (<see cref="Value"/>), its rank and fraction, played count and scenarios per tier (index 0 = unranked).</summary>
public sealed record CategoryStat(double Value, int Rank, double Fraction, int Played, int Total, int[] Counts);

/// <summary>
/// Benchmark table built in code: one rounded card per category with a header (rank, progress to the next tier, played), the tier
/// column header, subcategory captions and scenario rows with slanted progress cells per tier. It has no scroller of its own,
/// so its full height flows into the parent.
/// </summary>
public sealed class BenchmarkTable : UserControl
{
    const double RowH = 30, CapH = 24, HeadH = 44, ColH = 26;
    readonly StackPanel _stack = new();
    readonly Dictionary<string, Border> _cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A single click on a scenario row: its name and the pointer position in window coordinates.</summary>
    public event Action<string, Point?>? ScenarioOpened;

    /// <summary>False hides each card's header row (name, rank, progress), for pages whose own hero already shows it.</summary>
    public bool ShowCategoryHeader { get; set; } = true;

    public BenchmarkTable() => Content = _stack;

    /// <summary>Narrowest width at which every column keeps its minimum (scenario 170, score 76, % 44, tiers 44 each).</summary>
    public double MinTableWidth { get; private set; }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    static Brush Argb(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    static Brush WithAlpha(Brush b, double alpha)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    static TextBlock Cell(string text, double size, Brush fg, HorizontalAlignment h, FontWeight? weight = null, Thickness? margin = null, bool trim = true) => Tab(new()
    {
        Text = text, FontSize = size, Foreground = fg, HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Center,
        FontWeight = weight ?? FontWeights.Normal, TextTrimming = trim ? TextTrimming.CharacterEllipsis : TextTrimming.None, Margin = margin ?? default,
    });

    static TextBlock Tab(TextBlock t)
    {
        System.Windows.Documents.Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
        return t;
    }

    static void Put(Grid g, UIElement e, int row, int col, int colSpan = 1)
    {
        Grid.SetRow(e, row); Grid.SetColumn(e, col); Grid.SetColumnSpan(e, colSpan);
        g.Children.Add(e);
    }

    static int AddRow(Grid g, double h)
    {
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(h) });
        return g.RowDefinitions.Count - 1;
    }

    /// <summary>Category value = mean continuous tier progress of its scenarios; rank = its floor, clamped to the tier count.</summary>
    public static CategoryStat Summarize(BenchCategory c, int tiers)
    {
        var rows = c.Subs.SelectMany(s => s.Rows).ToList();
        var counts = new int[tiers + 1];
        var sum = 0.0;
        foreach (var r in rows)
        {
            sum += r.Maxes.Count == 0 ? 0 : Tier.ValueOf(r.Best ?? 0, r.Maxes);
            counts[r.Maxes.Count == 0 ? 0 : Math.Min(Tier.TierOf(r.Best ?? 0, r.Maxes).Rank, tiers)]++;
        }
        var v = rows.Count == 0 ? 0 : sum / rows.Count;
        var rank = Math.Min((int)Math.Floor(v + 1e-9), tiers);
        return new CategoryStat(v, rank, rank >= tiers ? 1 : v - rank, rows.Count(r => r.Best != null), rows.Count, counts);
    }

    public void Set(IReadOnlyList<BenchCategory> cats, IReadOnlyList<string> tierNames, IReadOnlyList<Brush> tierBrushes)
    {
        _stack.Children.Clear();
        _cards.Clear();
        MinTableWidth = 170 + 76 + 44 + 44 * tierNames.Count + 2;
        var dim = Res("Dim");
        var shown = cats.Where(c => c.Subs.Any(s => s.Rows.Count > 0)).ToList();
        if (shown.Count == 0)
        {
            _stack.Children.Add(new TextBlock { Text = "No scenarios", Foreground = dim, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 24) });
            return;
        }
        for (var i = 0; i < shown.Count; i++)
        {
            var card = BuildCard(shown[i], tierNames, tierBrushes);
            if (i < shown.Count - 1) card.Margin = new Thickness(0, 0, 0, 14);
            _cards[shown[i].Name] = card;
            _stack.Children.Add(card);
        }
    }

    /// <summary>Brings a category's card into view and flashes its border in the accent colour.</summary>
    public void ScrollToCategory(string name)
    {
        if (!_cards.TryGetValue(name, out var card)) return;
        card.BringIntoView();
        var c = ((SolidColorBrush)Res("Accent")).Color;
        var brush = new SolidColorBrush(c);
        card.BorderBrush = brush;
        var anim = new ColorAnimation(c, Color.FromArgb(0x14, 255, 255, 255), TimeSpan.FromMilliseconds(600)) { FillBehavior = FillBehavior.Stop };
        anim.Completed += (_, _) => card.BorderBrush = Argb("#12FFFFFF");
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    Border BuildCard(BenchCategory cat, IReadOnlyList<string> tierNames, IReadOnlyList<Brush> tierBrushes)
    {
        var dim = Res("Dim");
        var tiers = tierNames.Count;
        var cols = tiers + 3; // scenario, score, to-next, tiers...
        var stat = Summarize(cat, tiers);
        var subs = cat.Subs.Where(s => s.Rows.Count > 0).ToList();
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.2, GridUnitType.Star), MinWidth = 170 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        for (var i = 0; i < tiers; i++) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 44 });

        if (ShowCategoryHeader)
        {
            // Card header: name, rank pill, progress to the next tier, played.
            var hr = AddRow(g, HeadH);
            var head = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            for (var i = 0; i < 4; i++) head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Brush? rankBrush = stat.Rank >= 1 && stat.Rank <= tierBrushes.Count ? tierBrushes[stat.Rank - 1] : null;
            var nextBrush = stat.Rank < tiers && stat.Rank < tierBrushes.Count ? tierBrushes[stat.Rank] : rankBrush ?? dim;
            void H(UIElement e, int col) { Grid.SetColumn(e, col); head.Children.Add(e); }
            H(Cell(cat.Name, 15, Res("Fg"), HorizontalAlignment.Left, FontWeights.SemiBold, null, false), 0);
            H(new Border
            {
                CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = WithAlpha(rankBrush ?? dim, 0.15),
                Child = new TextBlock { Text = rankBrush == null ? "UNRANKED" : tierNames[stat.Rank - 1], FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ChartPaths.TextTone(rankBrush ?? dim) },
            }, 1);
            var bar = new Grid { Width = 120, Height = 4, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Argb("#1AFFFFFF") });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 120 * Math.Clamp(stat.Fraction, 0, 1), Background = nextBrush });
            H(bar, 2);
            H(Cell(stat.Rank >= tiers ? "Max" : $"+{stat.Fraction * 100:0}%", 11, dim, HorizontalAlignment.Left, null, new Thickness(8, 0, 0, 0)), 3);
            Put(g, head, hr, 0, cols);
        }

        // Column header, repeated per card so each reads on its own.
        var cr = AddRow(g, ColH);
        Put(g, new Border { BorderBrush = Res("Separator"), BorderThickness = new Thickness(0, 0, 0, 1) }, cr, 0, cols);
        Put(g, Cell("SCENARIO", 10.5, dim, HorizontalAlignment.Left, FontWeights.SemiBold, new Thickness(16, 0, 0, 0)), cr, 0);
        Put(g, Cell("SCORE", 10.5, dim, HorizontalAlignment.Right, FontWeights.SemiBold, new Thickness(0, 0, 8, 0)), cr, 1);
        for (var k = 0; k < tiers; k++)
            Put(g, Cell(TierText.Label(tierNames[k]), 10, k < tierBrushes.Count ? ChartPaths.TextTone(tierBrushes[k]) : dim, HorizontalAlignment.Center, FontWeights.Bold, new Thickness(-8, 0, -8, 0), false), cr, 3 + k);

        foreach (var sub in subs)
        {
            var name = sub.Name.Trim();
            if (subs.Count > 1 && name.Length > 0 && !name.Equals(cat.Name, StringComparison.OrdinalIgnoreCase))
            {
                var row = AddRow(g, CapH);
                var cap = new DockPanel { Margin = new Thickness(16, 0, 16, 0), IsHitTestVisible = false };
                var label = Cell(name.ToUpperInvariant(), 10.5, Argb("#8AFFFFFF"), HorizontalAlignment.Left, FontWeights.SemiBold, null, false);
                DockPanel.SetDock(label, Dock.Left);
                cap.Children.Add(label);
                cap.Children.Add(new Border { Height = 1, Background = Argb("#14FFFFFF"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                Put(g, cap, row, 0, cols);
            }
            foreach (var r in sub.Rows) BuildRow(g, AddRow(g, RowH), r, tierNames, tierBrushes, cols);
        }
        AddRow(g, 6);

        return new Border
        {
            CornerRadius = new CornerRadius(18), Background = Argb("#0DFFFFFF"), BorderBrush = Argb("#12FFFFFF"), BorderThickness = new Thickness(1), ClipToBounds = true, Child = g,
        };
    }

    /// <summary>A 4px progress track with a gradient fill of <paramref name="fraction"/> in the next tier's colour.</summary>
    public static UIElement NextBar(double fraction, Brush next, double height = 4, Brush? track = null)
    {
        var c = next is SolidColorBrush s ? s.Color : Colors.Gray;
        var fill = new LinearGradientBrush(Color.FromArgb(0x99, c.R, c.G, c.B), c, 0);
        fill.Freeze();
        var g = new Grid { Height = height, VerticalAlignment = VerticalAlignment.Bottom };
        var trackBrush = track ?? Argb("#1AFFFFFF");
        g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = trackBrush });
        var f = Math.Clamp(fraction, 0, 1);
        if (f > 0.001)
        {
            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 1e-6), GridUnitType.Star) });
            inner.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = fill });
            g.Children.Add(inner);
        }
        return g;
    }

    void BuildRow(Grid g, int r, BenchRow row, IReadOnlyList<string> tierNames, IReadOnlyList<Brush> tierBrushes, int cols)
    {
        var dim = Res("Dim");
        var fg = Res("Fg");
        var tiers = tierNames.Count;
        var unplayed = row.Plays == 0 && row.Best == null;
        var pos = Tier.TierOf(row.Best ?? 0, row.Maxes);
        var rank = row.Maxes.Count == 0 ? 0 : pos.Rank;
        Brush? rowBrush = rank >= 1 && rank <= tierBrushes.Count ? tierBrushes[rank - 1] : null;

        void Add(UIElement e, int col) { e.IsHitTestVisible = false; Put(g, e, r, col); }

        Add(Cell(row.Scenario, 12.5, unplayed ? dim : fg, HorizontalAlignment.Left, FontWeights.SemiBold, new Thickness(16, 0, 8, 0)), 0);

        var score = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        if (row.Best != null && row.FromServer)
            score.Children.Add(new TextBlock
            {
                Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
            });
        score.Children.Add(Tab(new TextBlock
        {
            Text = row.Best is { } b ? b.ToString("0.#", CultureInfo.InvariantCulture) : "–", FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = row.Best == null || rowBrush == null ? dim : ChartPaths.TextTone(rowBrush), VerticalAlignment = VerticalAlignment.Center,
        }));
        Add(score, 1);

        Add(Cell(row.Maxes.Count == 0 || row.Best == null ? "" : pos.ToNext == null ? "MAX" : $"{pos.Fraction * 100:0}%", 11, dim, HorizontalAlignment.Right, null, new Thickness(0, 0, 6, 0)), 2);

        var sepText = Argb("#66FFFFFF");
        var nextText = Argb("#B3FFFFFF");
        for (var k = 1; k <= tiers; k++)
        {
            var cell = new Grid();
            var has = k <= row.Maxes.Count;
            var reached = has && k <= rank;
            var next = has && k == rank + 1;
            if (reached && k <= tierBrushes.Count)
                cell.Children.Add(new Border { Margin = new Thickness(2, 3, 2, 3), CornerRadius = new CornerRadius(6), Background = WithAlpha(tierBrushes[k - 1], 0.22) });
            if (next && row.Best != null)
            {
                var nb = k <= tierBrushes.Count ? tierBrushes[k - 1] : dim;
                var bar = NextBar(pos.Fraction, nb);
                bar.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 4));
                cell.Children.Add(bar);
            }
            if (has)
            {
                var tb = new TextBlock
                {
                    Text = row.Maxes[k - 1].ToString("#,0.##", CultureInfo.InvariantCulture), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = reached ? fg : next ? nextText : sepText, FontWeight = reached ? FontWeights.SemiBold : FontWeights.Normal,
                };
                if (next && row.Best != null) tb.Margin = new Thickness(0, 0, 0, 4);
                System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
                cell.Children.Add(tb);
            }
            Add(cell, 2 + k);
        }

        var wash = new Border { Background = Argb("#0DFFFFFF"), Opacity = 0, IsHitTestVisible = false };
        Put(g, wash, r, 0, cols);
        var tip = row.Scenario + "\n" + (row.LastPlayed is { } d ? $"Last played {d:yyyy-MM-dd}" : "Not played yet");
        if (pos.ToNext is { } to && rank < tiers && rank < tierNames.Count) tip += $"\n+{to.ToString("#,0.#", CultureInfo.InvariantCulture)} to {tierNames[rank]}";
        var hit = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = tip };
        Put(g, hit, r, 0, cols);
        void Fade(double to) => wash.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)));
        hit.MouseEnter += (_, _) => Fade(1);
        hit.MouseLeave += (_, _) => Fade(0);
        RowClick.Attach(hit, p => ScenarioOpened?.Invoke(row.Scenario, p), down =>
        {
            wash.BeginAnimation(OpacityProperty, null);
            wash.Background = Argb(down ? "#14FFFFFF" : "#0DFFFFFF");
            wash.Opacity = down || hit.IsMouseOver ? 1 : 0;
        });
    }
}

