using System.Windows.Threading;
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

    void ShowHome()
    {
        FitHome();
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
        if (_playlistsSlot == null) return;
        if (_heroSlot != null) _heroSlot.Child = BuildTierHero();
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

        if (PinnedTiersEnabled)
        {
            _heroSlot = new Border { Child = BuildTierHero() };
            page.Children.Add(_heroSlot);
        }
        else
        {
            _heroSlot = null;
        }
        _playlistsSlot = new Border { Child = BuildHomePlaylists() };
        page.Children.Add(_playlistsSlot);
        if (runs.Count > 0)
        {
            page.Children.Add(FadeTitle("Sessions"));
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            var day = _selDay is { } d0 && plays.ContainsKey(d0) ? d0 : plays.Keys.Max();
            _selDay = day;
            var dayList = new StackPanel();
            FillDay(dayList, day, pbs);
            var dayScroll = new ScrollViewer { Content = dayList, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Height = 0, Style = (Style)FindResource("PinnedScroll") };
            var activityCard = ActivityCard(plays, ChartPaths.TextTone((Brush)FindResource("Accent")), null, dayScroll, null, cal =>
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
                cal.PbDays = pbs.OfType<RunRecord>().Select(r => r.End.Date).ToHashSet();
            }, calendarShare: 2.4, maxCell: 42, maxWeeks: 24);

            var sessions = PlaySession.Group(_lib.AllRuns, all: _lib.AllRuns);
            var sessionsList = new StackPanel();
            var pagerPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };

            var totalPages = (sessions.Count + SessionsPerPage - 1) / SessionsPerPage;
            if (_sessionPage >= totalPages) _sessionPage = Math.Max(0, totalPages - 1);

            void FillSessions()
            {
                sessionsList.Children.Clear();
                _liveCard = null;
                _newestEnd = sessions.Count > 0 ? sessions[0].End : null;
                var live = sessions.Count > 0 && _sessionPage == 0 && LiveNow();
                _liveShown = LiveNow();
                var start = _sessionPage * SessionsPerPage;
                var end = Math.Min(start + SessionsPerPage, sessions.Count);
                for (var i = start; i < end; i++) sessionsList.Children.Add(SessionCard(sessions[i], pbs, i == 0 && live));
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
                wrapper.Children.Add(new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(16, 32, 16, 36) });
                var sessionsPanel = new StackPanel { Margin = new Thickness(16, 0, 16, 4) };
                sessionsPanel.Children.Add(sessionsList);
                sessionsPanel.Children.Add(pagerPanel);
                wrapper.Children.Add(sessionsPanel);
                activityBorder.Child = wrapper;
            }
            page.Children.Add(activityCard);
        }
        return page;
    }

    /// <summary>One row above the calendar: days played, longest daily streak, total scenario time and playlists at the top rank (unplayed scenarios included).</summary>
    UIElement BuildPlayStats(IReadOnlyList<RunRecord> runs, IReadOnlyDictionary<DateTime, int> plays)
    {
        var days = plays.Keys.Order().ToList();
        int best = 0, run = 0;
        for (var i = 0; i < days.Count; i++)
        {
            run = i > 0 && (days[i] - days[i - 1]).Days == 1 ? run + 1 : 1;
            best = Math.Max(best, run);
        }
        var time = TimeSpan.FromTicks(runs.Sum(r => r.Duration.Ticks));
        var done = AllDifficulties().Select(x => x.D.KovaaksBenchmarkId).Distinct()
            .Count(id => _progress.TryGetValue(id, out var p) && p.RankNames.Count >= 2 && p.OverallRank >= p.RankNames.Count - 1);
        var cells = new (string Label, string Value)[]
        {
            ("Days", $"{days.Count}"),
            ("Streak", $"{best}"),
            ("Time", time.TotalHours > 1 ? $"{(int)time.TotalHours}h" : Dur(time)),
            ("Complete", $"{done}")
        };
        var grid = new Grid { Margin = new Thickness(16, 12, 16, 12) };
        for (var i = 0; i < cells.Length; i++)
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
            cell.Children.Add(Text(cells[i].Value, 32, FgB, FontWeights.SemiBold));
            cell.Children.Add(Text(cells[i].Label, 16, DimB, null, new Thickness(0, 2, 0, 0)));
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
                var s = ScenarioStats.For(_lib, n);
                var name = n;
                body.Add(ResultRow(null, n, n, $"{s.Plays} plays · best {s.Best:0.#}", "", DimB, p => { CloseSearch(); _host.ShowScenario(name, p); }));
            }
        }
        if (body.Count == 0) body.Add(Text("No matches", 13, DimB, null, new Thickness(16, 4, 16, 12)));
        var list = new StackPanel();
        foreach (var e in body) list.Children.Add(e);
        return list;
    }

    Border ResultRow(Brush? tierBrush, string benchmarkId, string title, string sub, string mark, Brush markBrush, Action<Point?> click)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6) };

        // Title with optional tier dot
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (tierBrush != null)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = tierBrush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(dot);
        }
        var t = Text(title, 13.5, FgB, FontWeights.SemiBold);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        titleStack.Children.Add(t);
        info.Children.Add(titleStack);

        var st = Text(sub, 11.5, DimB, null, new Thickness(0, 2, 0, 0));
        st.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(st);
        g.Children.Add(info);
        if (mark.Length > 0)
        {
            var m = Text(mark, 12, markBrush, FontWeights.ExtraBold, new Thickness(0, 0, 12, 0));
            m.Opacity = 0.75; m.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(m, 1);
            g.Children.Add(m);
        }
        var row = new Border { MinHeight = 48, CornerRadius = new CornerRadius(10), Margin = new Thickness(8, 0, 8, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = g };
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        RowClick.Attach(row, click, down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
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
        var percentText = Text(top ? "Max" : $"{p.OverallFraction * 100:0}%", 11.5, textDim, null, new Thickness(0, 8, 0, 0));
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
        var completion = p.ProgressShare is { } ps ? $"{ps * 100:0.00}%" : $"{p.Progress:#,0} pts";
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
    void FillDay(StackPanel host, DateTime day, HashSet<object> pbs)
    {
        host.Children.Clear();
        var sessions = PlaySession.Group(_lib.AllRuns, all: _lib.AllRuns).Where(s => s.Runs.Any(r => r.End.Date == day)).ToList();
        host.Children.Add(Text($"{DayLabel(day)} · {sessions.Count} {(sessions.Count == 1 ? "session" : "sessions")}", 15, FgB, FontWeights.SemiBold, new Thickness(0, 0, 0, 8)));
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
            info.Children.Add(Text($"{s.RunCount} {(s.RunCount == 1 ? "run" : "runs")} · {s.ScenarioCount} {(s.ScenarioCount == 1 ? "scenario" : "scenarios")} · {s.AverageAccuracy:P0}", 12, DimB, null, new Thickness(0, 2, 0, 0)));
            g.Children.Add(info);
            if (pbCount >= 1)
            {
                var green = (Brush)FindResource("Green");
                var badge = new Grid { Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                badge.Children.Add(new Border { CornerRadius = new CornerRadius(9), Background = green, Opacity = 0.18 });
                badge.Children.Add(new TextBlock { Text = pbCount >= 2 ? $"PB ×{pbCount}" : "PB", Foreground = green, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 3, 10, 3) });
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
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd, MMM d", CultureInfo.InvariantCulture);

    UIElement SessionCard(PlaySession s, HashSet<object> pbs, bool live, bool expanded = false)
    {
        var pbCount = s.Runs.Count(pbs.Contains);
        var head = new Grid { Margin = new Thickness(24, 3, 24, 3) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
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

        var stats = new List<StatTile>
        {
            new StatTile("Played", Dur(s.Duration), "", null),
            new StatTile("Runs", $"{s.RunCount}", "", null),
            new StatTile("Scenarios", $"{s.ScenarioCount}", "", null),
            new StatTile("PBs", $"{pbCount}", "", pbCount > 0 ? (Brush)FindResource("Green") : null)
        };
        var tiles = new ItemsControl { Style = (Style)FindResource("StatTiles"), ItemsSource = stats, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tiles, 1);
        head.Children.Add(tiles);

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
            var spinner = Spinner.Create((Brush)FindResource("Accent"), 13);
            Put(spinner, 6, new Thickness(0));
            RowClick.Attach(row, p => _host.ShowScenario(name, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        }
        else
        {
            RowClick.Attach(row, p => _host.ShowScenario(name, p), down => row.Background = down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent);
        }
        row.MouseEnter += (_, _) => row.Background = Solid("#0DFFFFFF");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    // Playlists

    bool _playlistsExpanded;

    /// <summary>When any scenario of the playlist was last played, or null.</summary>
    DateTime? LastPlayed(int id)
    {
        DateTime? m = null;
        foreach (var n in _index.Scenarios(id))
            if (_lib.Runs(n) is { Count: > 0 } l && (m == null || l[^1].End > m)) m = l[^1].End;
        return m;
    }

    /// <summary>Collapsed: all favourited playlists. Expanded: favorites first, then a divider, then all remaining playlists.</summary>
    UIElement BuildHomePlaylists()
    {
        var items = AllDifficulties().Select(x => (x.B, x.D, Fav: _host.Ui.IsFavorite($"{x.D.KovaaksBenchmarkId}"), Last: LastPlayed(x.D.KovaaksBenchmarkId))).ToList();
        var favs = items.Where(i => i.Fav).OrderByDescending(i => i.Last ?? DateTime.MinValue).ToList();
        var rest = items.Where(i => !i.Fav).OrderByDescending(i => i.Last ?? DateTime.MinValue).ToList();
        var canExpand = rest.Count > 0;

        // Handle "No favorites" case when collapsed
        if (favs.Count == 0 && !_playlistsExpanded)
        {
            var emptyBody = new StackPanel();
            emptyBody.Children.Add(Text("No favorites", 13, DimB, null, new Thickness(16, 4, 16, 12)));
            if (canExpand)
            {
                var toggle = new Button
                {
                    Content = "", HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 0, 8, 0), MinHeight = 26, MinWidth = 30, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent"), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                    Style = (Style)FindResource("PopupClose")
                };
                toggle.Click += (_, _) =>
                {
                    _playlistsExpanded = !_playlistsExpanded;
                    if (_playlistsSlot != null) _playlistsSlot.Child = BuildHomePlaylists();
                };
                emptyBody.Children.Add(toggle);
            }
            return PlaylistSection(emptyBody);
        }

        Func<UIElement> BuildPlaylistGrid(IEnumerable<(Benchmark B, Difficulty D, bool Fav, DateTime?)> playlists)
        {
            return () =>
            {
                var grid = new UniformGrid { Columns = 2 };
                var big = playlists.Any() && playlists.All(x => x.Fav);
                if (big) grid.Columns = 1;
                foreach (var (b, d, fav, _) in playlists)
        {
            if (fav)
            {
                grid.Children.Add(FavoriteCard(b, d));
                continue;
            }
            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var colorDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = ParseBrush(b.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(colorDot);
            var title = Text(b.BenchmarkName, 14, FgB, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            titleStack.Children.Add(title);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(titleStack);
            info.Children.Add(Text(d.DifficultyName, 12, DimB, null, new Thickness(0, 2, 0, 0)));

            // Tier text without dot
            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0)
            {
                var pct = p.OverallRank >= p.RankNames.Count - 1 ? "100%" : $"{p.OverallFraction * 100:0}%";
                var tierText = Text($"{TierText.Label(p.OverallRankName)} · {pct}", 12, ChartPaths.TextTone(RankBrush(d, p.OverallRankName)), FontWeights.Bold, new Thickness(0, 2, 0, 0));
                info.Children.Add(tierText);
            }
            else
            {
                // Unranked: show "NO RANK" text only
                var tierText = Text("NO RANK", 12, new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x38)),FontWeights.Bold, new Thickness(0, 2, 0, 0));
                info.Children.Add(tierText);
            }
            var cell = new Grid();
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            cell.Children.Add(info);
            if (fav)
            {
                var star = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 13, Foreground = (Brush)FindResource("Orange"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 2, 0, 0) };
                Grid.SetColumn(star, 1);
                cell.Children.Add(star);
            }
            var (bb, dd) = (b, d);
            var card = new Border { CornerRadius = new CornerRadius(14), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(TileGap / 2), Cursor = Cursors.Hand };

            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var rp) && rp.OverallRankName.Length > 0)
            {
                var rankBrush = RankBrush(d, rp.OverallRankName) as SolidColorBrush;
                if (rankBrush != null)
                {
                    var c = rankBrush.Color;
                    var gradBrush = new LinearGradientBrush
                    {
                        StartPoint = new Point(0, 0),
                        EndPoint = new Point(1, 0),
                        GradientStops = new GradientStopCollection
                        {
                            new GradientStop(Color.FromArgb(0x05, 255, 255, 255), 0),
                            new GradientStop(Color.FromArgb(0x38, c.R, c.G, c.B), 1)
                        }
                    };
                    gradBrush.Freeze();
                    card.Background = gradBrush;
                }
                var contentGrid = new Grid();
                var overlay = new Border { CornerRadius = new CornerRadius(13), IsHitTestVisible = false, Background = new SolidColorBrush(Colors.Transparent) };
                contentGrid.Children.Add(overlay);
                contentGrid.Children.Add(cell);
                card.Child = contentGrid;
                var dur = TimeSpan.FromMilliseconds(120);
                card.MouseEnter += (_, _) =>
                {
                    var anim = new ColorAnimation(Color.FromArgb(0x12, 255, 255, 255), dur);
                    overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
                card.MouseLeave += (_, _) =>
                {
                    var anim = new ColorAnimation(Colors.Transparent, dur);
                    overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                };
            }
            else
            {
                card.Child = cell;
                HoverFade(card, "#05FFFFFF", "#0FFFFFFF");
            }

            Clickable(card, () => OpenPlaylist(bb, dd));
                    grid.Children.Add(card);
                }
                grid.SizeChanged += (_, e) => grid.Columns = big ? 1 : e.NewSize.Width < 640 ? 2 : e.NewSize.Width < 900 ? 3 : 4;
                return new Border { Padding = new Thickness(TileGap / 2 - 2, 0, TileGap / 2 - 2, 0), Margin = new Thickness(0, 0, 0, -TileGap / 2), Child = grid };
            };
        }

        var body = new StackPanel();
        if (_playlistsExpanded && favs.Count > 0)
        {
            // Favorites grid
            body.Children.Add(BuildPlaylistGrid(favs)());
            // Divider
            body.Children.Add(new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(4, 8, 4, 8) });
            // Rest grid
            body.Children.Add(BuildPlaylistGrid(rest)());
        }
        else
        {
            // Just show favorites (collapsed or only favorites exist)
            body.Children.Add(BuildPlaylistGrid(favs)());
        }
        if (canExpand || _playlistsExpanded)
        {
            var toggle = new Button
            {
                Content = "", HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(8, 0, 8, 0), MinHeight = 26, MinWidth = 30, Background = Brushes.Transparent, Foreground = (Brush)FindResource("Accent"), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Style = (Style)FindResource("PopupClose")
            };
            toggle.Content = _playlistsExpanded ? "" : "";
            toggle.Click += (_, _) =>
            {
                _playlistsExpanded = !_playlistsExpanded;
                if (_playlistsSlot != null) _playlistsSlot.Child = BuildHomePlaylists();
            };
            body.Children.Add(toggle);
        }
        return PlaylistSection(body);
    }

    readonly HashSet<string> _homeCloudInflight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Redraws a favorite card's line with server scores for scenarios that have no local runs, like the playlist popup chart does: cached ones first, then stale or missing ones fetched and cached.</summary>
    async Task ApplyCachedCloudAsync(ScoreChart chart, BenchmarkProgress p, Brush tier, string watermark)
    {
        var missing = p.Scenarios.Where(sc => sc.RankMaxes.Count > 0 && _lib.Runs(sc.Scenario).Count == 0).Select(sc => sc.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var claimed = new List<string>();
        try
        {
            var cache = Cache;
            var steam = _host.Settings.EffectiveSteamId;
            var (all, user) = await Task.Run(() => (cache.LoadAllScores(), steam.Length == 0 ? null : cache.LoadUsername(steam)));
            var cloud = new Dictionary<string, IReadOnlyList<CloudScore>>(StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();
            var now = DateTime.Now;
            foreach (var n in missing)
            {
                if (all.TryGetValue(n, out var c)) { cloud[n] = c.Scores; if (now - c.FetchedAt > CacheTtl) stale.Add(n); }
                else stale.Add(n);
            }
            void Draw()
            {
                IReadOnlyList<CloudScore> CloudOf(string n) => cloud.TryGetValue(n, out var l) ? l : [];
                var history = Tier.HistoryWithCloud(p.Scenarios.Select<ScenarioProgress, (IReadOnlyList<RunRecord>, IReadOnlyList<CloudScore>, IReadOnlyList<double>)>(
                    sc => (ScenarioStats.For(_lib, sc.Scenario).Runs, CloudOf(sc.Scenario), sc.RankMaxes)));
                chart.SetMini(history.Select(h => (h.Day, h.Value)).ToList(), tier, watermark);
            }
            if (cloud.Count > 0) Draw();
            stale = stale.Where(n => _homeCloudInflight.Add(n)).ToList();
            claimed.AddRange(stale);
            if (stale.Count == 0 || steam.Length == 0) return;

            if (string.IsNullOrEmpty(user))
            {
                user = await _api.ResolveUsernameAsync(steam, p.Scenarios, _cts.Token);
                if (string.IsNullOrEmpty(user)) return;
                var name = user;
                await Task.Run(() => cache.SaveUsername(steam, name));
            }
            var gate = new SemaphoreSlim(MaxParallel);
            var fresh = new Dictionary<string, List<CloudScore>>();
            await Task.WhenAll(stale.Select(async n =>
            {
                await gate.WaitAsync(_cts.Token);
                try { var l = await _api.GetLastScoresAsync(user, n, _cts.Token); fresh[n] = l; cloud[n] = l; }
                catch (Exception) when (!_cts.IsCancellationRequested) { }
                finally { gate.Release(); }
            }));
            if (fresh.Count == 0) return;
            var batch = new Dictionary<string, List<CloudScore>>(fresh);
            _ = Task.Run(() => { try { cache.SaveScoresBatch(batch); } catch (System.IO.IOException) { } });
            Draw();
        }
        catch (Exception) { } // keep what is shown
        finally { foreach (var n in claimed) _homeCloudInflight.Remove(n); }
    }

    /// <summary>Favorite playlist card, sized like the playlist popup's activity card: name and difficulty on the left, the tier progress line on the right over a card-high tier-name watermark.</summary>
    UIElement FavoriteCard(Benchmark b, Difficulty d)
    {
        _progress.TryGetValue(d.KovaaksBenchmarkId, out var p);
        var ranked = p != null && p.OverallRankName.Length > 0;
        var tier = ranked ? RankBrush(d, p!.OverallRankName) : (Brush)FindResource("Dim");

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = ParseBrush(b.Color), Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = Text(b.BenchmarkName, 32, FgB, FontWeights.Light);
        title.FontFamily = new FontFamily("Segoe UI");
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.Opacity = 0.85;
        titleRow.Children.Add(title);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 0, 0, 0) };
        info.Children.Add(titleRow);
        var diff = Text(d.DifficultyName, 22, Solid("#C8C8CE"), FontWeights.Light, new Thickness(20, 4, 0, 0));
        diff.FontFamily = new FontFamily("Segoe UI");
        diff.Opacity = 0.85;
        info.Children.Add(diff);

        var chart = new ScoreChart { IsHitTestVisible = false };
        var history = p == null ? [] : Tier.History(p.Scenarios.Select(sc => (ScenarioStats.For(_lib, sc.Scenario).Runs, (IReadOnlyList<double>)sc.RankMaxes)));
        var watermark = ranked ? TierText.Label(p!.OverallRankName) : "";
        chart.SetMini(history.Select(h => (h.Day, h.Value)).ToList(), tier, watermark);
        if (p != null && p.Scenarios.Any(sc => sc.RankMaxes.Count > 0 && _lib.Runs(sc.Scenario).Count == 0)) _ = ApplyCachedCloudAsync(chart, p, tier, watermark);
        chart.OpacityMask = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Colors.Transparent, 0.15), new GradientStop(Colors.White, 0.5) }, new Point(0, 0), new Point(1, 0));

        var overlay = new Border { CornerRadius = new CornerRadius(3), IsHitTestVisible = false, Background = new SolidColorBrush(Colors.Transparent) };
        var cell = new Grid();
        cell.Children.Add(overlay);
        cell.Children.Add(chart);
        cell.Children.Add(info);

        var card = new Border { CornerRadius = new CornerRadius(4), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Margin = new Thickness(TileGap / 2, TileGap / 2, TileGap / 2, TileGap / 2 + 10), Height = 120, Cursor = Cursors.Hand, Child = cell };
        cell.SizeChanged += (_, e) => cell.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 3, 3);
        if (tier is SolidColorBrush rb && ranked)
        {
            var c = rb.Color;
            var gradBrush = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0x30, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0x08, c.R, c.G, c.B), 1) }, new Point(0, 0), new Point(1, 1));
            gradBrush.Freeze();
            card.Background = gradBrush;
            var dur = TimeSpan.FromMilliseconds(120);
            card.MouseEnter += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Color.FromArgb(0x12, 255, 255, 255), dur));
            card.MouseLeave += (_, _) => overlay.Background.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(Colors.Transparent, dur));
        }
        else HoverFade(card, "#0AFFFFFF", "#14FFFFFF");
        Clickable(card, () => OpenPlaylist(b, d));
        return card;
    }

    /// <summary>Section title outside, content in a single card (same section pattern as Recent sessions).</summary>
    UIElement PlaylistSection(UIElement content)
    {
        var page = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(FadeTitle("Playlists"));
        var searchBtn = new Button { Style = (Style)FindResource("PopupClose"), Width = 34, Height = 34, Margin = new Thickness(0, 12, 0, 0), Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        searchBtn.Click += (_, _) => OpenSearch();
        titleRow.Children.Add(searchBtn);
        page.Children.Add(titleRow);
        var card = Card(content);
        card.Background = Solid("#272729");
        page.Children.Add(card);
        return page;
    }
}

