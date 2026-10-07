using System.Windows.Threading;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Session;

namespace KovaaksCompanion.App;

/// <summary>The Home page (the whole Stats page): search, current tier, playlists, activity and play sessions, built in code.</summary>
public partial class StatsView
{
    const int SessionStep = 6, SessionRunsShown = 7;
    int _homeCount = SessionStep;
    TextBox _search = null!;
    Border? _resultsSlot, _heroSlot, _playlistsSlot;
    bool _playlistsExpanded;

    void InitSearch()
    {
        _search = new TextBox { Tag = "Search playlists and scenarios", Margin = new Thickness(0, 0, 0, CardGap) };
        _search.TextChanged += (_, _) => { if (IsLoaded) RefreshResults(); };
        _search.KeyDown += (_, e) => { if (e.Key == Key.Escape && _search.Text.Length > 0) { e.Handled = true; _search.Text = ""; } };
    }

    void ShowHome()
    {
        FitHome();
        _homeCount = SessionStep;
        var keep = HomeScroll.VerticalOffset;
        (_search.Parent as Panel)?.Children.Remove(_search);
        HomeHost.Children.Clear();
        HomeHost.Children.Add(BuildHome());
        HomeScroll.UpdateLayout();
        HomeScroll.ScrollToVerticalOffset(keep);
        UpdateLivePill();
    }

    FrameworkElement? _liveCard;
    bool _livePillOn, _liveShown;
    DateTime? _newestEnd;

    /// <summary>The newest session is live while it can still take a run (gap not elapsed) or the game is being recorded.</summary>
    bool LiveNow() => (_newestEnd is { } end && DateTime.Now - end <= PlaySession.DefaultGap) || _host.GameActive;

    /// <summary>Shows the floating pill while the live session card is below the Home viewport (not while the playlist popup is open).</summary>
    void UpdateLivePill()
    {
        var want = false;
        if (_liveCard != null && !_popupOpen && HomeScroll.IsVisible && _liveCard.IsDescendantOf(HomeScroll) && HomeScroll.ActualHeight > 0)
        {
            try { want = _liveCard.TransformToAncestor(HomeScroll).Transform(new Point(0, 0)).Y >= HomeScroll.ActualHeight; }
            catch (InvalidOperationException) { }
        }
        if (want == _livePillOn) return;
        _livePillOn = want;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(200);
        if (want)
        {
            var tt = new TranslateTransform(0, 12);
            LivePill.RenderTransform = tt;
            LivePill.Visibility = Visibility.Visible;
            LivePill.IsHitTestVisible = true;
            LivePillDot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            LivePill.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(LivePill.Opacity, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur) { EasingFunction = ease });
        }
        else
        {
            LivePill.IsHitTestVisible = false;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(LivePill.Opacity, 0, dur) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                if (_livePillOn) return;
                LivePillDot.BeginAnimation(OpacityProperty, null);
                LivePill.Visibility = Visibility.Collapsed;
            };
            LivePill.BeginAnimation(OpacityProperty, fade);
        }
    }

    void OnLivePill(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_liveCard == null || !_liveCard.IsDescendantOf(HomeScroll)) return;
        var y = _liveCard.TransformToAncestor(HomeScroll).Transform(new Point(0, 0)).Y;
        _ws = HomeScroll;
        _target = Math.Clamp(HomeScroll.VerticalOffset + y - 64, 0, HomeScroll.ScrollableHeight);
        StartEase(HomeScroll);
    }

    /// <summary>Redraws the parts that depend on server data or favorites (tier hero, playlists, search results) without rebuilding the page.</summary>
    void RefreshHome()
    {
        if (_heroSlot == null || _playlistsSlot == null) return;
        _heroSlot.Child = BuildTierHero();
        _playlistsSlot.Child = BuildHomePlaylists();
        RefreshResults();
    }

    /// <summary>Runs that beat every earlier run of their scenario.</summary>
    HashSet<object> PbSet()
    {
        var set = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var n in _lib.Scenarios)
        {
            var s = ScenarioStats.For(_lib, n);
            foreach (var i in s.PersonalBestIndexes()) set.Add(s.Runs[i]);
        }
        return set;
    }

    UIElement BuildHome()
    {
        var page = new StackPanel();
        var runs = _lib.AllRuns.OrderByDescending(r => r.End).ToList();
        var pbs = PbSet();
        var today = DateTime.Today;

        page.Children.Add(_search);
        _resultsSlot = new Border();
        page.Children.Add(_resultsSlot);

        _heroSlot = new Border { Child = BuildTierHero() };
        page.Children.Add(_heroSlot);
        if (runs.Count > 0)
        {
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            var bars = new ScoreChart();
            bars.Set(Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d => (d, (double)plays.GetValueOrDefault(d))).ToList(), "0", ChartKind.Bars);
            page.Children.Add(ActivityCard(plays, ChartPaths.TextTone((Brush)FindResource("Accent")), 26, bars));
        }
        _playlistsSlot = new Border { Child = BuildHomePlaylists() };
        page.Children.Add(_playlistsSlot);
        if (runs.Count > 0) page.Children.Add(BuildSessions(pbs));
        RefreshResults();
        return page;
    }

    // Search

    void RefreshResults()
    {
        if (_resultsSlot == null) return;
        var q = _search.Text.Trim();
        _resultsSlot.Child = q.Length == 0 ? null : BuildResults(q);
    }

    UIElement BuildResults(string q)
    {
        bool Match(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        bool NameMatch(Benchmark b) => Match(b.BenchmarkName) || Match(b.Abbreviation);
        var ui = _host.Ui;
        var playlists = BenchmarkCatalog.All.Where(b => NameMatch(b) || b.Difficulties.Any(d => Match(d.DifficultyName)))
            .OrderByDescending(b => b.Difficulties.Any(d => ui.IsFavorite($"{d.KovaaksBenchmarkId}"))).ThenByDescending(NameMatch).Take(12).ToList();
        var scenarios = _lib.Scenarios.Where(Match).Take(30).ToList();

        var body = new List<UIElement>();
        UIElement Caption(string t, bool first) => Text(t.ToUpperInvariant(), 11, DimB, null, new Thickness(16, first ? 4 : 14, 16, 4));
        if (playlists.Count > 0)
        {
            body.Add(Caption("Playlists", true));
            foreach (var b in playlists)
            {
                var theme = ParseBrush(b.Color);
                var bb = b;
                body.Add(ResultRow(theme, b.BenchmarkName, ItemSubtitle(b), b.Abbreviation.ToUpperInvariant(), Lighten(theme, 0.45), _ => OpenPlaylist(bb)));
            }
        }
        if (scenarios.Count > 0)
        {
            body.Add(Caption("Scenarios", playlists.Count == 0));
            foreach (var n in scenarios)
            {
                var s = ScenarioStats.For(_lib, n);
                var name = n;
                body.Add(ResultRow(null, n, $"{s.Plays} plays · best {s.Best:0.#}", "", DimB, p => _host.ShowScenario(name, p)));
            }
        }
        if (body.Count == 0) body.Add(Text("No matches", 13, DimB, null, new Thickness(16, 4, 16, 12)));
        var total = playlists.Count + scenarios.Count;
        return TitledCard("Results", total == 0 ? null : $"{total}{(total >= 12 + 30 ? "+" : "")} match{(total == 1 ? "" : "es")}", [.. body]);
    }

    Border ResultRow(Brush? pill, string title, string sub, string mark, Brush markBrush, Action<Point?> click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4 + 14) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (pill != null) g.Children.Add(new Border { Width = 4, Height = 24, CornerRadius = new CornerRadius(2), Background = pill, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };
        var t = Text(title, 13.5, FgB, FontWeights.SemiBold);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        var st = Text(sub, 11.5, DimB, null, new Thickness(0, 2, 0, 0));
        st.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(t); info.Children.Add(st);
        Grid.SetColumn(info, 1);
        g.Children.Add(info);
        if (mark.Length > 0)
        {
            var m = Text(mark, 12, markBrush, FontWeights.ExtraBold, new Thickness(0, 0, 12, 0));
            m.Opacity = 0.75; m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 2);
            g.Children.Add(m);
        }
        var row = new Border { MinHeight = 48, CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        RowClick.Attach(row, click, down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        return row;
    }

    // Current tier

    /// <summary>The difficulty position inside its benchmark (later = harder).</summary>
    static int HardIndex(Benchmark b, Difficulty d)
    {
        for (var i = 0; i < b.Difficulties.Count; i++) if (b.Difficulties[i].KovaaksBenchmarkId == d.KovaaksBenchmarkId) return i;
        return 0;
    }

    /// <summary>
    /// The ranked difficulty played most in the newest session (ties go to the harder difficulty, then more points).
    /// Falls back to the loaded difficulty whose overall rank is highest relative to its tier count when the session matches none.
    /// </summary>
    (Benchmark B, Difficulty D, BenchmarkProgress P)? CurrentTier()
    {
        var ranked = new List<(Benchmark B, Difficulty D, BenchmarkProgress P)>();
        foreach (var (b, d) in AllDifficulties())
            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRank > 0 && p.RankNames.Count >= 2) ranked.Add((b, d, p));
        if (PlaySession.Group(_lib.AllRuns, all: _lib.AllRuns) is [var newest, ..]
            && newest.MostPlayed(ranked.Select((x, i) => (i, (IReadOnlyCollection<string>)_index.Scenarios(x.D.KovaaksBenchmarkId), HardIndex(x.B, x.D), x.P.Progress))) is { } win)
            return ranked[win];

        (Benchmark B, Difficulty D, BenchmarkProgress P)? best = null;
        double bestF = 0;
        int bestHard = 0;
        foreach (var (b, d) in AllDifficulties())
        {
            if (!_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) || p.OverallRank <= 0 || p.RankNames.Count < 2) continue;
            var f = p.OverallRank / (double)(p.RankNames.Count - 1);
            var hard = HardIndex(b, d);
            if (best == null || f > bestF + 1e-9 || (Math.Abs(f - bestF) <= 1e-9 && (hard > bestHard || (hard == bestHard && p.Progress > best.Value.P.Progress))))
            {
                best = (b, d, p); bestF = f; bestHard = hard;
            }
        }
        return best;
    }

    UIElement BuildTierHero()
    {
        Border Note(string title, string text, string? action = null)
        {
            var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            sp.Children.Add(Text(title, 11, DimB, null, new Thickness(0, 0, 0, 6)));
            sp.Children.Add(Text(text, 14, FgB, FontWeights.SemiBold));
            if (action != null)
            {
                var link = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0), MinHeight = 28, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent") };
                link.Click += (_, _) => _host.ShowMain("Settings");
                sp.Children.Add(link);
            }
            return Card(sp, new Thickness(8));
        }

        if (_host.Settings.EffectiveSteamId.Length == 0)
            return Note("CURRENT TIER", "No Steam ID", "Open Settings");
        if (CurrentTier() is not { } t)
            return Note("CURRENT TIER", _progress.Count == 0 ? "Loading…" : "No ranked playlist");

        var (b, d, p) = t;
        var brush = RankBrush(d, p.OverallRankName);
        var tone = ChartPaths.TextTone(brush);
        var tiers = p.RankNames.Count - 1;

        var pill = new Border
        {
            CornerRadius = new CornerRadius(16), Background = Alpha(brush, 0.18), BorderBrush = Alpha(brush, 0.55), BorderThickness = new Thickness(1.5),
            Padding = new Thickness(22, 12, 22, 12), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0),
            Child = Text(p.OverallRankName, 28, tone, FontWeights.Bold),
        };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text("CURRENT TIER", 11, DimB));
        var name = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new System.Windows.Documents.Run(b.BenchmarkName) { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = FgB });
        name.Inlines.Add(new System.Windows.Documents.Run("  " + d.DifficultyName) { FontSize = 14, Foreground = DimB });
        info.Children.Add(name);
        var next = p.OverallRank < tiers ? $"next: {p.RankName(p.OverallRank + 1)}" : "top tier";
        info.Children.Add(Text($"{p.Progress:#,0} pts · tier {p.OverallRank} of {tiers} · {next}", 12.5, DimB, null, new Thickness(0, 4, 0, 10)));

        // Ladder: one segment per tier, filled up to the reached one (the overall tier thresholds are not part of the server data).
        var ladder = new Grid { Height = 6 };
        for (var i = 1; i <= tiers; i++)
        {
            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var seg = new Border
            {
                CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, i < tiers ? 3 : 0, 0), ToolTip = p.RankName(i),
                Background = i <= p.OverallRank ? RankBrush(d, p.RankName(i)) : Solid("#1AFFFFFF"),
            };
            Grid.SetColumn(seg, i - 1);
            ladder.Children.Add(seg);
        }
        info.Children.Add(ladder);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(pill);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        var chev = Chevron(HorizontalAlignment.Right);
        chev.Margin = new Thickness(16, 0, 4, 0);
        Grid.SetColumn(chev, 2);
        grid.Children.Add(chev);

        var bg = new LinearGradientBrush(((SolidColorBrush)Alpha(brush, 0.20)).Color, ((SolidColorBrush)Alpha(brush, 0.04)).Color, 0);
        bg.Freeze();
        var card = new Border
        {
            CornerRadius = new CornerRadius(18), Background = bg, BorderBrush = Alpha(brush, 0.35), BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 18, 20, 18), Margin = new Thickness(0, 0, 0, CardGap), Cursor = Cursors.Hand, Child = grid,
        };
        Clickable(card, () => OpenPlaylist(b, d));
        return card;
    }

    // Play sessions

    UIElement BuildSessions(HashSet<object> pbs)
    {
        var sessions = PlaySession.Group(_lib.AllRuns, all: _lib.AllRuns);
        var page = new StackPanel();
        page.Children.Add(SectionTitle("Recent sessions"));
        var list = new StackPanel();
        page.Children.Add(list);
        var more = new Button { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, CardGap), Padding = new Thickness(14, 0, 14, 0), MinHeight = 34, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent"), Content = "Show more" };
        page.Children.Add(more);
        void Fill()
        {
            list.Children.Clear();
            _liveCard = null;
            // The newest session is live while it can still take a run (gap not elapsed) or the game is being recorded.
            _newestEnd = sessions.Count > 0 ? sessions[0].End : null;
            var live = sessions.Count > 0 && LiveNow();
            _liveShown = LiveNow();
            for (var i = 0; i < Math.Min(sessions.Count, _homeCount); i++) list.Children.Add(SessionCard(sessions[i], pbs, i == 0 && live));
            if (live && list.Children.Count > 0) _liveCard = list.Children[0] as FrameworkElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateLivePill);
            more.Visibility = sessions.Count > _homeCount ? Visibility.Visible : Visibility.Collapsed;
        }
        more.Click += (_, _) => { _homeCount += SessionStep; Fill(); };
        Fill();
        return page;
    }

    static string DayLabel(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    UIElement SessionCard(PlaySession s, HashSet<object> pbs, bool live)
    {
        var pbCount = s.Runs.Count(pbs.Contains);
        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        var title = new TextBlock();
        title.Inlines.Add(new System.Windows.Documents.Run(DayLabel(s.Start.Date)) { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = FgB });
        title.Inlines.Add(new System.Windows.Documents.Run($"  {s.Start:HH:mm} – {s.End:HH:mm}") { FontSize = 13, Foreground = DimB });
        if (live)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 8, 1) };
            pillRow.Children.Add(dot);
            pillRow.Children.Add(Text("LIVE", 10.5, Brushes.White, FontWeights.Bold));
            var pill = new Border { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8), Background = Solid("#FF453A"), Child = pillRow };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(title); line.Children.Add(pill);
            head.Children.Add(line);
        }
        else head.Children.Add(title);

        var stats = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Stat(string value, string label, Brush? fg = null)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 0, 28, 4) };
            sp.Children.Add(Text(value, 15, fg ?? FgB, FontWeights.SemiBold));
            sp.Children.Add(Text(label, 11, DimB));
            stats.Children.Add(sp);
        }
        Stat(Dur(s.Span), "span");
        Stat(Dur(s.Duration), "played");
        Stat($"{s.RunCount}", s.RunCount == 1 ? "run" : "runs");
        Stat($"{s.ScenarioCount}", s.ScenarioCount == 1 ? "scenario" : "scenarios");
        Stat(s.AverageAccuracy.ToString("P1"), "avg accuracy");
        Stat($"{pbCount}", pbCount == 1 ? "PB" : "PBs", pbCount > 0 ? (Brush)FindResource("Green") : null);
        head.Children.Add(stats);

        var headHit = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = head, CornerRadius = new CornerRadius(14, 14, 0, 0) };
        RowClick.Attach(headHit, p => _host.ShowSession(s, p), down => headHit.Background = down ? Solid("#14FFFFFF") : headHit.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        headHit.MouseEnter += (_, _) => headHit.Background = Solid("#0DFFFFFF");
        headHit.MouseLeave += (_, _) => headHit.Background = Brushes.Transparent;

        var rows = new StackPanel { Margin = new Thickness(8, 0, 8, 4) };
        var expand = new Button { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(14, 0, 14, 0), MinHeight = 30, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent") };
        var all = false;
        void Fill()
        {
            rows.Children.Clear();
            foreach (var r in s.Runs.OrderByDescending(x => x.End).Take(all ? int.MaxValue : SessionRunsShown)) rows.Children.Add(RunRow(r, pbs.Contains(r)));
            expand.Visibility = !all && s.RunCount > SessionRunsShown ? Visibility.Visible : Visibility.Collapsed;
            expand.Content = $"Show all {s.RunCount} runs";
        }
        expand.Click += (_, _) => { all = true; Fill(); };
        Fill();

        var sep = new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(16, 0, 16, 6) };
        var body = new StackPanel();
        body.Children.Add(headHit); body.Children.Add(sep); body.Children.Add(rows); body.Children.Add(expand);
        var card = Card(body);
        if (live)
        {
            var red = Solid("#FF453A");
            card.BorderBrush = Alpha(red, 0.6);
            card.BorderThickness = new Thickness(1.5);
            card.Background = CompositeWash(Alpha(red, 0.06));
        }
        return card;
    }

    /// <summary>Layers the faint red wash over the normal card background.</summary>
    Brush CompositeWash(Brush wash)
    {
        var g = new DrawingGroup();
        g.Children.Add(new GeometryDrawing(Solid("#0DFFFFFF"), null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        g.Children.Add(new GeometryDrawing(wash, null, new RectangleGeometry(new Rect(0, 0, 1, 1))));
        return new DrawingBrush(g) { Stretch = Stretch.Fill };
    }

    Border RunRow(RunRecord r, bool pb)
    {
        var name = r.Scenario;
        var end = r.End;
        var replay = SessionStore.FindRun(_sessions, name, end) >= 0;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        void Put(FrameworkElement e, int col, Thickness m, HorizontalAlignment h = HorizontalAlignment.Left)
        {
            e.VerticalAlignment = VerticalAlignment.Center; e.HorizontalAlignment = h; e.Margin = m;
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
        Put(Text(r.Start.ToString("HH:mm"), 12, DimB), 0, new Thickness(8, 0, 0, 0));
        var nm = Text(name, 13, FgB, FontWeights.SemiBold);
        nm.TextTrimming = TextTrimming.CharacterEllipsis;
        Put(nm, 1, new Thickness(4, 0, 8, 0));
        var sc = Text(r.Score.ToString("0.#"), 13, FgB, FontWeights.SemiBold);
        System.Windows.Documents.Typography.SetNumeralAlignment(sc, FontNumeralAlignment.Tabular);
        Put(sc, 2, new Thickness(0), HorizontalAlignment.Right);
        var ac = Text(r.Accuracy.ToString("P1"), 12, DimB);
        System.Windows.Documents.Typography.SetNumeralAlignment(ac, FontNumeralAlignment.Tabular);
        Put(ac, 3, new Thickness(0), HorizontalAlignment.Right);
        if (pb)
        {
            var green = (Brush)FindResource("Green");
            var pill = new Grid();
            pill.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = green, Opacity = 0.18 });
            pill.Children.Add(Text("PB", 10.5, green, FontWeights.SemiBold, new Thickness(8, 1, 8, 1)));
            Put(pill, 4, new Thickness(10, 0, 0, 0));
        }
        var row = new Border { Height = 40, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
        if (replay)
        {
            var play = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Open replay", Child = Text("", 15, (Brush)FindResource("Accent")) };
            ((TextBlock)play.Child).FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            ((TextBlock)play.Child).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)play.Child).VerticalAlignment = VerticalAlignment.Center;
            play.MouseEnter += (_, _) => play.Background = Solid("#1AFFFFFF");
            play.MouseLeave += (_, _) => play.Background = Brushes.Transparent;
            RowClick.Attach(play, _ => _host.ShowRun(name, end), down => play.Background = Solid(down ? "#29FFFFFF" : play.IsMouseOver ? "#1AFFFFFF" : "#00FFFFFF"));
            Put(play, 5, new Thickness(0));
        }
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        RowClick.Attach(row, p => _host.ShowScenario(name, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        return row;
    }

    // Playlists

    /// <summary>All favourite playlists first, then recently played ones (a playlist counts as played when any of its scenarios has a local run), up to 8 in total.</summary>
    UIElement BuildHomePlaylists()
    {
        DateTime? LastOf(Difficulty d)
        {
            DateTime? m = null;
            foreach (var n in _index.Scenarios(d.KovaaksBenchmarkId))
                if (_lib.Runs(n) is { Count: > 0 } l && (m == null || l[^1].End > m)) m = l[^1].End;
            return m;
        }
        var items = AllDifficulties().Select(x => (x.B, x.D, Fav: _host.Ui.IsFavorite($"{x.D.KovaaksBenchmarkId}"), Last: LastOf(x.D))).ToList();
        var favs = items.Where(i => i.Fav).OrderByDescending(i => i.Last ?? DateTime.MinValue).ToList();
        var rest = items.Where(i => !i.Fav).OrderByDescending(i => i.Last ?? DateTime.MinValue).ToList();
        var collapsed = favs.Concat(rest.Where(i => i.Last != null).Take(Math.Max(0, 8 - favs.Count))).ToList();
        var canExpand = items.Count > collapsed.Count;
        var shown = _playlistsExpanded ? favs.Concat(rest).ToList() : collapsed;
        if (shown.Count == 0)
            return PlaylistSection(Text("No playlists", 13, DimB, null, new Thickness(16, 4, 16, 12)));

        var grid = new UniformGrid { Columns = 2 };
        foreach (var (b, d, fav, last) in shown)
        {
            var theme = ParseBrush(b.Color);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Text(b.BenchmarkName, 14, FgB, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            info.Children.Add(title);
            info.Children.Add(Text(d.DifficultyName, 12, DimB, null, new Thickness(0, 2, 0, 0)));
            if (last is { } l)
            {
                var days = (DateTime.Today - l.Date).Days;
                info.Children.Add(Text($"last played {(days <= 0 ? "today" : $"{days}d ago")}", 11.5, DimB, null, new Thickness(0, 2, 0, 0)));
            }
            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0)
                info.Children.Add(Text(p.OverallRankName, 12, ChartPaths.TextTone(RankBrush(d, p.OverallRankName)), FontWeights.SemiBold, new Thickness(0, 2, 0, 0)));
            var cell = new Grid();
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var pill = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = theme, Margin = new Thickness(0, 2, 12, 2) };
            cell.Children.Add(pill);
            Grid.SetColumn(info, 1);
            cell.Children.Add(info);
            if (fav)
            {
                var star = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 13, Foreground = (Brush)FindResource("Orange"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
                Grid.SetColumn(star, 2);
                cell.Children.Add(star);
            }
            var card = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(TileGap / 2), Cursor = Cursors.Hand, Child = cell };
            HoverFade(card, "#0AFFFFFF", "#14FFFFFF");
            var (bb, dd) = (b, d);
            Clickable(card, () => OpenPlaylist(bb, dd));
            grid.Children.Add(card);
        }
        grid.SizeChanged += (_, e) => grid.Columns = Math.Min(shown.Count, e.NewSize.Width < 640 ? 2 : e.NewSize.Width < 900 ? 3 : 4);
        var body = new StackPanel();
        body.Children.Add(new Border { Padding = new Thickness(TileGap / 2 - 2, 0, TileGap / 2 - 2, TileGap / 2 - 2), Child = grid });
        if (canExpand || _playlistsExpanded)
        {
            var toggle = new Button
            {
                Content = _playlistsExpanded ? "Show less" : $"Show all ({items.Count})", HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(12, 0, 12, 0), MinHeight = 28, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent"),
            };
            toggle.Click += (_, _) =>
            {
                _playlistsExpanded = !_playlistsExpanded;
                if (_playlistsSlot != null) _playlistsSlot.Child = BuildHomePlaylists();
            };
            body.Children.Add(toggle);
        }
        return PlaylistSection(body);
    }

    /// <summary>Section title outside, content in a single card (same section pattern as Recent sessions).</summary>
    UIElement PlaylistSection(UIElement content)
    {
        var page = new StackPanel();
        page.Children.Add(SectionTitle("Playlists"));
        page.Children.Add(Card(content));
        return page;
    }
}
