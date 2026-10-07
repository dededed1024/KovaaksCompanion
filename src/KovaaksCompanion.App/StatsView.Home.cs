using System.Windows.Threading;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Session;

namespace KovaaksCompanion.App;

/// <summary>The Home page (the whole Stats page): search, current tier, playlists, activity and play sessions, built in code.</summary>
public partial class StatsView
{
    const int SessionStep = 6, SessionRunsShown = 7;
    int _homeCount = SessionStep;
    Border? _heroSlot, _playlistsSlot;

    void ShowHome()
    {
        FitHome();
        _homeCount = SessionStep;
        var keep = HomeScroll.VerticalOffset;
        HomeHost.Children.Clear();
        HomeHost.Children.Add(BuildHome());
        HomeScroll.UpdateLayout();
        HomeScroll.ScrollToVerticalOffset(keep);
        UpdateLivePill();
        RefreshResults();
    }

    FrameworkElement? _liveCard;
    bool _livePillOn, _liveShown;
    DateTime? _newestEnd;

    bool _gameWasActive, _gameClosed;

    /// <summary>The newest session is live while the game is being recorded, or while it can still take a run (gap not elapsed) and the game has not been closed since.</summary>
    bool LiveNow() => _host.GameActive || (!_gameClosed && _newestEnd is { } end && DateTime.Now - end <= PlaySession.DefaultGap);

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

        _heroSlot = new Border { Child = BuildTierHero() };
        page.Children.Add(_heroSlot);
        if (runs.Count > 0)
        {
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            var day = _selDay is { } d0 && plays.ContainsKey(d0) ? d0 : plays.Keys.Max();
            _selDay = day;
            var dayList = new StackPanel();
            FillDay(dayList, day);
            var dayScroll = new ScrollViewer { Content = dayList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Height = 0, Style = (Style)FindResource("PinnedScroll") };
            page.Children.Add(ActivityCard(plays, ChartPaths.TextTone((Brush)FindResource("Accent")), null, dayScroll, null, cal =>
            {
                cal.Cursor = Cursors.Hand;
                cal.SelectedDay = day;
                cal.SizeChanged += (_, _) => dayScroll.Height = cal.ActualHeight;
                cal.DayClicked += clicked =>
                {
                    _selDay = clicked.Date;
                    cal.SelectedDay = clicked;
                    FillDay(dayList, clicked.Date);
                    dayScroll.ScrollToTop();
                };
            }, calendarShare: 1.6));
        }
        _playlistsSlot = new Border { Child = BuildHomePlaylists() };
        page.Children.Add(_playlistsSlot);
        if (runs.Count > 0) page.Children.Add(BuildSessions(pbs));
        return page;
    }

    // Search popup

    bool _searchOpen;
    int _searchTok;

    /// <summary>Opens the search popup with an empty query and the keyboard focus in the field.</summary>
    public void OpenSearch()
    {
        if (_searchOpen) { SearchBox.Focus(); SearchBox.SelectAll(); return; }
        _searchOpen = true;
        _searchTok++;
        SearchScrim.BeginAnimation(OpacityProperty, null); SearchScrim.Opacity = 0;
        SearchCard.BeginAnimation(OpacityProperty, null); SearchCard.Opacity = 0;
        SearchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); SearchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SearchScale.ScaleX = SearchScale.ScaleY = 0.94;
        SearchBox.Text = "";
        RefreshResults();
        SearchPopup.Visibility = Visibility.Visible;
        Animate(true, null, SearchScrim, SearchCard, SearchScale);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_searchOpen) { SearchBox.Focus(); Keyboard.Focus(SearchBox); } });
    }

    public void CloseSearch()
    {
        if (!_searchOpen) return;
        _searchOpen = false;
        var tok = ++_searchTok;
        Animate(false, () =>
        {
            if (tok != _searchTok) return;
            SearchPopup.Visibility = Visibility.Collapsed;
            SearchScroll.Content = null;
        }, SearchScrim, SearchCard, SearchScale);
    }

    void OnSearchScrim(object sender, MouseButtonEventArgs e) => CloseSearch();
    void OnSearchChanged(object sender, TextChangedEventArgs e) { if (_searchOpen) RefreshResults(); }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        else CloseSearch();
    }

    void RefreshResults()
    {
        if (!_searchOpen) return;
        var q = SearchBox.Text.Trim();
        SearchScroll.Content = q.Length == 0 ? null : BuildResults(q);
        SearchScroll.Visibility = q.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchScroll.ScrollToTop();
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
                body.Add(ResultRow(theme, b.BenchmarkName, ItemSubtitle(b), b.Abbreviation.ToUpperInvariant(), Lighten(theme, 0.45), _ => { CloseSearch(); OpenPlaylist(bb); }));
            }
        }
        if (scenarios.Count > 0)
        {
            body.Add(Caption("Scenarios", playlists.Count == 0));
            foreach (var n in scenarios)
            {
                var s = ScenarioStats.For(_lib, n);
                var name = n;
                body.Add(ResultRow(null, n, $"{s.Plays} plays · best {s.Best:0.#}", "", DimB, p => { CloseSearch(); _host.ShowScenario(name, p); }));
            }
        }
        if (body.Count == 0) body.Add(Text("No matches", 13, DimB, null, new Thickness(16, 4, 16, 12)));
        var list = new StackPanel();
        foreach (var e in body) list.Children.Add(e);
        return list;
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

    /// <summary>The playlists the user pinned for the hero (<see cref="Core.UiState.TierPlaylists"/>) in pin order, those whose progress is loaded and ranked.</summary>
    List<(Benchmark B, Difficulty D, BenchmarkProgress P)> PinnedTiers()
    {
        var all = AllDifficulties().ToList();
        var list = new List<(Benchmark, Difficulty, BenchmarkProgress)>();
        foreach (var id in _host.Ui.TierPlaylists)
            foreach (var (b, d) in all)
                if ($"{d.KovaaksBenchmarkId}" == id && _progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRank > 0 && p.RankNames.Count >= 2)
                    list.Add((b, d, p));
        return list;
    }

    UIElement BuildTierHero()
    {
        Border Note(string text, string? action = null)
        {
            var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            sp.Children.Add(Text(text, 14, FgB, FontWeights.SemiBold));
            if (action != null)
            {
                var link = new Button { Content = action, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0), MinHeight = 28, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent") };
                link.Click += (_, _) => _host.ShowMain("Settings");
                sp.Children.Add(link);
            }
            return Card(sp, new Thickness(8));
        }

        if (_host.Ui.TierPlaylists.Count == 0) return new Border();
        if (_host.Settings.EffectiveSteamId.Length == 0)
            return Note("No Steam ID", "Open Settings");
        var pinned = PinnedTiers();
        if (pinned.Count == 0)
            return Note(_progress.Count == 0 ? "Loading…" : "No ranked playlist");

        var stack = new StackPanel();
        foreach (var (b, d, p) in pinned) stack.Children.Add(TierCard(b, d, p));
        return stack;
    }

    /// <summary>One hero card for a pinned playlist: rank emblem ring, benchmark and difficulty, progress line and the tier ladder; opens the playlist.</summary>
    Border TierCard(Benchmark b, Difficulty d, BenchmarkProgress p)
    {
        var brush = RankBrush(d, p.OverallRankName);
        var tone = ChartPaths.TextTone(brush);
        var tiers = p.RankNames.Count - 1;
        var top = p.OverallRank >= tiers;
        var nextBrush = top ? brush : RankBrush(d, p.RankName(p.OverallRank + 1));
        static Color Col(Brush x) => x is SolidColorBrush s ? s.Color : Colors.Gray;
        Color Tint(double a) => Color.FromArgb((byte)(a * 255), Col(brush).R, Col(brush).G, Col(brush).B);

        // Emblem
        var ring = new TierRing(TierText.Label(p.OverallRankName), top ? null : $"{p.OverallFraction * 100:0}%", p.OverallFraction, Col(brush), Col(nextBrush), tone)
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0),
            Effect = new DropShadowEffect { Color = Col(brush), ShadowDepth = 0, BlurRadius = 24, Opacity = 0.6 },
        };

        // Info
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = FgB, TextTrimming = TextTrimming.CharacterEllipsis };
        nameRow.Inlines.Add(new System.Windows.Documents.Run(b.BenchmarkName));
        nameRow.Inlines.Add(new System.Windows.Documents.Run("  ·  ") { Foreground = DimB });
        nameRow.Inlines.Add(new System.Windows.Documents.Run(d.DifficultyName));
        info.Children.Add(nameRow);

        info.Children.Add(Text($"{(p.ProgressShare is { } s ? $"{s * 100:0.00}%" : $"{p.Progress:#,0} pts")} · tier {p.OverallRank} of {tiers}", 12.5, DimB, null, new Thickness(0, 4, 0, 12)));

        var ladder = new Grid { Height = 8 };
        for (var i = 1; i <= tiers; i++)
        {
            ladder.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var seg = new Border { CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, i < tiers ? 3 : 0, 0), ToolTip = TierText.Label(p.RankName(i)), Background = Solid("#1AFFFFFF") };
            if (i <= p.OverallRank) seg.Background = RankBrush(d, p.RankName(i));
            else if (i == p.OverallRank + 1 && p.OverallFraction > 0)
            {
                var f = Math.Clamp(p.OverallFraction, 0, 1);
                var fill = new Grid();
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(f, GridUnitType.Star) });
                fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - f, GridUnitType.Star) });
                fill.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = nextBrush });
                seg.Child = fill;
            }
            Grid.SetColumn(seg, i - 1);
            ladder.Children.Add(seg);
        }
        info.Children.Add(ladder);

        var grid = new Grid { Margin = new Thickness(20, 18, 20, 18) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(ring);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);
        var chev = Chevron(HorizontalAlignment.Right);
        chev.Margin = new Thickness(16, 0, 4, 0);
        Grid.SetColumn(chev, 2);
        grid.Children.Add(chev);

        // Background layers: emblem glow and a large tier-name watermark, clipped to the rounded card.
        var glowBrush = new RadialGradientBrush(Tint(0.25), Color.FromArgb(0, Col(brush).R, Col(brush).G, Col(brush).B));
        glowBrush.Freeze();
        var glow = new Border { Width = 260, Height = 260, CornerRadius = new CornerRadius(130), Background = glowBrush, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(-58, 0, 0, 0), IsHitTestVisible = false };
        var mark = new TextBlock
        {
            Text = TierText.Label(p.OverallRankName), FontSize = 96, FontWeight = FontWeights.ExtraBold, Foreground = Alpha(brush, 0.06), TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 36, 0), IsHitTestVisible = false,
        };
        var layers = new Grid();
        layers.Children.Add(glow);
        layers.Children.Add(mark);
        layers.Children.Add(grid);
        layers.SizeChanged += (_, e) => layers.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);

        var bg = new LinearGradientBrush(Tint(0.28), Tint(0.03), new Point(0, 0), new Point(1, 1));
        bg.Freeze();
        var border = new LinearGradientBrush(Tint(0.5), Tint(0.1), new Point(0, 0), new Point(1, 1));
        border.Freeze();
        var card = new Border { CornerRadius = new CornerRadius(18), Background = bg, BorderBrush = border, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = layers };
        Clickable(card, () => OpenPlaylist(b, d));

        // Hover: lift 2px and brighten the emblem glow.
        var lift = new TranslateTransform();
        var wrap = new Border { Margin = new Thickness(0, 0, 0, CardGap), Child = card, RenderTransform = lift };
        var shadow = (DropShadowEffect)ring.Effect;
        void Hover(bool on)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(on ? -2 : 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(on ? 0.95 : 0.6, TimeSpan.FromMilliseconds(150)));
        }
        card.MouseEnter += (_, _) => Hover(true);
        card.MouseLeave += (_, _) => Hover(false);
        return wrap;
    }

    // Play sessions

    DateTime? _selDay;

    /// <summary>The sessions that have a run on <paramref name="day"/>, one clickable row each (opens the session popup), under a day heading.</summary>
    void FillDay(StackPanel host, DateTime day)
    {
        host.Children.Clear();
        var sessions = PlaySession.Group(_lib.AllRuns, all: _lib.AllRuns).Where(s => s.Runs.Any(r => r.End.Date == day)).ToList();
        host.Children.Add(Text($"{DayLabel(day)} · {sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")}", 15, FgB, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var s in sessions)
        {
            var g = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            var rowTitle = new TextBlock();
            rowTitle.Inlines.Add(new System.Windows.Documents.Run($"#{s.Number}  ") { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = DimB });
            rowTitle.Inlines.Add(new System.Windows.Documents.Run(ClockFormat.Range(s.Start, s.End)) { FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = FgB });
            info.Children.Add(rowTitle);
            info.Children.Add(Text($"{s.RunCount} {(s.RunCount == 1 ? "run" : "runs")} · {s.ScenarioCount} {(s.ScenarioCount == 1 ? "scenario" : "scenarios")} · {s.AverageAccuracy:P0}", 12, DimB, null, new Thickness(0, 2, 0, 0)));
            g.Children.Add(info);
            var chev = Chevron(HorizontalAlignment.Right);
            chev.FontSize = 10;
            Grid.SetColumn(chev, 1);
            g.Children.Add(chev);
            var row = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g, Margin = new Thickness(0, 0, 0, 2) };
            var sess = s;
            RowClick.Attach(row, p => _host.ShowSession(sess, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
            row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            host.Children.Add(row);
        }
    }

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
        title.Inlines.Add(new System.Windows.Documents.Run($"  #{s.Number}  " + ClockFormat.Range(s.Start, s.End)) { FontSize = 13, Foreground = DimB });
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
            expand.Visibility = s.RunCount > SessionRunsShown ? Visibility.Visible : Visibility.Collapsed;
            expand.Content = all ? "Show less" : $"Show all {s.RunCount} runs";
        }
        UIElement? cardRef = null;
        expand.Click += (_, _) =>
        {
            all = !all; Fill();
            if (all) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                if (cardRef == null || !cardRef.IsDescendantOf(HomeScroll)) return;
                var y = cardRef.TransformToAncestor(HomeScroll).Transform(new Point(0, 0)).Y;
                if (y >= 0) return;
                _ws = HomeScroll;
                _target = Math.Clamp(HomeScroll.VerticalOffset + y - 64, 0, HomeScroll.ScrollableHeight);
                StartEase(HomeScroll);
            }));
        };
        Fill();

        var sep = new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(16, 0, 16, 6) };
        var body = new StackPanel();
        body.Children.Add(headHit); body.Children.Add(sep); body.Children.Add(rows); body.Children.Add(expand);
        var card = Card(body);
        cardRef = card;
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
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
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
        Put(Text(ClockFormat.Clock(r.Start), 12, DimB), 0, new Thickness(8, 0, 0, 0));
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

    bool _playlistsExpanded;

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
        foreach (var (b, d, fav, _) in shown)
        {
            var theme = ParseBrush(b.Color);
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Text(b.BenchmarkName, 14, FgB, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            info.Children.Add(title);
            info.Children.Add(Text(d.DifficultyName, 12, DimB, null, new Thickness(0, 2, 0, 0)));
            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0)
                info.Children.Add(Text(TierText.Label(p.OverallRankName), 12, ChartPaths.TextTone(RankBrush(d, p.OverallRankName)), FontWeights.SemiBold, new Thickness(0, 2, 0, 0)));
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
