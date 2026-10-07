using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>The Playlists detail: one scrolling page of hero, overview cards, the scenario table and the charts, built in code from <see cref="PlaylistCtx"/>.</summary>
public partial class StatsView
{
    sealed record PlaylistCtx(BrowserItem Item, Difficulty D, BenchmarkProgress P, PlaylistReport Report, List<BenchCategory> Cats, List<string> Names, List<Brush> Brushes,
        List<(DateTime Day, double Value)> History, int[] Counts, List<(string Scenario, int Rank, double Fraction)> Blocks, List<RadarEntry> Entries, DateTime Cutoff, Brush Theme, string Note)
    {
        public Dictionary<DateTime, int> Plays { get; } = Report.PlaysPerDay().GroupBy(x => x.Day.Date).ToDictionary(g => g.Key, g => g.Sum(x => x.Plays));
    }

    sealed record CatInfo(BenchCategory Cat, CategoryStat St, string RankName, Brush? RankBrush, Brush NextBrush, string NextName);

    /// <summary>Skill balance card parts: the radar and the ranked list, laid out side by side or stacked depending on the width.</summary>
    sealed class BalanceUi
    {
        public Grid Body = new();
        public RadarChart Radar = new();
        public Grid List = new();
        public List<UIElement> Rows = [];
        public double LastW;
    }

    /// <summary>Vertical gap between cards and sections, and between grid tiles.</summary>
    const double CardGap = 16, TileGap = 12;

    const string Icons = "Segoe Fluent Icons, Segoe MDL2 Assets";
    PlaylistCtx? _ctx;
    BenchmarkTable? _bench;
    BalanceUi? _bal;
    TextBlock? _noteBlock;

    Brush DimB => (Brush)FindResource("Dim");
    Brush FgB => (Brush)FindResource("Fg");

    string _code = "";
    int _codeTok;

    void ShowCode(string code)
    {
        _code = code;
        _codeTok++;
        CodeText.Text = code;
        CodeChip.Visibility = PlayList.Visibility = code.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnCodeClick(object sender, MouseButtonEventArgs e)
    {
        if (_code.Length == 0) return;
        try { Clipboard.SetText(_code); } catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); return; }
        var tok = ++_codeTok;
        CodeText.Text = "Copied";
        await Task.Delay(1200);
        if (tok == _codeTok) CodeText.Text = _code;
    }

    void OnPlayListClick(object sender, RoutedEventArgs e)
    {
        if (_code.Length > 0) KovaaksCompanion.Core.KovaaksLaunch.Open(KovaaksCompanion.Core.KovaaksLaunch.Playlist(_code));
    }

    void ShowPlaylist(double scroll)
    {
        if (_ctx is not { } c) return;
        Heading.Text = c.Item.Title;
        ShowStar();
        ShowCode(c.D.Sharecode);
        FitHeading();
        FitHost();
        PageHost.Children.Clear();
        PageHost.Children.Add(BuildPage(c));
        PlaylistScroll.UpdateLayout();
        PlaylistScroll.ScrollToVerticalOffset(scroll);
    }

    // Building blocks

    static bool HasRows(BenchCategory c) => c.Subs.Any(x => x.Rows.Count > 0);


    List<CatInfo> CatInfos(PlaylistCtx c)
    {
        var tiers = c.Names.Count;
        return c.Cats.Where(HasRows).Select(cat =>
        {
            var st = BenchmarkTable.Summarize(cat, tiers);
            Brush? rb = st.Rank >= 1 && st.Rank <= c.Brushes.Count ? c.Brushes[st.Rank - 1] : null;
            var nb = st.Rank < tiers && st.Rank < c.Brushes.Count ? c.Brushes[st.Rank] : rb ?? DimB;
            return new CatInfo(cat, st, rb == null ? "UNRANKED" : c.Names[st.Rank - 1], rb, nb, st.Rank < tiers ? c.Names[st.Rank] : "");
        }).ToList();
    }

    TextBlock Text(string text, double size, Brush fg, FontWeight? weight = null, Thickness? margin = null) =>
        new() { Text = text, FontSize = size, Foreground = fg, FontWeight = weight ?? FontWeights.Normal, Margin = margin ?? default };

    Border Card(UIElement child, Thickness? pad = null, double gap = CardGap) => new()
    {
        CornerRadius = new CornerRadius(18), Background = Solid("#0DFFFFFF"), BorderBrush = Solid("#12FFFFFF"), BorderThickness = new Thickness(1),
        Padding = pad ?? new Thickness(8), Margin = new Thickness(0, 0, 0, gap), Child = child,
    };

    /// <summary>A card with a title (17 SemiBold) and an optional one-line insight under it, above the body.</summary>
    Border TitledCard(string title, string? subtitle, params UIElement[] body)
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(title, 17, FgB, FontWeights.SemiBold, new Thickness(16, 12, 16, subtitle == null ? 4 : 0)));
        if (subtitle != null) sp.Children.Add(new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = DimB, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 2, 16, 6) });
        foreach (var e in body) sp.Children.Add(e);
        return Card(sp);
    }

    TextBlock Chevron(HorizontalAlignment h = HorizontalAlignment.Left) =>
        new() { Text = "", FontFamily = new FontFamily(Icons), FontSize = 12, Foreground = DimB, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = h };

    /// <summary>Click on release inside the element, with a 0.98 press scale over 100ms.</summary>
    static void Clickable(Border b, Action click)
    {
        var st = new ScaleTransform(1, 1);
        b.RenderTransformOrigin = new Point(0.5, 0.5);
        b.RenderTransform = st;
        void To(double s)
        {
            var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(100));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
        b.MouseLeftButtonDown += (_, _) => { b.CaptureMouse(); To(0.98); };
        b.MouseLeftButtonUp += (_, _) =>
        {
            if (!b.IsMouseCaptured) return;
            b.ReleaseMouseCapture();
            if (b.IsMouseOver) click();
        };
        b.LostMouseCapture += (_, _) => To(1);
    }

    /// <summary>Rank name big, the points on the same baseline, and a meta line.</summary>
    UIElement Hero(string rank, Brush? rankBrush, string tail, string meta)
    {
        var sp = new StackPanel { Margin = new Thickness(4, 0, 0, CardGap) };
        var line = new TextBlock();
        line.Inlines.Add(new Run(rank) { FontSize = 30, FontWeight = FontWeights.Bold, Foreground = rankBrush == null ? DimB : ChartPaths.TextTone(rankBrush) });
        line.Inlines.Add(new Run("  " + tail) { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = FgB });
        sp.Children.Add(line);
        sp.Children.Add(Text(meta, 12.5, DimB));
        return sp;
    }

    /// <summary>Large section title fading to transparent at the bottom, overlapping the card below it.</summary>
    TextBlock FadeTitle(string text)
    {
        var t = Text(text, 34, FgB, FontWeights.SemiBold, new Thickness(4, 8, 0, -5));
        t.IsHitTestVisible = false;
        t.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.White, 0.2), new GradientStop(Colors.Transparent, 0.95) }, new Point(0, 0), new Point(0, 1));
        return t;
    }

    // The page

    UIElement BuildPage(PlaylistCtx c)
    {
        var page = new StackPanel();
        var p = c.P;
        var r = c.Report;
        var ranked = p.OverallRank > 0;
        page.Children.Add(Hero(ranked ? TierText.Label(p.OverallRankName) : "UNRANKED", ranked ? RankBrush(c.D, p.OverallRankName) : null, $"· {(p.PlayedProgressShare is { } s ? $"{s * 100:0.00}%" : $"{p.Progress:#,0} pts")}",
            $"{r.PlayedCount} of {r.ScenarioCount} scenarios played · {r.TotalPlays} plays · {Dur(r.TimePlayed)}"));

        var tiers = c.Names.Count;
        var cats = CatInfos(c);
        _bench = new BenchmarkTable();

        // Category summary cards: two per row under 760px, otherwise up to four. A click scrolls to the category in the table.
        var grid = new WrapPanel();
        foreach (var ci in cats)
        {
            var st = ci.St;
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = ci.Cat.Name, Style = (Style)FindResource("Headline"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 14, 0) });
            stack.Children.Add(Text(ci.RankName, 20, ci.RankBrush == null ? DimB : ChartPaths.TextTone(ci.RankBrush), FontWeights.Bold, new Thickness(0, 4, 0, 0)));
            var bar = BenchmarkTable.NextBar(st.Fraction, ci.NextBrush, 6);
            bar.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 10, 0, 0));
            stack.Children.Add(bar);
            stack.Children.Add(Text(st.Rank >= tiers ? "Max" : $"{st.Fraction * 100:0}% to {ci.NextName}", 11.5, DimB, null, new Thickness(0, 6, 0, 0)));
            var cell = new Grid();
            cell.Children.Add(stack);
            var chev = Chevron(HorizontalAlignment.Right);
            chev.FontSize = 10; chev.VerticalAlignment = VerticalAlignment.Top; chev.Margin = new Thickness(0, 4, 0, 0);
            cell.Children.Add(chev);
            var card = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14), Cursor = Cursors.Hand, Child = cell, Margin = new Thickness(0, 0, TileGap, TileGap) };
            HoverFade(card, "#0AFFFFFF", "#14FFFFFF");
            var name = ci.Cat.Name;
            Clickable(card, () => _bench?.ScrollToCategory(name));
            grid.Children.Add(card);
        }
        grid.SizeChanged += (_, ev) =>
        {
            var cols = Math.Max(1, Math.Min(ev.NewSize.Width < 760 ? 2 : 4, cats.Count));
            for (var k = 0; k < grid.Children.Count; k++)
            {
                var el = (FrameworkElement)grid.Children[k];
                el.Width = Math.Max(0, Math.Floor((ev.NewSize.Width - TileGap * (cols - 1)) / cols));
                el.Margin = new Thickness(0, 0, k % cols == cols - 1 ? 0 : TileGap, TileGap);
            }
        };
        grid.Margin = new Thickness(0, 0, 0, CardGap - TileGap);
        page.Children.Add(grid);

        page.Children.Add(BuildActivity(c));

        // Scenarios
        page.Children.Add(FadeTitle("Scenarios"));
        page.Children.Add(BuildTable(c));

        // Charts
        page.Children.Add(FadeTitle("Charts"));
        page.Children.Add(BuildBalance(c, cats));
        page.Children.Add(BuildBlocks(c));
        return page;
    }

    UIElement BuildTable(PlaylistCtx c)
    {
        var bench = _bench ?? new BenchmarkTable();
        bench.ScenarioOpened += (n, o) => _host.ShowScenario(n, o);
        bench.Set(c.Cats, c.Names, c.Brushes);
        // Scrolls sideways only when the tier columns no longer fit; the wheel goes to the page scroll.
        var sv = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = bench, Margin = new Thickness(0, 0, 0, CardGap) };
        sv.SizeChanged += (_, ev) => bench.Width = Math.Max(ev.NewSize.Width, bench.MinTableWidth);
        return sv;
    }

    UIElement BuildBlocks(PlaylistCtx c)
    {
        var total = c.Counts.Sum();
        var r = Math.Clamp(c.P.OverallRank, 0, c.Names.Count);
        var blocks = new RankBlocksChart { Margin = new Thickness(16, 4, 16, 12) };
        blocks.SizeChanged += (_, ev) => { var h = Math.Clamp(ev.NewSize.Width * 0.3, 220, 400); if (double.IsNaN(blocks.Height) || Math.Abs(blocks.Height - h) > 1) blocks.Height = h; };
        blocks.Set(c.Blocks, c.Names, c.Brushes);
        return TitledCard("Scenarios by rank", r == 0 ? $"{total - c.Counts[0]} of {total} ranked" : $"{c.Counts.Skip(r).Sum()} of {total} at {c.Names[r - 1]}+", blocks);
    }

    FrameworkElement ProgressChart(PlaylistCtx c)
    {
        if (c.History.Count == 0) return Text("No runs", 13, DimB);
        var chart = new ScoreChart();
        chart.Set(c.History, "0.00", ChartKind.Line, false, c.Names.Select((n, i) => new ChartBand(i + 1, n, c.Brushes[i])).ToList());
        return chart;
    }

    /// <summary>Activity calendar (left) and progress chart (right) in one card; stacked when narrower than 820px.</summary>
    UIElement BuildActivity(PlaylistCtx c) => ActivityCard(c.Plays, ChartPaths.TextTone(c.Theme), 9, ProgressChart(c), c.Note);

    UIElement ActivityCard(IReadOnlyDictionary<DateTime, int> plays, Brush tone, int? weeks, FrameworkElement chart, string? note = null, Action<ActivityCalendar>? withCalendar = null, double calendarShare = 0, double maxCell = 16, int maxWeeks = 53)
    {
        var cal = new ActivityCalendar { FixedWeeks = weeks, MaxCell = maxCell, MaxWeeks = maxWeeks, Margin = new Thickness(0, 0, 0, 12) };
        cal.Set(plays, tone);
        withCalendar?.Invoke(cal);
        var left = new StackPanel();
        left.Children.Add(cal);

        chart.Margin = new Thickness(0, 0, 0, 12);
        var right = new StackPanel();
        right.Children.Add(chart);
        if (note != null)
        {
            _noteBlock = new TextBlock { Style = (Style)FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Text = note, Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible };
            right.Children.Add(_noteBlock);
        }

        var divider = new Border { Width = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(0, 8, 0, 8) };
        var body = new Grid();
        body.Children.Add(left); body.Children.Add(divider); body.Children.Add(right);

        bool? stacked = null;
        void Fit(double w)
        {
            var st = w < 820;
            if (st && chart is ScoreChart) chart.Height = Math.Clamp(w * 0.32, 180, 320);
            if (stacked == st) return;
            stacked = st;
            body.ColumnDefinitions.Clear();
            body.RowDefinitions.Clear();
            divider.Visibility = st ? Visibility.Collapsed : Visibility.Visible;
            left.Margin = st ? new Thickness(16, 0, 16, 0) : new Thickness(16, 0, 12, 0);
            right.Margin = st ? new Thickness(16, 0, 16, 0) : new Thickness(12, 0, 16, 0);
            if (st)
            {
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetRow(left, 0); Grid.SetColumn(left, 0); Grid.SetRow(right, 1); Grid.SetColumn(right, 0);
            }
            else
            {
                var share = calendarShare > 0;
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = share ? new GridLength(calendarShare, GridUnitType.Star) : GridLength.Auto });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(left, 0); Grid.SetColumn(left, 0); Grid.SetRow(right, 0); Grid.SetColumn(right, 2);
                Grid.SetRow(divider, 0); Grid.SetColumn(divider, 1);
                if (chart is ScoreChart && cal.ActualHeight > 0) chart.Height = Math.Max(140, cal.ActualHeight);
            }
        }
        body.SizeChanged += (_, ev) => Fit(ev.NewSize.Width);
        cal.SizeChanged += (_, _) => { if (stacked == false && chart is ScoreChart && cal.ActualHeight > 0) chart.Height = Math.Max(140, cal.ActualHeight); };
        return Card(body, new Thickness(8, 16, 8, 8));
    }

    // Skill balance

    UIElement BuildBalance(PlaylistCtx c, List<CatInfo> cats)
    {
        var axes = new UniformGrid { Rows = 1 };
        foreach (var n in new[] { "Categories", "Subcategories", "Scenarios" })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), Content = n, Tag = n, IsChecked = n == _host.Ui.RadarAxes };
            rb.Checked += OnAxesChecked;
            axes.Children.Add(rb);
        }
        var head = new WrapPanel { Margin = new Thickness(16, 6, 16, 12) };
        head.Children.Add(new Border { Style = (Style)FindResource("SegmentTrack"), Child = axes, Margin = new Thickness(0, 0, 20, 0) });
        var legend = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        legend.Children.Add(new TextBlock { Text = "30 days ago", FontSize = 12.5, Foreground = DimB, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(legend);

        var ui = new BalanceUi();
        ui.Radar.VerticalAlignment = VerticalAlignment.Top;
        ui.List.VerticalAlignment = VerticalAlignment.Top;
        ui.Body.Margin = new Thickness(16, 0, 16, 0);
        ui.Body.Children.Add(ui.Radar);
        ui.Body.Children.Add(ui.List);
        ui.Body.SizeChanged += (_, ev) => { if (Math.Abs(ev.NewSize.Width - ui.LastW) > 0.5) LayoutBalance(ui); };
        _bal = ui;
        RenderRadar();
        return TitledCard("Skill balance", null, head, ui.Body);
    }

    const double RankRowH = 46, RankColMin = 210, ColGap = 20;

    /// <summary>Radar and ranking, both top-aligned. Beside the radar the ranking flows into as many columns as its height needs
    /// (up to what fits); when not even one column fits it goes below the radar in as many columns as the width allows.</summary>
    void LayoutBalance(BalanceUi ui)
    {
        var w = ui.Body.ActualWidth;
        if (w <= 0) return;
        ui.LastW = w;
        var n = ui.Rows.Count;
        var radar = ui.Radar;
        ui.Body.ColumnDefinitions.Clear();
        ui.Body.RowDefinitions.Clear();
        var size = Math.Clamp(w * 0.45, 320, 480);
        var rest = w - size - 24;
        var fit = (int)Math.Floor((rest + ColGap) / (RankColMin + ColGap));
        int cols;
        if (fit >= 1)
        {
            cols = Math.Clamp((int)Math.Ceiling(n * RankRowH / size), 1, fit);
            ui.Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(size) });
            ui.Body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(radar, 0); Grid.SetColumn(radar, 0); Grid.SetRow(ui.List, 0); Grid.SetColumn(ui.List, 1);
            ui.List.Margin = new Thickness(24, 0, 0, 0);
            radar.Width = size; radar.Height = n > RadarChart.MaxLabelled ? size : Math.Max(300, size - 120); // labels, not height, limit the radius
        }
        else
        {
            cols = Math.Clamp((int)Math.Floor((w + ColGap) / (RankColMin + ColGap)), 1, Math.Max(1, n));
            ui.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            ui.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(radar, 0); Grid.SetColumn(radar, 0); Grid.SetRow(ui.List, 1); Grid.SetColumn(ui.List, 0);
            ui.List.Margin = new Thickness(0, 12, 0, 0);
            radar.Width = double.NaN; radar.Height = n > RadarChart.MaxLabelled ? Math.Clamp(w, 300, 420) : Math.Clamp(w - 120, 300, 440);
        }
        foreach (var sp in ui.List.Children.OfType<StackPanel>()) sp.Children.Clear();
        ui.List.Children.Clear();
        ui.List.ColumnDefinitions.Clear();
        var per = (int)Math.Ceiling(n / (double)Math.Max(1, cols));
        for (var k = 0; k < cols && k * per < n; k++)
        {
            if (k > 0) ui.List.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColGap) });
            ui.List.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sp = new StackPanel();
            foreach (var row in ui.Rows.Skip(k * per).Take(per)) sp.Children.Add(row);
            Grid.SetColumn(sp, ui.List.ColumnDefinitions.Count - 1);
            ui.List.Children.Add(sp);
        }
    }

    void RenderRadar()
    {
        if (_bal is not { } ui || _ctx is not { } c) return;
        var mode = _host.Ui.RadarAxes;
        Func<RadarEntry, string> KeyFor(string m) => m switch
        {
            "Scenarios" => r => r.Scenario,
            "Subcategories" => r => $"{r.Category} / {r.Sub}",
            _ => r => r.Category,
        };
        var key = KeyFor(mode);
        var groups = c.Entries.GroupBy(key).ToList();
        if (groups.Count < 3 && mode == "Categories") { mode = "Subcategories"; key = KeyFor(mode); groups = c.Entries.GroupBy(key).ToList(); }
        var axes = groups.Select(g =>
        {
            var f = g.First();
            var label = mode == "Scenarios" ? f.Scenario : mode == "Subcategories" ? Join(f.Category, f.Sub) : f.Category;
            return new RadarAxis(label.Length > 0 ? label : "Other", g.Average(r => r.Value), g.Average(r => r.Previous));
        }).ToList();
        ui.Radar.Set(axes, c.Names, c.Brushes, true);
        ui.Rows = RankingRows(axes, c);
        LayoutBalance(ui);
    }

    /// <summary>Axes strongest to weakest: name, tier and a bar. Only the strongest (green) and the weakest (orange) stand out.</summary>
    List<UIElement> RankingRows(List<RadarAxis> axes, PlaylistCtx c)
    {
        var res = new List<UIElement>();
        var max = Math.Max(1, c.Names.Count);
        var sorted = axes.OrderByDescending(a => a.Value).ToList();
        var muted = Solid("#33FFFFFF");
        for (var i = 0; i < sorted.Count; i++)
        {
            var a = sorted[i];
            var strong = i == 0 && sorted.Count > 1;
            var weak = i == sorted.Count - 1 && sorted.Count > 1;
            var v = Math.Clamp(a.Value, 0, max);
            var rank = (int)Math.Floor(v + 1e-9);
            var tb = rank >= 1 && rank <= c.Brushes.Count ? ChartPaths.TextTone(c.Brushes[rank - 1]) : DimB;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 14) };
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = Text(a.Label, 13, FgB);
            name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = a.Label;
            g.Children.Add(name);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
            right.Children.Add(Text(rank >= 1 && rank <= c.Names.Count ? c.Names[rank - 1] : "UNRANKED", 12, tb, FontWeights.SemiBold));
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            var f = v / max;
            var bar = strong ? BenchmarkTable.NextBar(f, (Brush)FindResource("Green"), 6) : weak ? BenchmarkTable.NextBar(f, (Brush)FindResource("Orange"), 6) : Meter(f, muted, 6);
            ((FrameworkElement)bar).Margin = new Thickness(0, 6, 0, 0);
            Grid.SetRow(bar, 1); Grid.SetColumnSpan(bar, 2);
            g.Children.Add(bar);
            res.Add(g);
        }
        return res;
    }

    static UIElement Meter(double fraction, Brush fill, double h)
    {
        var g = new Grid { Height = h };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(h / 2), Background = Solid("#1AFFFFFF") });
        var f = Math.Clamp(fraction, 0, 1);
        if (f > 0.001)
        {
            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 1e-6), GridUnitType.Star) });
            inner.Children.Add(new Border { CornerRadius = new CornerRadius(h / 2), Background = fill });
            g.Children.Add(inner);
        }
        return g;
    }
}
