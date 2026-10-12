using System.Windows.Threading;
using System.IO;
using System.Windows.Media.Imaging;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Session;

namespace KovaaksCompanion.App;

/// <summary>The Home page (the whole Stats page): search, current tier, playlists, activity and play sessions, built in code.</summary>
public partial class StatsView
{
    /// <summary>Legacy: pinned-tier cards on Home and the pin button in the playlist popup. Off; code kept for reference.</summary>
    static readonly bool PinnedTiersEnabled = false;

    const int SessionRunsShown = 7;
    const int SessionsPerPage = 5;
    int _sessionPage = 0;
    Border? _heroSlot, _playlistsSlot;
    StackPanel? _sessionsPanel;

    void ShowHome()
    {
        FitHome();
        var keep = HomeScroll.VerticalOffset;
        HomeHost.Children.Clear();
        HomeHost.Children.Add(BuildHome());
        RefreshProfile();
        if (_listOpen && _listKind == "Sessions") FillList();
        HomeScroll.UpdateLayout();
        HomeScroll.ScrollToVerticalOffset(keep);
        UpdateLivePill();
        RefreshResults();
    }

    FrameworkElement? _liveCard;
    bool _livePillOn, _liveShown;
    DateTime? _newestEnd;

    // Closed until the game runs under this app, so sessions from before launch are never live.
    bool _gameWasActive;
    DateTime _liveSince = DateTime.Now;

    /// <summary>The newest session is live while the game runs: a session is one game launch.</summary>
    bool LiveNow() => _host.GameActive;

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
        if (_playlistsSlot == null) return;
        if (_heroSlot != null) _heroSlot.Child = BuildTierHero();
        if (_listOpen) FillList();
        RefreshResults();
    }

    /// <summary>Local runs that beat every earlier local run and server score of their scenario.</summary>
    IReadOnlySet<RunRecord> PbSet() => _book.PersonalBestRuns;

    UIElement BuildHome()
    {
        var page = new StackPanel();
        _sessionsPanel = null;
        var runs = _lib.AllRuns.OrderByDescending(r => r.End).ToList();
        var pbs = PbSet();
        var today = DateTime.Today;

        if (PinnedTiersEnabled)
        {
            _heroSlot = new Border { Child = BuildTierHero() };
            page.Children.Add(_heroSlot);
        }
        else
        {
            _heroSlot = null;
        }
        _playlistsSlot = new Border { Child = PlaylistsButton() };
        page.Children.Add(_playlistsSlot);
        if (runs.Count > 0)
        {
            page.Children.Add(FadeTitle("Sessions", 40));
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            var day = _selDay is { } d0 && plays.ContainsKey(d0) ? d0 : plays.Keys.Max();
            _selDay = day;
            var dayList = new StackPanel();
            FillDay(dayList, day, pbs);
            var dayScroll = new ScrollViewer { Content = dayList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Height = 0, Style = (Style)FindResource("PinnedScroll") };
            var activityCard = ActivityCard(plays, ChartPaths.TextTone((Brush)FindResource("Accent")), null, dayScroll, cal =>
            {
                cal.Cursor = Cursors.Hand;
                cal.SelectedDay = day;
                cal.SizeChanged += (_, _) => dayScroll.Height = cal.ActualHeight;
                cal.DayClicked += clicked =>
                {
                    _selDay = clicked.Date;
                    cal.SelectedDay = clicked;
                    FillDay(dayList, clicked.Date, pbs);
                    dayScroll.ScrollToTop();
                };
                cal.PbDays = _book.PersonalBestDays;
            }, calendarShare: 2.4, maxCell: 42, maxWeeks: 24);

            var sessions = PlaySession.Group(_lib.AllRuns.Where(r => !IsHidden(r)), all: _lib.AllRuns, personalBests: pbs);
            var sessionsList = new StackPanel();
            var pagerPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

            var hasEmptyLive = LiveNow() && (sessions.Count == 0 || sessions[0].End < _liveSince);
            var totalPages = (sessions.Count + (hasEmptyLive ? 1 : 0) + SessionsPerPage - 1) / SessionsPerPage;
            if (_sessionPage >= totalPages) _sessionPage = Math.Max(0, totalPages - 1);

            void FillSessions()
            {
                sessionsList.Children.Clear();
                _liveCard = null;
                _newestEnd = sessions.Count > 0 ? sessions[0].End : null;
                var liveNow = LiveNow();
                _liveShown = liveNow;
                // No run yet this game launch: show an empty live session on top.
                var shown = sessions;
                if (liveNow && (sessions.Count == 0 || sessions[0].End < _liveSince))
                    shown = [new PlaySession([], 0) { Number = sessions.Count > 0 ? sessions[0].Number + 1 : 1, Opened = _liveSince }, .. sessions];
                var live = shown.Count > 0 && _sessionPage == 0 && liveNow;
                var start = _sessionPage * SessionsPerPage;
                var end = Math.Min(start + SessionsPerPage, shown.Count);
                for (var i = start; i < end; i++) sessionsList.Children.Add(SessionCard(shown[i], pbs, i == 0 && live));
                if (live && sessionsList.Children.Count > 0) _liveCard = sessionsList.Children[0] as FrameworkElement;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateLivePill);

                pagerPanel.Children.Clear();
                if (totalPages > 1)
                {
                    var pagerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    var prevBtn = new Button { Style = (Style)FindResource("PopupClose"), Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), Content = "\uE76B", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };
                    prevBtn.IsEnabled = _sessionPage > 0;
                    prevBtn.Opacity = _sessionPage > 0 ? 1.0 : 0.3;
                    prevBtn.Click += (_, _) => { _sessionPage--; FillSessions(); };
                    pagerRow.Children.Add(prevBtn);

                    var visible = Math.Min(5, totalPages);
                    var pageStart = Math.Clamp(_sessionPage - 2, 0, totalPages - visible);
                    var pageEnd = pageStart + visible;

                    for (var p = pageStart; p < pageEnd; p++)
                    {
                        var pageNum = p + 1;
                        var btn = new Button { Style = (Style)FindResource("PopupClose"), Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), Content = pageNum.ToString(), FontSize = 13, Foreground = p == _sessionPage ? (Brush)FindResource("Accent") : (Brush)FindResource("Dim") };
                        if (p == _sessionPage) btn.FontWeight = FontWeights.SemiBold;
                        else btn.Opacity = 0.55;
                        var pageIdx = p;
                        btn.Click += (_, _) => { _sessionPage = pageIdx; FillSessions(); };
                        pagerRow.Children.Add(btn);
                    }

                    var nextBtn = new Button { Style = (Style)FindResource("PopupClose"), Width = 30, Height = 30, Padding = new Thickness(0), Margin = new Thickness(2, 0, 2, 0), Content = "\uE76C", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };
                    nextBtn.IsEnabled = _sessionPage < totalPages - 1;
                    nextBtn.Opacity = _sessionPage < totalPages - 1 ? 1.0 : 0.3;
                    nextBtn.Click += (_, _) => { _sessionPage++; FillSessions(); };
                    pagerRow.Children.Add(nextBtn);

                    pagerPanel.Children.Add(pagerRow);
                }
            }
            FillSessions();

            if (activityCard is Border activityBorder)
            {
                activityBorder.Padding = new Thickness(8, 16, 8, 8);
                var wrapper = new StackPanel();
                var inner = activityBorder.Child;
                activityBorder.Child = null;
                wrapper.Children.Add(BuildPlayStats(runs, plays));
                wrapper.Children.Add(new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(16, 24, 16, 40) });
                wrapper.Children.Add(inner ?? new Border());
                _sessionsPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
                _sessionsPanel.Children.Add(sessionsList);
                _sessionsPanel.Children.Add(pagerPanel);
                activityBorder.Child = wrapper;
            }
            page.Children.Add(activityCard);
        }
        return page;
    }

    /// <summary>One row above the calendar: days played, total scenario time, run count, total personal bests and playlists with every scenario at its top tier (one unplayed scenario makes it incomplete).</summary>
    UIElement BuildPlayStats(IReadOnlyList<RunRecord> runs, IReadOnlyDictionary<DateTime, int> plays, double valueSize = 32, double labelSize = 16, FontWeight? valueWeight = null, Brush? ink = null)
    {
        var days = plays.Keys.Order().ToList();
        var time = TimeSpan.FromTicks(runs.Sum(r => r.Duration.Ticks));
        var done = AllDifficulties().Select(x => x.D.KovaaksBenchmarkId).Distinct()
            .Where(id => _progress.ContainsKey(id))
            .Sum(id =>
            {
                var p = _progress[id];
                return Enumerable.Range(1, Math.Max(0, p.RankNames.Count - 1)).Count(p.IsTierComplete);
            });
        var cells = new List<(string Label, string Value)>
        {
            (L.T("Days"), $"{days.Count}"),
            (L.T("Time"), time.TotalHours > 1 ? $"{(int)time.TotalHours}h" : Dur(time)),
            (L.T("Plays"), $"{_book.RecordCount}"),
        };
        cells.Add((L.T("PB"), $"{_book.PersonalBestCount}"));
        cells.Add((L.T("Complete"), $"{done}"));
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sep = new Border { Width = 1, Margin = new Thickness(0, 6, 0, 6), Background = Solid("#14FFFFFF") };
                Grid.SetColumn(sep, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(sep);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(Text(cells[i].Value, valueSize, ink ?? FgB, valueWeight ?? FontWeights.SemiBold));
            cell.Children.Add(Text(cells[i].Label, labelSize, ink ?? FgB, null, new Thickness(0, 2, 0, 0)));
            ((TextBlock)cell.Children[0]).HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)cell.Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(cell, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(cell);
        }
        return grid;
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
        Register("search", SearchPopup, CloseSearch, () => { SearchBox.Focus(); Keyboard.Focus(SearchBox); });
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
        Popups.Closed((this, "search"));
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
        var content = q.Length == 0 ? BuildFavorites() : BuildResults(q);
        SearchScroll.Content = content;
        SearchScroll.Visibility = content == null ? Visibility.Collapsed : Visibility.Visible;
        SearchScroll.ScrollToTop();
    }

    /// <summary>The empty-query list: favorite scenarios and favorite runs, newest favorite first; null when there are none.</summary>
    UIElement? BuildFavorites()
    {
        var ui = _host.Ui;
        var scenarios = ui.FavoriteScenarios.Reverse().ToList();
        var runs = ui.FavoriteRuns.Reverse().Select(Core.UiState.ParseRunKey).OfType<(string Scenario, DateTime End)>().ToList();
        if (scenarios.Count == 0 && runs.Count == 0) return null;
        var list = new StackPanel();
        UIElement Caption(string t, bool first) => Text(L.T(t).ToUpperInvariant(), 11, DimB, null, new Thickness(16, first ? 4 : 14, 16, 4));
        if (scenarios.Count > 0)
        {
            list.Children.Add(Caption("Favorite scenarios", true));
            foreach (var n in scenarios)
            {
                var s = _book.For(n);
                var name = n;
                list.Children.Add(ResultRow(null, n, n, s.Plays == 0 && s.Best == 0 ? L.T("Not played") : L.F("{0} plays · best {1}", s.Plays, s.Best.ToString("0.#")), "", DimB, p => { CloseSearch(); _host.ShowScenario(name, p); }, tier: TierOf(n, s.Best)));
            }
        }
        if (runs.Count > 0)
        {
            list.Children.Add(Caption("Favorite plays", scenarios.Count == 0));
            foreach (var (scenario, end) in runs)
            {
                var sc = scenario; var e = end;
                var run = _lib.AllRuns.FirstOrDefault(r => r.End == end && string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
                var sub = $"{end:yyyy-MM-dd} {ClockFormat.Clock(end)}" + (run is null ? "" : $" · {run.Score:0.#}");
                list.Children.Add(ResultRow(null, scenario, scenario, sub, "", DimB, p => { CloseSearch(); if (!_host.ShowRun(sc, e)) _host.ShowScenario(sc, p); }, tier: run is null ? null : TierOf(scenario, run.Score)));
            }
        }
        return list;
    }

    UIElement BuildResults(string q)
    {
        bool Match(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        bool NameMatch(Benchmark b) => Match(b.BenchmarkName) || Match(b.Abbreviation);
        var ui = _host.Ui;
        var playlists = BenchmarkCatalog.All.Where(b => NameMatch(b) || b.Difficulties.Any(d => Match(d.DifficultyName)))
            .OrderByDescending(b => b.Difficulties.Any(d => ui.IsFavorite($"{d.KovaaksBenchmarkId}"))).ThenByDescending(NameMatch).Take(12).ToList();
        var scenarios = _lib.Scenarios.Where(Match).OrderByDescending(ui.IsFavoriteScenario).Take(30).ToList();

        var body = new List<UIElement>();
        UIElement Caption(string t, bool first) => Text(L.T(t).ToUpperInvariant(), 11, DimB, null, new Thickness(16, first ? 4 : 14, 16, 4));
        if (playlists.Count > 0)
        {
            body.Add(Caption("Playlists", true));
            foreach (var b in playlists)
            {
                var bb = b;
                // Find a ranked difficulty for this playlist to get tier dot, otherwise use grey dot
                Brush tierBrush = DimB;
                foreach (var d in b.Difficulties)
                {
                    if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0)
                    {
                        tierBrush = RankBrush(d, p.OverallRankName);
                        break;
                    }
                }
                var theme = ParseBrush(b.Color);
                body.Add(ResultRow(tierBrush, b.BenchmarkName, b.BenchmarkName, ItemSubtitle(b), "", Lighten(theme, 0.45), _ => { CloseSearch(); OpenPlaylist(bb); }));
            }
        }
        if (scenarios.Count > 0)
        {
            body.Add(Caption("Scenarios", playlists.Count == 0));
            foreach (var n in scenarios)
            {
                var s = _book.For(n);
                var name = n;
                body.Add(ResultRow(null, n, n, L.F("{0} plays · best {1}", s.Plays, s.Best.ToString("0.#")), "", DimB, p => { CloseSearch(); _host.ShowScenario(name, p); }, tier: TierOf(n, s.Best)));
            }
        }
        if (body.Count == 0) body.Add(Text(L.T("No matches"), 13, DimB, null, new Thickness(16, 4, 16, 12)));
        var list = new StackPanel();
        foreach (var e in body) list.Children.Add(e);
        return list;
    }

    /// <summary>The highest tier a score reaches in the scenario; null without tiers or below the first.</summary>
    ChartBand? TierOf(string scenario, double score) => TierBands(scenario)?.LastOrDefault(b => score >= b.Value);

    Border ResultRow(Brush? tierBrush, string benchmarkId, string title, string sub, string mark, Brush markBrush, Action<Point?> click, bool fav = false, ChartBand? tier = null)
    {
        // Same look as the playlist card: tier name in the tier colour, card washed with the tier colour toward the right.
        Brush restBg = Brushes.Transparent;
        if (tier?.Brush is SolidColorBrush tb)
        {
            var c = tb.Color;
            restBg = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0x05, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x38, c.R, c.G, c.B), 1) }, new Point(0, 0), new Point(1, 0));
            restBg.Freeze();
        }
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };

        // Title with optional tier dot
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (tierBrush != null)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = tierBrush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(dot);
        }
        if (fav) titleStack.Children.Add(new TextBlock { Text = "\uE735", FontFamily = new FontFamily(Icons), FontSize = 12, Foreground = Solid("#FFD60A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var t = Text(title, 13.5, FgB, FontWeights.SemiBold);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        titleStack.Children.Add(t);
        info.Children.Add(titleStack);

        var st = Text(sub, 11.5, DimB, null, new Thickness(0, 2, 0, 0));
        st.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(st);
        if (tier != null) info.Children.Add(Text(tier.Label, 12, ChartPaths.TextTone(tier.Brush), FontWeights.Bold, new Thickness(0, 2, 0, 0)));
        g.Children.Add(info);
        if (mark.Length > 0)
        {
            var m = Text(mark, 16, markBrush, FontWeights.SemiBold, new Thickness(12, 0, 12, 0));
            m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 1);
            g.Children.Add(m);
        }
        var row = new Border { MinHeight = 48, CornerRadius = new CornerRadius(14), Margin = new Thickness(8, 4, 8, 4), Padding = new Thickness(14, 4, 6, 4), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Background = restBg, Cursor = Cursors.Hand, Child = g };
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = restBg;
        RowClick.Attach(row, click, down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : restBg);
        return row;
    }

    // Current tier

    #region Legacy: pinned tiers

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
            sp.Children.Add(Text(L.T(text), 14, FgB, FontWeights.SemiBold));
            if (action != null)
            {
                var link = new Button { Content = L.T(action), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(0), MinHeight = 28, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent") };
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

        // WrapPanel of category-style pinned cards
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (b, d, p) in pinned)
        {
            var card = TierBadge(b, d, p);
            card.Margin = new Thickness(6);
            wrap.Children.Add(card);
        }

        return Card(wrap, new Thickness(12), CardGap);
    }

    /// <summary>Perceived luminance: (0.299R + 0.587G + 0.114B)/255.</summary>
    static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    /// <summary>Darken a color by about 18% (multiply RGB by ~0.82).</summary>
    static Color Darken(Color c, double factor = 0.82) =>
        Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    /// <summary>Convert RGB to HSV (H in [0,360), S in [0,1], V in [0,1]).</summary>
    static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
            h = max == r ? 60 * (((g - b) / delta) % 6) :
                max == g ? 60 * (((b - r) / delta) + 2) :
                60 * (((r - g) / delta) + 4);
        if (h < 0) h += 360;

        double s = max == 0 ? 0 : delta / max;
        double v = max;
        return (h, s, v);
    }

    /// <summary>Convert HSV to RGB.</summary>
    static Color FromHsv(double h, double s, double v)
    {
        h = h % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }

        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>Card for a pinned tier: category-card-style with tier-colored background.</summary>
    Border TierBadge(Benchmark b, Difficulty d, BenchmarkProgress p)
    {
        var brush = RankBrush(d, p.OverallRankName);
        static Color Col(Brush x) => x is SolidColorBrush s ? s.Color : Colors.Gray;
        var tierColor = Col(brush);
        var darkColor = Darken(tierColor);
        var lum = Luminance(tierColor);
        var isLight = lum > 0.6;

        // Diagonal gradient background (0,0 → 1,1) with alpha 0.92
        var bgGrad = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xEB, tierColor.R, tierColor.G, tierColor.B), 0),
                new GradientStop(Color.FromArgb(0xEB, darkColor.R, darkColor.G, darkColor.B), 1)
            }
        };
        bgGrad.Freeze();

        // Text colors based on luminance
        var textFg = isLight ? Solid("#E6000000") : Brushes.White;
        var textDim = isLight ? Solid("#99000000") : Solid("#B3FFFFFF");

        // Boosted tier colour for shadow text
        var (h, s, v) = ToHsv(tierColor);
        double sPrime = Math.Min(1, s * 1.25 + 0.10);
        double vPrime = Math.Min(1, v * 1.20 + 0.15);
        var boostedColor = FromHsv(h, sPrime, vPrime);
        var boostedBrush = new SolidColorBrush(boostedColor);
        boostedBrush.Freeze();

        // Next tier colour for reflection light
        var nextRankName = p.RankName(p.OverallRank + 1);
        var nextRankBrush = RankBrush(d, nextRankName);
        var nextTierColor = Col(nextRankBrush);
        var (nh, ns, nv) = ToHsv(nextTierColor);
        double nsPrime = Math.Min(1, ns * 1.25 + 0.10);
        double nvPrime = Math.Min(1, nv * 1.20 + 0.15);
        var nextBoostedColor = FromHsv(nh, nsPrime, nvPrime);
        var nextBoostedBrush = new SolidColorBrush(nextBoostedColor);
        nextBoostedBrush.Freeze();

        // Content grid (for light pool background)
        var contentGrid = new Grid();

        // Light pool: Rectangle with LinearGradientBrush (full width, bottom to top)
        var lightPool = new Rectangle
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        // LinearGradientBrush for the area light (bottom to top)
        var linearBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1),
            EndPoint = new Point(0, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xD9, nextBoostedColor.R, nextBoostedColor.G, nextBoostedColor.B), 0),
                new GradientStop(Color.FromArgb(0x4D, nextBoostedColor.R, nextBoostedColor.G, nextBoostedColor.B), 0.55),
                new GradientStop(Color.FromArgb(0x00, nextBoostedColor.R, nextBoostedColor.G, nextBoostedColor.B), 1)
            }
        };
        linearBrush.Freeze();
        lightPool.Fill = linearBrush;

        contentGrid.Children.Add(lightPool);

        // Content stack
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };

        // Line 1: Benchmark name (Headline style, centered, no margin)
        var benchName = new TextBlock
        {
            Text = b.BenchmarkName,
            Style = (Style)FindResource("Headline"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = textFg
        };
        stack.Children.Add(benchName);

        // Line 2: Tier name with 3D extrusion effect
        var tierLabel = TierText.Label(p.OverallRankName);
        var tierGrid = new Grid { Margin = new Thickness(0, 4, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };

        // Calculate reflection opacity
        double reflectionOpacity = 0.35 + 0.65 * (p.OverallRank >= p.RankNames.Count - 1 ? 1 : p.OverallFraction);

        // 3D extrusion: depth = 6 layers, back to front
        const int depth = 6;
        for (int i = depth; i >= 1; i--)
        {
            if (i == depth)
            {
                // Bottom face: next tier boosted color with opacity based on fraction
                var bottomFace = new TextBlock
                {
                    Text = tierLabel,
                    FontSize = 34,
                    FontWeight = FontWeights.Bold,
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    Foreground = nextBoostedBrush,
                    RenderTransform = new TranslateTransform(0, i),
                    Opacity = reflectionOpacity
                };
                tierGrid.Children.Add(bottomFace);
            }
            else
            {
                // Side walls: darkened tier color, interpolating from 0.55 to 0.30
                double darkFactor = 0.55 - (0.55 - 0.30) * ((double)(depth - i) / (depth - 1));
                var sideWall = new TextBlock
                {
                    Text = tierLabel,
                    FontSize = 34,
                    FontWeight = FontWeights.Bold,
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    Foreground = new SolidColorBrush(Darken(tierColor, darkFactor)),
                    RenderTransform = new TranslateTransform(0, i),
                    Opacity = 1.0
                };
                tierGrid.Children.Add(sideWall);
            }
        }

        // Main face on top: Boosted tier colour, no transform
        var tierText = new TextBlock
        {
            Text = tierLabel,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            FontFamily = (FontFamily)FindResource("UiFont"),
            Foreground = boostedBrush
        };
        tierGrid.Children.Add(tierText);

        stack.Children.Add(tierGrid);

        // Line 3: Percentage or Max (11.5, centered)
        var top = p.OverallRank >= p.RankNames.Count - 1;
        var percentText = Text(top ? L.T("Max") : $"{p.OverallFraction * 100:0}%", 11.5, textDim, null, new Thickness(0, 8, 0, 0));
        percentText.HorizontalAlignment = HorizontalAlignment.Center;
        percentText.TextAlignment = TextAlignment.Center;
        stack.Children.Add(percentText);

        // Set stack properties for centering
        stack.HorizontalAlignment = HorizontalAlignment.Center;

        contentGrid.Children.Add(stack);

        // Update light pool properties on size change
        Action updateLightPool = () =>
        {
            if (double.IsNaN(contentGrid.ActualWidth) || contentGrid.ActualWidth <= 0) return;
            double fraction = p.OverallRank >= p.RankNames.Count - 1 ? 1 : p.OverallFraction;
            lightPool.Height = 28 + 72 * fraction;
            lightPool.Opacity = 0.45 + 0.55 * fraction;
        };
        contentGrid.SizeChanged += (_, _) => updateLightPool();

        // Clip to rounded rectangle (RadiusX/Y = 5, which is card CornerRadius 6 minus 1px border)
        var clipGeometry = new RectangleGeometry { RadiusX = 5, RadiusY = 5 };
        contentGrid.SizeChanged += (_, ev) =>
        {
            clipGeometry.Rect = new Rect(0, 0, ev.NewSize.Width, ev.NewSize.Height);
        };
        contentGrid.Clip = clipGeometry;

        // Tooltip
        var completion = $"{p.OverallFraction * 100:0.00}%";
        var tooltip = $"{b.BenchmarkName} · {d.DifficultyName} · {completion}";

        // Card border
        var card = new Border
        {
            Width = 220,
            CornerRadius = new CornerRadius(6),
            Background = bgGrad,
            BorderBrush = Solid("#26FFFFFF"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Child = contentGrid,
            ToolTip = tooltip
        };

        // Hover: brighten border
        var originalBorder = card.BorderBrush;
        card.MouseEnter += (_, _) => card.BorderBrush = Solid("#4DFFFFFF");
        card.MouseLeave += (_, _) => card.BorderBrush = originalBorder;

        Clickable(card, () => OpenPlaylist(b, d));

        return card;
    }

    #endregion

    // Play sessions

    DateTime? _selDay;

    /// <summary>The sessions that have a run on <paramref name="day"/>, one clickable row each (opens the session popup), under a day heading.</summary>
    void FillDay(StackPanel host, DateTime day, IReadOnlySet<RunRecord> pbs)
    {
        host.Children.Clear();
        var sessions = PlaySession.Group(_lib.AllRuns.Where(r => !IsHidden(r)), all: _lib.AllRuns, personalBests: pbs).Where(s => s.Runs.Any(r => r.End.Date == day)).ToList();
        host.Children.Add(Text(DayLabel(day) + " · " + (sessions.Count == 1 ? L.F("{0} session", sessions.Count) : L.F("{0} sessions", sessions.Count)), 15, FgB, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
        foreach (var s in sessions)
        {
            var pbCount = s.Runs.Count(pbs.Contains);
            var g = new Grid { Margin = new Thickness(10, 0, 10, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (pbCount >= 1) g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
            var rowTitle = new TextBlock();
            rowTitle.Inlines.Add(new System.Windows.Documents.Run($"#{s.Number}") { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = FgB });
            rowTitle.Inlines.Add(new System.Windows.Documents.Run("  " + ClockFormat.Range(s.Start, s.End)) { FontSize = 12.5, FontWeight = FontWeights.Normal, Foreground = DimB });
            info.Children.Add(rowTitle);
            info.Children.Add(Text((s.RunCount == 1 ? L.F("{0} play", s.RunCount) : L.F("{0} plays", s.RunCount)) + " · " + (s.ScenarioCount == 1 ? L.F("{0} scenario", s.ScenarioCount) : L.F("{0} scenarios", s.ScenarioCount)) + $" · {s.AverageAccuracy:P0}", 12, DimB, null, new Thickness(0, 2, 0, 0)));
            g.Children.Add(info);
            if (pbCount >= 1)
            {
                var green = (Brush)FindResource("Green");
                var badge = new Grid { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = green, Opacity = 0.35 });
                badge.Children.Add(new TextBlock { Text = pbCount >= 2 ? L.F("PB ×{0}", pbCount) : L.T("PB"), Foreground = green, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 3, 10, 3) });
                Grid.SetColumn(badge, 1);
                g.Children.Add(badge);
            }
            var chev = Chevron(HorizontalAlignment.Right);
            chev.FontSize = 10;
            Grid.SetColumn(chev, pbCount >= 1 ? 2 : 1);
            g.Children.Add(chev);
            var row = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g, Margin = new Thickness(0, 0, 0, 2) };
            var sess = s;
            RowClick.Attach(row, p => _host.ShowSession(sess, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
            row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            host.Children.Add(row);
        }
    }

    static string DayLabel(DateTime day) =>
        day == DateTime.Today ? L.T("Today") : day == DateTime.Today.AddDays(-1) ? L.T("Yesterday") : day.ToString("ddd, MMM d", L.Culture);

    /// <summary>Renders the whole session (every run, not just the visible ones) to the clipboard and a png in the captures folder.</summary>
    void CopySessionImage(FrameworkElement source, PlaySession s, IReadOnlySet<RunRecord> pbs)
    {
        try
        {
            const double width = 960;
            var card = SessionCard(s, pbs, false, expanded: true, capture: true);
            var holder = new Border { Background = CaptureBg(), Padding = new Thickness(8), Child = card, Width = width };
            holder.Resources.MergedDictionaries.Add(Resources);
            holder.Measure(new Size(width, double.PositiveInfinity));
            holder.Arrange(new Rect(0, 0, width, holder.DesiredSize.Height));
            holder.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(source);
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX), (int)Math.Ceiling(holder.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            rtb.Render(holder);
            rtb.Freeze();
            Clipboard.SetImage(rtb);
            var dir = _host.Settings.CapturesFolder;
            Directory.CreateDirectory(dir);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(System.IO.Path.Combine(dir, $"session_{s.Number}_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
            enc.Save(fs);
        }
        catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); }
    }

    UIElement SessionCard(PlaySession s, IReadOnlySet<RunRecord> pbs, bool live, bool expanded = false, bool capture = false)
    {
        var pbCount = s.Runs.Count(pbs.Contains);
        var head = new Grid { Margin = new Thickness(24, 3, 24, 3) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new TextBlock();
        title.Inlines.Add(new System.Windows.Documents.Run($"#{s.Number}") { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = FgB });
        title.Inlines.Add(new System.Windows.Documents.Run($"  {DayLabel(s.Start.Date)} · " + ClockFormat.Range(s.Start, s.End)) { FontSize = 12, Foreground = DimB });

        var titleCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        if (live)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = Brushes.White, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.3, TimeSpan.FromSeconds(1)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            var pillRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 8, 1) };
            pillRow.Children.Add(dot);
            pillRow.Children.Add(Text("LIVE", 10.5, Brushes.White, FontWeights.Bold));
            var pill = new Border { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(8), Background = Solid("#FF453A"), Child = pillRow };
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(title); line.Children.Add(pill);
            titleCol.Children.Add(line);
        }
        else titleCol.Children.Add(title);

        Grid.SetColumn(titleCol, 0);
        head.Children.Add(titleCol);

        var stats = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Foreground = DimB, TextAlignment = TextAlignment.Right };
        stats.Inlines.Add(new System.Windows.Documents.Run(L.F("{0} · {1} · {2} scenarios · {3} plays · ", Dur(s.Span), Dur(s.Duration), s.ScenarioCount, s.RunCount)));
        stats.Inlines.Add(new System.Windows.Documents.Run(L.F("{0} PBs", pbCount)) { Foreground = pbCount > 0 ? (Brush)FindResource("Green") : DimB });
        Grid.SetColumn(stats, 1);
        head.Children.Add(stats);
        if (!capture && s.Runs.Count > 0)
        {
            var copy = new Button
            {
                Content = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Background = Brushes.Transparent, Padding = new Thickness(10, 0, 10, 0),
                Margin = new Thickness(6, 0, 0, 0), MinHeight = 36, MinWidth = 36, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Copy session as image"),
            };
            copy.Click += async (_, _) =>
            {
                CopySessionImage(copy, s, pbs);
                var icon = copy.Content;
                copy.Content = "";
                await Task.Delay(1200);
                copy.Content = icon;
            };
            Grid.SetColumn(copy, 2);
            head.Children.Add(copy);
        }

        var headHit = new Border { Background = Brushes.Transparent, Child = head, CornerRadius = new CornerRadius(14, 14, 0, 0) };
        var rows = new StackPanel { Margin = new Thickness(20, 0, 20, 0) };
        var runScroll = new ScrollViewer { Focusable = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, CanContentScroll = false, Content = rows };
        var expand = new Button { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 0, 8, 0), MinHeight = 26, MinWidth = 30, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent"), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Style = (Style)FindResource("PopupClose") };
        var all = expanded;
        void Fill()
        {
            rows.Children.Clear();
            foreach (var r in s.Runs.OrderByDescending(x => x.End).Take(all ? int.MaxValue : SessionRunsShown)) rows.Children.Add(RunRow(r, pbs.Contains(r)));
            expand.Visibility = (s.RunCount > SessionRunsShown && !expanded) ? Visibility.Visible : Visibility.Collapsed;
            expand.Content = all ? "" : "";
            if (expanded)
            {
                runScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                runScroll.MaxHeight = double.PositiveInfinity;
            }
            else
            {
                runScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                runScroll.MaxHeight = double.PositiveInfinity;
            }
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

        var sep = new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(28, 0, 28, 4) };
        var bodyGrid = new Grid();
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        bodyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(headHit, 0);
        bodyGrid.Children.Add(headHit);
        Grid.SetRow(sep, 1);
        bodyGrid.Children.Add(sep);
        Grid.SetRow(runScroll, 2);
        bodyGrid.Children.Add(runScroll);
        Grid.SetRow(expand, 3);
        bodyGrid.Children.Add(expand);

        UIElement result;
        if (expanded)
        {
            result = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(14), Padding = new Thickness(8), Child = bodyGrid };
        }
        else
        {
            result = Card(bodyGrid, new Thickness(8, 6, 8, 4), CardGap);
        }
        cardRef = result;
        if (live)
        {
            if (result is Border b)
            {
                var red = Solid("#FF453A");
                b.BorderBrush = Alpha(red, 0.6);
                b.BorderThickness = new Thickness(1.5);
                b.Background = CompositeWash(Alpha(red, 0.06));
            }
            else if (result is Border card)
            {
                var red = Solid("#FF453A");
                card.BorderBrush = Alpha(red, 0.6);
                card.BorderThickness = new Thickness(1.5);
                card.Background = CompositeWash(Alpha(red, 0.06));
            }
        }
        return result;
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
        var bands = TierBands(name);
        var tier = bands?.LastOrDefault(b => r.Score >= b.Value);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
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
        Put(nm, 1, new Thickness(4, 0, 16, 0));
        var bench = tier != null ? TierBenchmarkName(name) : null;
        var benchText = Text(bench ?? "-", 12, DimB);
        benchText.TextTrimming = TextTrimming.CharacterEllipsis;
        Put(benchText, 2, new Thickness(8, 0, 8, 0), HorizontalAlignment.Center);
        var ac = Text(tier?.Label ?? "-", 12, tier != null ? ChartPaths.TextTone(tier.Brush) : DimB, FontWeights.SemiBold);
        Put(ac, 3, new Thickness(0), HorizontalAlignment.Center);
        var scoreFg = tier != null ? ChartPaths.TextTone(tier.Brush) : FgB;
        var sc = Text(r.Score.ToString("0.#"), 13, scoreFg, FontWeights.SemiBold);
        System.Windows.Documents.Typography.SetNumeralAlignment(sc, FontNumeralAlignment.Tabular);
        Put(sc, 4, new Thickness(0), HorizontalAlignment.Right);
        if (pb)
        {
            var green = (Brush)FindResource("Green");
            var pill = new Grid();
            pill.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = green, Opacity = 0.18 });
            pill.Children.Add(Text("PB", 10.5, green, FontWeights.SemiBold, new Thickness(8, 1, 8, 1)));
            Put(pill, 5, new Thickness(10, 0, 0, 0));
        }
        var row = new Border { Height = 40, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
        if (replay)
        {
            var playText = Text("", 15, (Brush)FindResource("Accent"));
            playText.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            playText.HorizontalAlignment = HorizontalAlignment.Center;
            playText.VerticalAlignment = VerticalAlignment.Center;
            Put(playText, 6, new Thickness(0));
            RowClick.Attach(row, p => _host.ShowRun(name, end), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        }
        else if (_host.IsPending(name, end))
        {
            Put(ProgressRing.Create(_host, name, end, (Brush)FindResource("Accent"), 16), 6, new Thickness(0), HorizontalAlignment.Center);
            RowClick.Attach(row, p => _host.ShowRun(name, end), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        }
        else
        {
            RowClick.Attach(row, p => _host.ShowScenario(name, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        }
        Put(DeleteRunButton(r), 7, new Thickness(0));
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>Trash button of a run row: the first click arms it (red), the second deletes the run.</summary>
    Button DeleteRunButton(RunRecord r)
    {
        var icon = new TextBlock { Text = "\uE74D", FontFamily = new FontFamily(Icons), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var btn = new Button { Style = (Style)FindResource("PopupClose"), Width = 30, Height = 30, Padding = new Thickness(0), Content = icon, ToolTip = L.T("Delete play"), Foreground = DimB };
        var armed = false;
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            if (!armed)
            {
                armed = true;
                btn.Foreground = (Brush)FindResource("Red");
                btn.ToolTip = L.T("Click again to delete the replay for good");
                return;
            }
            DeleteRun(r);
        };
        btn.MouseLeave += (_, _) => { armed = false; btn.Foreground = DimB; btn.ToolTip = L.T("Delete play"); };
        return btn;
    }

    /// <summary>Deletes the run's session folder for good, keeps its score in the cache like a server score and hides it from the lists.</summary>
    async void DeleteRun(RunRecord r)
    {
        var sessions = _host.SessionsFolder;
        var cache = Cache;
        try
        {
            await Task.Run(() =>
            {
                SessionStore.Delete(sessions, r.Scenario, r.End);
                cache.HideRun(r.Scenario, r.End, r.Score);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Reload();
    }

    // Playlists

    bool _listOpen;
    string _listKind = "Playlists";
    int _listTok;

    /// <summary>When any scenario of the playlist was last played, or null.</summary>
    DateTime? LastPlayed(int id)
    {
        DateTime? m = null;
        foreach (var n in _index.Scenarios(id))
            if (_lib.Runs(n) is { Count: > 0 } l && (m == null || l[^1].End > m)) m = l[^1].End;
        return m;
    }

    /// <summary>Every playlist: favorites first, then most recently played, then highest tier progress.</summary>
    List<(Benchmark B, Difficulty D, bool Fav)> SortedPlaylists() =>
        AllDifficulties()
            .Select(x =>
            {
                _progress.TryGetValue(x.D.KovaaksBenchmarkId, out var p);
                var ranked = p != null && p.OverallRankName.Length > 0;
                return (x.B, x.D, Fav: _host.Ui.IsFavorite($"{x.D.KovaaksBenchmarkId}"), Last: LastPlayed(x.D.KovaaksBenchmarkId),
                    Rank: ranked ? p!.OverallRank : -1, Frac: ranked ? p!.OverallFraction : 0.0);
            })
            .OrderByDescending(i => i.Fav).ThenByDescending(i => i.Last ?? DateTime.MinValue).ThenByDescending(i => i.Rank).ThenByDescending(i => i.Frac)
            .Select(i => (i.B, i.D, i.Fav)).ToList();

    /// <summary>The Home button that opens the playlists popup, next to the search button.</summary>
    UIElement PlaylistsButton()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        var kinds = new (string Kind, string Label, string Glyph)[] { ("Playlists", "Playlists", ""), ("Scenarios", "Scenarios", ""), ("Plays", "Replays", ""), ("Sessions", "Sessions", "") };
        foreach (var (kind, label, glyph) in kinds)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily(Icons), FontSize = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 10, 0) });
            content.Children.Add(new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center });
            var open = new Button { Content = content, MinHeight = 46, Padding = new Thickness(20, 0, 22, 0), Margin = new Thickness(0, 0, 10, 0), FontSize = 15, FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => OpenList(kind);
            row.Children.Add(open);
        }
        return row;
    }

    public void OpenList(string kind)
    {
        if (_listOpen) return;
        _listOpen = true;
        Register("list", ListPopup, CloseList, () => ListPopup.Focus());
        _listKind = kind;
        ListTitle.Text = L.T(kind == "Plays" ? "Replays" : kind);
        _listTok++;
        ListScrim.BeginAnimation(OpacityProperty, null); ListScrim.Opacity = 0;
        ListCard.BeginAnimation(OpacityProperty, null); ListCard.Opacity = 0;
        ListScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ListScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ListScale.ScaleX = ListScale.ScaleY = 0.94;
        FillList();
        ListPopup.Visibility = Visibility.Visible;
        Animate(true, null, ListScrim, ListCard, ListScale);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_listOpen) ListPopup.Focus(); });
    }

    public void CloseList()
    {
        if (!_listOpen) return;
        _listOpen = false;
        Popups.Closed((this, "list"));
        var tok = ++_listTok;
        Animate(false, () =>
        {
            if (tok != _listTok) return;
            ListPopup.Visibility = Visibility.Collapsed;
            ListScroll.Content = null;
        }, ListScrim, ListCard, ListScale);
    }

    void OnListScrim(object sender, MouseButtonEventArgs e) => CloseList();

    void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseList();
    }

    /// <summary>Fills the popup for the current kind, keeping the scroll position when redrawn.</summary>
    void FillList()
    {
        var offset = ListScroll.VerticalOffset;
        if (_listKind == "Sessions")
        {
            ListScroll.Content = _sessionsPanel;
            ListScroll.UpdateLayout();
            ListScroll.ScrollToVerticalOffset(offset);
            return;
        }
        var grid = new UniformGrid { Columns = 3 };
        int Wide(double w, int max) => w < 520 ? 1 : w < 800 ? Math.Min(2, max) : max;
        var max = 3;
        if (_listKind == "Playlists")
            foreach (var (b, d, fav) in SortedPlaylists()) grid.Children.Add(PlaylistCard(b, d, fav));
        else
        {
            max = 2;
            foreach (var r in _listKind == "Scenarios" ? ScenarioRows() : RecordRows()) grid.Children.Add(r);
        }
        grid.Columns = max;
        grid.SizeChanged += (_, e) => grid.Columns = Wide(e.NewSize.Width, max);
        ListScroll.Content = new Border { Padding = new Thickness(TileGap / 2 - 2, 0, TileGap / 2 - 2, 0), Child = grid };
        ListScroll.UpdateLayout();
        ListScroll.ScrollToVerticalOffset(offset);
    }

    /// <summary>Rank thresholds and server best score per scenario, from every loaded benchmark.</summary>
    Dictionary<string, (IReadOnlyList<double> Maxes, double Score)> RankData()
    {
        var data = new Dictionary<string, (IReadOnlyList<double>, double)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in _progress.Values)
            foreach (var sc in p.Scenarios)
                if (sc.RankMaxes.Count > 0 && !data.ContainsKey(sc.Scenario)) data[sc.Scenario] = (sc.RankMaxes, sc.Score);
        return data;
    }

    /// <summary>Progress through the current tier (0..1) of a score: from the last threshold passed to the next one; 1 once the top tier is reached.</summary>
    static double TierProgress(IReadOnlyList<double> maxes, double score)
    {
        var rank = maxes.Count(m => score >= m);
        if (rank >= maxes.Count) return 1;
        var lower = rank == 0 ? 0 : maxes[rank - 1];
        var span = maxes[rank] - lower;
        return span > 0 ? Math.Clamp((score - lower) / span, 0, 1) : 0;
    }
    /// <summary>Sort key: scenarios below 100% first, highest percent first; 100% and unranked scenarios last.</summary>
    static double PercentKey(double? pct) => pct is { } v && v < 1 ? v : -1;

    /// <summary>The line under a scenario name: the playlist it belongs to (see <see cref="TierSource"/>), then <paramref name="text"/>.</summary>
    string WithPlaylist(string scenario, string text) => TierBenchmarkName(scenario) is { } name ? $"{name} · {text}" : text;

    IEnumerable<UIElement> ScenarioRows()
    {
        var ui = _host.Ui;
        var data = RankData();
        return _lib.Scenarios
            .Select(n =>
            {
                var s = _book.For(n);
                double? pct = data.TryGetValue(n, out var t) ? TierProgress(t.Maxes, Math.Max(s.Best, t.Score)) : null;
                return (N: n, S: s, Fav: ui.IsFavoriteScenario(n), Last: _lib.Runs(n) is { Count: > 0 } l ? l[^1].End : DateTime.MinValue, Pct: pct);
            })
            .OrderByDescending(i => i.Fav).ThenByDescending(i => i.Last).ThenByDescending(i => PercentKey(i.Pct))
            .Select(i =>
            {
                var name = i.N;
                var tier = TierOf(name, i.S.Best);
                return (UIElement)ResultRow(null, name, name, WithPlaylist(name, L.F("{0} plays", i.S.Plays)), i.S.Best > 0 ? i.S.Best.ToString("0.#") : "", tier is null ? DimB : ChartPaths.TextTone(tier.Brush),
                    p => { CloseList(); _host.ShowScenario(name, p); }, i.Fav, tier);
            });
    }

    IEnumerable<UIElement> RecordRows()
    {
        var ui = _host.Ui;
        var data = RankData();
        return _lib.AllRuns.Where(r => !IsHidden(r))
            .Select(r => (R: r, Fav: ui.IsFavoriteRun(r.Scenario, r.End), Pct: data.TryGetValue(r.Scenario, out var t) ? (double?)TierProgress(t.Maxes, r.Score) : null))
            .OrderByDescending(i => i.Fav).ThenByDescending(i => i.R.End).ThenByDescending(i => PercentKey(i.Pct))
            .Take(300)
            .Select(i =>
            {
                var (sc, end) = (i.R.Scenario, i.R.End);
                var tier = TierOf(sc, i.R.Score);
                return (UIElement)ResultRow(null, sc, sc, WithPlaylist(sc, L.Stamp(end)), i.R.Score.ToString("0.#"), tier is null ? DimB : ChartPaths.TextTone(tier.Brush),
                    p => { CloseList(); if (!_host.ShowRun(sc, end)) _host.ShowScenario(sc, p); }, i.Fav, tier);
            });
    }
    UIElement PlaylistCard(Benchmark b, Difficulty d, bool fav)
    {
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = ParseBrush(b.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        if (fav) titleStack.Children.Add(new TextBlock { Text = "\uE735", FontFamily = new FontFamily(Icons), FontSize = 13, Foreground = Solid("#FFD60A"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var title = Text(b.BenchmarkName, 14, FgB, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        titleStack.Children.Add(title);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(titleStack);
        info.Children.Add(Text(d.DifficultyName, 12, DimB, null, new Thickness(0, 2, 0, 0)));

        var ranked = _progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0;
        if (ranked)
        {
            var pct = p!.OverallRank >= p.RankNames.Count - 1 ? "100%" : $"{p.OverallFraction * 100:0}%";
            info.Children.Add(Text($"{TierText.Label(p.OverallRankName)} · {pct}", 12, ChartPaths.TextTone(RankBrush(d, p.OverallRankName)), FontWeights.Bold, new Thickness(0, 2, 0, 0)));
        }
        else info.Children.Add(Text(L.T("UNRANKED"), 12, new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x38)), FontWeights.Bold, new Thickness(0, 2, 0, 0)));

        var card = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(TileGap / 2), Cursor = Cursors.Hand };
        if (ranked && RankBrush(d, p!.OverallRankName) is SolidColorBrush rankBrush)
        {
            var c = rankBrush.Color;
            var grad = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops = new GradientStopCollection { new GradientStop(Color.FromArgb(0x05, 255, 255, 255), 0), new GradientStop(Color.FromArgb(0x38, c.R, c.G, c.B), 1) }
            };
            grad.Freeze();
            card.Background = grad;
            var overlay = new Border { CornerRadius = new CornerRadius(13), IsHitTestVisible = false, Background = new SolidColorBrush(Colors.Transparent) };
            var content = new Grid();
            content.Children.Add(overlay);
            content.Children.Add(info);
            card.Child = content;
            var dur = TimeSpan.FromMilliseconds(120);
            card.MouseEnter += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Theme.Ink(Color.FromArgb(0x12, 255, 255, 255)), dur));
            card.MouseLeave += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, dur));
        }
        else
        {
            card.Child = info;
            HoverFade(card, "#05FFFFFF", "#0FFFFFFF");
        }
        Clickable(card, () => OpenPlaylist(b, d));
        return card;
    }

    /// <summary>Favorite playlist card, sized like the playlist popup's activity card: name and difficulty on the left, the tier progress line on the right over a card-high tier-name watermark.</summary>
    UIElement FavoriteCard(Benchmark b, Difficulty d)
    {
        _progress.TryGetValue(d.KovaaksBenchmarkId, out var p);
        var ranked = p != null && p.OverallRankName.Length > 0;
        var tier = ranked ? RankBrush(d, p!.OverallRankName) : (Brush)FindResource("Dim");
        var history = p == null ? [] : Tier.History(p.Scenarios.Select(sc => (_book.For(sc.Scenario), (IReadOnlyList<double>)sc.RankMaxes)));
        var watermark = ranked ? TierText.Label(p!.OverallRankName) : "";
        var complete = p != null && p.IsTierComplete(p.OverallRank);
        return MiniCard(b.BenchmarkName, d.DifficultyName, ParseBrush(b.Color), tier, ranked, history, watermark, complete, p, () => OpenPlaylist(b, d));
    }

    /// <summary>Profile card: <paramref name="name"/> and <paramref name="sub"/> on the left, the tier progress line on the right over a card-high tier-name watermark. <paramref name="cloudOf"/> supplies the scenarios whose server scores fill in a line with no local runs.</summary>
    UIElement MiniCard(string name, string sub, Brush dot, Brush tier, bool ranked, List<(DateTime Day, double Value)> history, string watermark, bool complete, BenchmarkProgress? cloudOf, Action click)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = dot, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = Text(name, 32, ProfileTone, FontWeights.Light);
        title.FontFamily = new FontFamily("Segoe UI");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Opacity = 0.85;
        titleRow.Children.Add(title);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 0, 0) };
        info.Children.Add(titleRow);
        var diff = Text(sub, 22, ProfileTone, FontWeights.Light, new Thickness(20, 4, 0, 0));
        diff.FontFamily = new FontFamily("Segoe UI");
        diff.Opacity = 0.65;
        info.Children.Add(diff);

        var chart = new ScoreChart { IsHitTestVisible = false };
        chart.SetMini(history.Select(h => (h.Day, h.Value)).ToList(), tier, watermark, complete);
        if (cloudOf != null) _ = EnsureCloudAsync(cloudOf.Scenarios.Where(sc => sc.RankMaxes.Count > 0).Select(sc => sc.Scenario)); // a change redraws Home through the book
        chart.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.Transparent, 0.15), new GradientStop(Colors.White, 0.5) }, new Point(0, 0), new Point(1, 0));

        var overlay = new Border { CornerRadius = new CornerRadius(3), IsHitTestVisible = false, Background = new SolidColorBrush(Colors.Transparent) };
        var cell = new Grid();
        cell.Children.Add(overlay);
        cell.Children.Add(chart);
        cell.Children.Add(info);

        var card = new Border { CornerRadius = new CornerRadius(4), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Margin = new Thickness(TileGap / 2, TileGap / 2, TileGap / 2, TileGap / 2), Height = 100, Cursor = Cursors.Hand, Child = cell };
        cell.SizeChanged += (_, e) => cell.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 3, 3);
        if (tier is SolidColorBrush rb && ranked)
        {
            var c = rb.Color;
            var gradBrush = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0x9D, (byte)(c.R * 0.18), (byte)(c.G * 0.18), (byte)(c.B * 0.18)), 0), new GradientStop(Color.FromArgb(0x00, c.R, c.G, c.B), 1) }, new Point(0, 0), new Point(1, 1));
            gradBrush.Freeze();
            card.Background = gradBrush;
            var dur = TimeSpan.FromMilliseconds(120);
            card.MouseEnter += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Theme.Ink(Color.FromArgb(0x12, 255, 255, 255)), dur));
            card.MouseLeave += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, dur));
        }
        else HoverFade(card, "#0AFFFFFF", "#14FFFFFF");
        Clickable(card, click);
        return card;
    }

}

