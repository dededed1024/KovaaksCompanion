using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Session;

namespace KovaaksCompanion.App;

/// <summary>A playlist as the popup shows it: <see cref="Theme"/> is the raw colour (pill), <see cref="MarkBrush"/> the lightened one (abbreviation).</summary>
sealed record BrowserItem(string Title, Benchmark Bench, Brush Theme, string Watermark, Brush MarkBrush);

sealed record RadarEntry(string Category, string Sub, string Scenario, double Value, double Previous);

/// <summary>
/// The Stats page: Home (search, current tier, activity, play sessions, playlists) is the whole page; a playlist opens
/// as a popup over it. Benchmarks (the embedded playlists and categories), per-scenario score
/// history from the stats folder, and the player's server scores. A different non-zero server score wins over the local best.
/// </summary>
public partial class StatsView : UserControl
{
    readonly AppHost _host;
    readonly KovaaksApi _api = new();
    readonly Dictionary<int, BenchmarkProgress> _progress = [];
    readonly Dictionary<int, BenchmarkProgress> _rawProgress = [];
    readonly Dictionary<string, double> _serverBest = new(StringComparer.OrdinalIgnoreCase);
    RunLibrary _lib = new([]);
    List<SessionInfo> _sessions = [];
    readonly PlaylistIndex _index;
    readonly Dictionary<string, int> _diffChoice = new(StringComparer.OrdinalIgnoreCase);
    BrowserItem? _popupItem;
    string? _playlist;
    string _favSig = "";
    bool _loaded, _popupOpen;
    readonly DispatcherTimer _liveTimer;
    int _token, _popTok;

    public StatsView(AppHost host)
    {
        InitializeComponent();
        _host = host;
        _index = new PlaylistIndex(host.Settings.PlaylistIndexFile);
        Heading.Text = "Loading…";
        host.SessionSaved += OnSessionSaved;
        host.RunFinished += OnRunFinished;
        host.GameStateChanged += OnGameStateChanged;
        host.UiChanged += OnUiChanged;
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(30) };
        _liveTimer.Tick += (_, _) => CheckLive();
        _liveTimer.Start();
        _favSig = FavSig();
        DiffTrack.SizeChanged += (_, _) => FitHeading();
        Detail.SizeChanged += (_, _) => FitHeading();
        foreach (var sv in new[] { PlaylistScroll, HomeScroll })
        {
            sv.PreviewMouseWheel += OnWheel;
            sv.ScrollChanged += OnScrolled;
        }
        MiddleScroll.Started += _ => StopEase();
        PlaylistScroll.SizeChanged += (_, _) => FitHost();
        PlaylistScroll.IsVisibleChanged += (_, _) => CopyImage.Visibility = PlaylistScroll.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        HomeScroll.SizeChanged += (_, _) => { FitHome(); UpdateLivePill(); };
        HomeScroll.ScrollChanged += (_, _) => UpdateLivePill();
        PopupCard.PreviewMouseDown += (_, _) => { if (!Popup.IsKeyboardFocusWithin) Popup.Focus(); };
        HomeScroll.Loaded += (_, _) =>
        {
            var scrollbar = HomeScroll.Template?.FindName("PART_VerticalScrollBar", HomeScroll) as ScrollBar;
            if (scrollbar != null) scrollbar.Margin = new Thickness(0, 48, 0, 0);
        };
        // The profile card has a fixed 21:9 design size inside a Viewbox, so the window size only scales it.
        ProfileHost.SizeChanged += (_, _) => RefitProfile();
        ProfileCard.SizeChanged += (_, _) => RefitProfile();
        SessionPopup.SizeChanged += (_, _) =>
        {
            if (SessionPopup.ActualWidth > 0 && SessionPopup.ActualHeight > 0)
            {
                SessionCardBorder.Width = Math.Min(1100, SessionPopup.ActualWidth - 160);
                SessionCardBorder.MaxHeight = Math.Max(0, SessionPopup.ActualHeight - 128);
            }
        };
        Loaded += (_, _) => { if (_loaded) return; _loaded = true; Reload(); };
    }

    void OnViewSized(object sender, SizeChangedEventArgs e) => FitHeading();

    ScrollViewer _ws = null!;
    double _target, _lastSet;
    bool _easing;
    readonly System.Diagnostics.Stopwatch _clock = new();
    TimeSpan _lastTick;

    /// <summary>Smooth wheel scrolling over the whole page: ease towards a target offset (about 40px per notch). Inner scrollers that can still move get the wheel first; otherwise it chains to the outer one.</summary>
    void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        e.Handled = true;
        var outer = (ScrollViewer)sender;
        var sv = outer;
        var node = e.OriginalSource as DependencyObject;
        while (node != null && node != outer)
        {
            if (node is ScrollViewer inner && inner.ScrollableHeight > 0
                && (e.Delta > 0 ? inner.VerticalOffset > 0 : e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight))
            {
                sv = inner;
                break;
            }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (_easing && sv != _ws) StopEase();
        _ws = sv;
        if (!_easing) _target = sv.VerticalOffset;
        _target = Math.Clamp(_target - e.Delta / 3.0, 0, sv.ScrollableHeight);
        StartEase(sv);
    }

    void StartEase(ScrollViewer sv)
    {
        _ws = sv;
        if (_easing) return;
        _easing = true;
        _lastSet = sv.VerticalOffset;
        _lastTick = TimeSpan.Zero;
        _clock.Restart();
        CompositionTarget.Rendering += OnEase;
    }

    void OnEase(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = Math.Min((now - _lastTick).TotalSeconds, 0.05);
        _lastTick = now;
        var cur = _ws.VerticalOffset;
        var diff = _target - cur;
        if (Math.Abs(diff) < 0.5) { _lastSet = _target; _ws.ScrollToVerticalOffset(_target); StopEase(); return; }
        _lastSet = cur + diff * (1 - Math.Exp(-dt / 0.035));
        _ws.ScrollToVerticalOffset(_lastSet);
    }

    void StopEase()
    {
        if (!_easing) return;
        _easing = false;
        CompositionTarget.Rendering -= OnEase;
    }

    /// <summary>A scroll we did not cause (scrollbar drag, bring-into-view) cancels the easing.</summary>
    void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_easing && sender == _ws && e.VerticalChange != 0 && Math.Abs(_ws.VerticalOffset - _lastSet) > 1.5) StopEase();
    }

    /// <summary>The difficulty control sits right of the title, or wraps below it when they do not fit together.</summary>
    void FitHeading()
    {
        var avail = Detail.ActualWidth - (CodeChip.Visibility == Visibility.Visible ? 200 : 0); // minus the sharecode controls
        var diff = DiffTrack.Visibility == Visibility.Visible;
        Heading.MaxWidth = double.PositiveInfinity;
        Heading.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        DiffTrack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var dw = DiffTrack.DesiredSize.Width + 12;
        var below = diff && Heading.DesiredSize.Width + 180 + dw > avail;
        DockPanel.SetDock(DiffTrack, below ? Dock.Bottom : Dock.Right);
        DiffTrack.HorizontalAlignment = below ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        DiffTrack.Margin = below ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        Heading.MaxWidth = Math.Max(120, avail - 180 - (diff && !below ? dw : 0));
    }

    /// <summary>Playlist page: up to 1100px wide, centred.</summary>
    void FitHost()
    {
        PageHost.Width = Math.Max(0, Math.Min(1100, PlaylistScroll.ActualWidth - 14));
    }

    /// <summary>Home page: up to 1100px wide, centred.</summary>
    void FitHome()
    {
        HomeHost.Width = Math.Max(0, Math.Min(1100, HomeScroll.ActualWidth - 64 - 14));
    }


    public void Detach()
    {
        _host.SessionSaved -= OnSessionSaved; _host.RunFinished -= OnRunFinished; _host.GameStateChanged -= OnGameStateChanged; _host.UiChanged -= OnUiChanged;
        _liveTimer.Stop();
        _cts.Cancel();
    }

    string FavSig() => string.Join('|', _host.Ui.FavoritePlaylists.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Favorites changed: redraw what lists playlists.</summary>
    void OnUiChanged()
    {
        var sig = FavSig();
        if (sig == _favSig) return;
        _favSig = sig;
        if (!_loaded) return;
        ShowStar();
        RefreshHome();
    }

    void OnStarClick(object sender, RoutedEventArgs e)
    {
        if (_playlist is { } key) ToggleFavorite(key);
    }

    /// <summary>Pins this playlist's tier to the Home hero, or unpins it.</summary>
    void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (_playlist is not { } key) return;
        _host.Ui.TogglePinned(key);
        _host.SaveUi();
        ShowStar();
        FitHeading();
        RefreshHome();
    }

    void ToggleFavorite(string key)
    {
        _host.Ui.ToggleFavorite(key);
        _host.SaveUi();
        ShowStar();
        RefreshHome();
    }

    /// <summary>The difficulty shown for a benchmark: the remembered one, else the first favourite, else the first.</summary>
    Difficulty Chosen(Benchmark b) =>
        (_diffChoice.TryGetValue(b.BenchmarkName, out var id) ? b.Difficulties.FirstOrDefault(d => d.KovaaksBenchmarkId == id) : null)
        ?? b.Difficulties.FirstOrDefault(d => _host.Ui.IsFavorite($"{d.KovaaksBenchmarkId}")) ?? b.Difficulties[0];

    void ShowStar()
    {
        if (_playlist is not { } key) { Star.Visibility = Visibility.Collapsed; Pin.Visibility = Visibility.Collapsed; return; }
        var pinned = _host.Ui.IsPinned(key);
        if (PinnedTiersEnabled)
        {
            Pin.Content = ((char)(pinned ? 0xE842 : 0xE718)).ToString();
            Pin.Foreground = (System.Windows.Media.Brush)FindResource(pinned ? "Accent" : "Dim");
            Pin.ToolTip = pinned ? "Remove tier from Home" : "Show tier on Home";
            Pin.Visibility = Visibility.Visible;
        }
        else
        {
            Pin.Visibility = Visibility.Collapsed;
        }
        var fav = _host.Ui.IsFavorite(key);
        Star.Content = ((char)(fav ? 0xE735 : 0xE734)).ToString();
        Star.Foreground = (System.Windows.Media.Brush)FindResource(fav ? "Orange" : "Dim");
        Star.ToolTip = fav ? "Remove from favorites" : "Add to favorites";
        Star.Visibility = Visibility.Visible;
    }

    /// <summary>The run's CSV is complete: show it now, without waiting for the clip to be cut.</summary>
    void OnRunFinished() => Dispatcher.InvokeAsync(() => { if (_loaded) Reload(); });

    void OnGameStateChanged() => Dispatcher.InvokeAsync(() =>
    {
        var active = _host.GameActive;
        if (_gameWasActive && !active) _gameClosed = true;
        else if (active)
        {
            if (!_gameWasActive) _liveSince = DateTime.Now;
            _gameClosed = false;
        }
        _gameWasActive = active;
        CheckLive();
    });

    /// <summary>Redraws Home when the live state flipped: the game started or stopped, or the newest session aged out.</summary>
    void CheckLive()
    {
        if (!_loaded || LiveNow() == _liveShown) return;
        ShowHome();
    }

    void OnSessionSaved(SessionInfo info, string folder) => Dispatcher.InvokeAsync(() =>
    {
        if (_popupOpen && int.TryParse(_playlist, out var id)) _progress.Remove(id); // refetched when the popup re-renders
        Reload();
    });

    async void Reload()
    {
        var folder = _host.Settings.StatsFolder;
        var sessionsFolder = _host.SessionsFolder;
        var cache = Cache;
        var (lib, sessions, cached) = await Task.Run(() => {
            var infos = SessionStore.List(sessionsFolder).Select(s => s.Info).ToList();
            return (RunLibrary.Scan(folder).Verified(infos), infos, LoadCachedProgress(cache, _host.Settings.EffectiveSteamId));
        });
        (_lib, _sessions) = (lib, sessions);
        foreach (var c in cached)
        {
            if (_progress.ContainsKey(c.D.KovaaksBenchmarkId)) continue;
            ApplyProgress(c.D, c.P);
            _fetched[c.D.KovaaksBenchmarkId] = c.At;
        }
        ReapplyLocal();
        ShowHome();
        if (_popupOpen && _popupItem is { } open) ShowBenchmark(open, true);
        _ = RefreshProgressAsync();
    }

    // Session popup

    bool _sessionOpen;
    int _sessionTok;

    /// <summary>Opens the session popup with the given session as an expanded card.</summary>
    public void OpenSession(PlaySession s, Point? origin)
    {
        if (_sessionOpen) return;
        _sessionOpen = true;
        _sessionTok++;
        SessionScrim.BeginAnimation(OpacityProperty, null); SessionScrim.Opacity = 0;
        SessionCardBorder.BeginAnimation(OpacityProperty, null); SessionCardBorder.Opacity = 0;
        SessionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); SessionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        SessionScale.ScaleX = SessionScale.ScaleY = 0.94;

        SessionHost.Children.Clear();
        SessionHost.Children.Add(SessionCard(s, PbSet(), false, expanded: true));

        SessionPopup.Visibility = Visibility.Visible;
        if (origin is { } o && SessionCardBorder.ActualWidth > 0 && SessionCardBorder.ActualHeight > 0)
        {
            var tl = SessionCardBorder.TranslatePoint(new Point(0, 0), this);
            SessionCardBorder.RenderTransformOrigin = new Point(Math.Clamp((o.X - tl.X) / SessionCardBorder.ActualWidth, 0, 1), Math.Clamp((o.Y - tl.Y) / SessionCardBorder.ActualHeight, 0, 1));
        }
        else SessionCardBorder.RenderTransformOrigin = new Point(0.5, 0.5);
        Animate(true, null, SessionScrim, SessionCardBorder, SessionScale);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_sessionOpen) SessionPopup.Focus(); });
    }

    public void CloseSession()
    {
        if (!_sessionOpen) return;
        _sessionOpen = false;
        var tok = ++_sessionTok;
        Animate(false, () =>
        {
            if (tok != _sessionTok) return;
            SessionPopup.Visibility = Visibility.Collapsed;
            SessionHost.Children.Clear();
        }, SessionScrim, SessionCardBorder, SessionScale);
    }

    void OnSessionScrim(object sender, MouseButtonEventArgs e) => CloseSession();

    void OnSessionKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseSession();
    }

    // The playlist popup

    /// <summary>True while the playlist popup is open.</summary>
    public bool IsPopupOpen => _popupOpen;

    /// <summary>Closes the topmost popup (search, then session, then playlist); false when none was open.</summary>
    public bool CloseOverlay()
    {
        if (_searchOpen) { CloseSearch(); return true; }
        if (_sessionOpen) { CloseSession(); return true; }
        if (_profileOpen) { CloseProfile(); return true; }
        if (!_popupOpen) return false;
        ClosePopup();
        return true;
    }

    void OnScrim(object sender, MouseButtonEventArgs e) => ClosePopup();
    void OnCloseClick(object sender, RoutedEventArgs e) => ClosePopup();

    void OnPopupKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        ClosePopup();
    }

    BrowserItem MakeItem(Benchmark b)
    {
        var theme = ParseBrush(b.Color);
        return new BrowserItem(b.BenchmarkName, b, theme, "", Lighten(theme, 0.45));
    }

    /// <summary>Opens a playlist (at <paramref name="d"/> when given, else the remembered difficulty) in the popup.</summary>
    void OpenPlaylist(Benchmark b, Difficulty? d = null)
    {
        CloseProfile(true);
        if (d != null) _diffChoice[b.BenchmarkName] = d.KovaaksBenchmarkId;
        _popupItem = MakeItem(b);
        OpenPopup();
        ShowBenchmark(_popupItem);
    }

    void OpenPopup()
    {
        if (_popupOpen) return;
        _popupOpen = true; UpdateLivePill();
        _popTok++;
        Scrim.BeginAnimation(OpacityProperty, null); Scrim.Opacity = 0;
        PopupCard.BeginAnimation(OpacityProperty, null); PopupCard.Opacity = 0;
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale.ScaleX = Scale.ScaleY = 0.94;
        PlaylistScroll.Visibility = Visibility.Collapsed;
        Popup.Visibility = Visibility.Visible;
        Animate(true, null);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_popupOpen) Popup.Focus(); });
    }

    void ClosePopup()
    {
        if (!_popupOpen) return;
        _popupOpen = false; UpdateLivePill();
        _token++; // drops in-flight loads
        _playlist = null;
        var tok = ++_popTok;
        StopEase();
        Animate(false, () =>
        {
            if (tok != _popTok) return;
            Popup.Visibility = Visibility.Collapsed;
            PageHost.Children.Clear();
            _ctx = null; _bal = null; _bench = null; _hero = null; _tiles = null;
        });
    }

    void Animate(bool show, Action? done, Border? scrim = null, Border? card = null, ScaleTransform? scale = null)
    {
        scrim ??= Scrim; card ??= PopupCard; scale ??= Scale;
        var ms = TimeSpan.FromMilliseconds(show ? 260 : 180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 180)), HandoffBehavior.SnapshotAndReplace);
        var fade = new DoubleAnimation(show ? 1 : 0, ms) { EasingFunction = ease };
        if (done != null) fade.Completed += (_, _) => done();
        card.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        var sx = new DoubleAnimation(show ? 1 : 0.96, ms) { EasingFunction = ease };
        var sy = sx.Clone();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy, HandoffBehavior.SnapshotAndReplace);
    }

    string ItemSubtitle(Benchmark b)
    {
        var d0 = b.Difficulties[0];
        var text = b.Difficulties.Count > 1 ? $"{b.Difficulties.Count} difficulties · {d0.ScenarioCount} scenarios" : $"{d0.ScenarioCount} scenarios";
        if (_progress.TryGetValue(Chosen(b).KovaaksBenchmarkId, out var p) && p.OverallRankName.Length > 0) text += $" · {TierText.Label(p.OverallRankName)}";
        return text;
    }

    /// <summary>Header accents: the benchmark's colour pill and abbreviation.</summary>
    void SetHeadAccent(BrowserItem item)
    {
        HeadPill.Visibility = Visibility.Visible;
        HeadMark.Visibility = item.Watermark.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeadPill.Background = item.Theme;
        HeadMark.Text = item.Watermark;
        HeadMark.Foreground = item.MarkBrush;
    }

    /// <summary>"none": only the header; "playlist": the scrolling playlist page.</summary>
    void SetLayout(string kind)
    {
        if (kind != "playlist") _bal = null;
        PlaylistScroll.Visibility = kind == "playlist" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One segment per difficulty (hidden for a single one); choosing it re-renders the benchmark for that difficulty.</summary>
    void BuildDiffBar(BrowserItem item, Difficulty current)
    {
        var b = item.Bench;
        DiffBar.Children.Clear();
        DiffTrack.Visibility = b.Difficulties.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (b.Difficulties.Count > 1)
            foreach (var d in b.Difficulties)
            {
                var r = new RadioButton { Style = (Style)FindResource("Segment"), Content = d.DifficultyName, Tag = d.KovaaksBenchmarkId, IsChecked = d.KovaaksBenchmarkId == current.KovaaksBenchmarkId };
                r.Checked += (_, _) =>
                {
                    if (_diffChoice.GetValueOrDefault(b.BenchmarkName) == d.KovaaksBenchmarkId) return;
                    _diffChoice[b.BenchmarkName] = d.KovaaksBenchmarkId;
                    ShowBenchmark(item, true);
                };
                DiffBar.Children.Add(r);
            }
        FitHeading();
    }

    void OnAxesChecked(object sender, RoutedEventArgs e)
    {
        _host.Ui.RadarAxes = (string)((RadioButton)sender).Tag;
        _host.SaveUi();
        RenderRadar();
    }

    void SetSummary(string note)
    {
        Summary.Text = note;
        Summary.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    async void ShowBenchmark(BrowserItem item, bool keepScroll = false)
    {
        var keep = keepScroll && PlaylistScroll.Visibility == Visibility.Visible ? PlaylistScroll.VerticalOffset : 0;
        var b = item.Bench;
        var d = Chosen(b);
        _diffChoice[b.BenchmarkName] = d.KovaaksBenchmarkId;
        var tok = ++_token;
        _playlist = $"{d.KovaaksBenchmarkId}";
        ShowStar();
        ShowCode(d.Sharecode);
        SetHeadAccent(item);
        BuildDiffBar(item, d);
        SetLayout("none");
        Heading.Text = item.Title;
        SetSummary("");

        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0)
        {
            SetSummary("No Steam ID\n" + Structure(d));
            return;
        }

        if (!_progress.TryGetValue(d.KovaaksBenchmarkId, out var p))
        {
            SetSummary("Loading…");
            try { p = await FetchProgressAsync(d, steam); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                if (tok == _token) SetSummary($"{ex.Message}\n" + Structure(d));
                return;
            }
            ApplyProgress(d, p);
            p = _progress[d.KovaaksBenchmarkId];
        }
        if (tok != _token) return;

        var report = PlaylistReport.Build(item.Title, p.Scenarios.Select(s => s.Scenario), _lib);
        var byName = report.Scenarios.ToDictionary(s => s.Scenario, StringComparer.OrdinalIgnoreCase);
        SetSummary("");

        var cutoff = DateTime.Now.AddDays(-30);
        var maxRank = p.Scenarios.Select(sc => sc.RankMaxes.Count).DefaultIfEmpty(0).Max();
        if (p.RankNames.Count > 1) maxRank = Math.Min(maxRank, p.RankNames.Count - 1);
        var tierNames = Enumerable.Range(1, maxRank).Select(p.RankName).ToList();
        var tierBrushes = tierNames.Select(n => RankBrush(d, n)).ToList();
        var history = Tier.History(p.Scenarios.Select(sc => (ScenarioStats.For(_lib, sc.Scenario).Runs, (IReadOnlyList<double>)sc.RankMaxes)));
        var entries = new List<RadarEntry>();
        var blocks = new List<(string Scenario, int Rank, double Fraction)>();
        var counts = new int[maxRank + 1];
        var cats = new List<BenchCategory>();
        foreach (var g in p.Subcategories.GroupBy(CatOf))
        {
            var def = d.Categories.FirstOrDefault(c => c.CategoryName == g.Key);
            var subs = g.Select(sub =>
            {
                var subDef = def?.Subcategories.FirstOrDefault(x => x.SubcategoryName.Trim().Equals(sub.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                var rows = sub.Scenarios.Select(sc =>
                {
                    var local = byName[sc.Scenario];
                    var best = KovaaksApi.Reconcile(local.Plays > 0 ? local.Best : null, sc.Score);
                    var fromServer = best != null && (local.Plays == 0 || Math.Abs(best.Value - local.Best) > 0.005);
                    var old = Tier.BestAsOf(ScenarioStats.For(_lib, sc.Scenario).Runs, cutoff);
                    entries.Add(new RadarEntry(CatOf(sub), sub.Name, sc.Scenario, Tier.ValueOf(best ?? 0, sc.RankMaxes), Tier.ValueOf(old ?? 0, sc.RankMaxes)));
                    var pos = Tier.TierOf(best ?? 0, sc.RankMaxes);
                    var rank = sc.RankMaxes.Count == 0 ? 0 : Math.Min(pos.Rank, maxRank);
                    counts[rank]++;
                    blocks.Add((sc.Scenario, rank, pos.Fraction));
                    return new BenchRow(sc.Scenario, best, fromServer, local.Plays, local.LastPlayed, sc.RankMaxes);
                }).ToList();
                return new BenchSub(sub.Name.Trim(), ParseBrush(subDef?.Color), rows);
            }).ToList();
            cats.Add(new BenchCategory(g.Key, ParseBrush(def?.Color), subs));
        }
        _ctx = new PlaylistCtx(item, d, p, report, cats, tierNames.Select(TierText.Label).ToList(), tierBrushes, history, counts, blocks, entries, cutoff, item.Theme, "");
        SetLayout("playlist");
        ShowPlaylist(keep);
        _ = LoadCloudAsync(tok, d, p);
    }

    static Brush Solid(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>Fades a border's background between two colours in 120ms on hover.</summary>
    static void HoverFade(Border b, string from, string to)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(from));
        b.Background = brush;
        void Go(string c) => brush.BeginAnimation(SolidColorBrush.ColorProperty, new System.Windows.Media.Animation.ColorAnimation((Color)ColorConverter.ConvertFromString(c), TimeSpan.FromMilliseconds(120)));
        b.MouseEnter += (_, _) => Go(to);
        b.MouseLeave += (_, _) => Go(from);
    }

    /// <summary>The category a subcategory is shown under; subcategories without a known parent are their own category.</summary>
    static string CatOf(SubcategoryProgress s) => s.Parent.Length > 0 ? s.Parent : s.Name;

    static string Join(string parent, string sub) => parent.Length == 0 ? sub : sub.Length == 0 || sub.Equals(parent, StringComparison.OrdinalIgnoreCase) ? parent : $"{parent} / {sub}";

    static string Structure(Difficulty d) =>
        string.Join("  |  ", d.Categories.Select(c => $"{c.CategoryName} ({c.Subcategories.Sum(s => s.ScenarioCount)})"));

    Brush RankBrush(Difficulty d, string name) => ParseBrush(d.RankColors.TryGetValue(name, out var hex) ? hex : null);

    /// <summary>A frozen brush from a colour string, or the dim brush when it is missing or malformed.</summary>
    Brush ParseBrush(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
            catch (FormatException) { }
        }
        return (Brush)FindResource("Dim");
    }

    /// <summary>
    /// Tier thresholds of a scenario as chart bands, from the cached progress of the selected playlist's benchmark that contains it.
    /// Outside a playlist, the containing playlist is chosen by: favorite, played in the live session, most recently played, then catalog (difficulty) order.
    /// </summary>
    (BenchmarkProgress P, int Id)? TierSource(string scenario)
    {
        (BenchmarkProgress P, int Id)? Find(int id) => _progress.TryGetValue(id, out var pr) && pr.Scenarios.Any(sc => sc.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) && sc.RankMaxes.Count > 0) ? (pr, id) : null;
        if (int.TryParse(_playlist, out var cur) && Find(cur) is { } sel) return sel;

        var live = LiveNow() && PlaySession.Group(_lib.AllRuns).FirstOrDefault() is { } s && s.End >= _liveSince
            ? s.Runs.Select(r => r.Scenario).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        return AllDifficulties()
            .Select((x, order) => (Id: x.D.KovaaksBenchmarkId, Order: order))
            .Select(x => (x.Id, x.Order, Hit: Find(x.Id)))
            .Where(x => x.Hit != null)
            .OrderByDescending(x => _host.Ui.IsFavorite($"{x.Id}"))
            .ThenByDescending(x => _index.Scenarios(x.Id).Any(live.Contains))
            .ThenByDescending(x => LastPlayed(x.Id) ?? DateTime.MinValue)
            .ThenBy(x => x.Order)
            .Select(x => x.Hit)
            .FirstOrDefault();
    }

    /// <summary>Name of the benchmark whose tiers <see cref="TierBands"/> uses for <paramref name="scenario"/>, or null.</summary>
    string? TierBenchmarkName(string scenario) =>
        TierSource(scenario) is { } h ? BenchmarkCatalog.All.FirstOrDefault(b => b.Difficulties.Any(x => x.KovaaksBenchmarkId == h.Id))?.BenchmarkName : null;

    List<ChartBand>? TierBands(string scenario)
    {
        if (TierSource(scenario) is not { } h) return null;
        var d = BenchmarkCatalog.All.SelectMany(b => b.Difficulties).FirstOrDefault(x => x.KovaaksBenchmarkId == h.Id);
        if (d == null) return null;
        var sc = h.P.Scenarios.First(x => x.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) && x.RankMaxes.Count > 0);
        return sc.RankMaxes.Select((v, i) => new ChartBand(v, TierText.Label(h.P.RankName(i + 1)), RankBrush(d, h.P.RankName(i + 1)))).ToList();
    }

    /// <summary>The colour blended <paramref name="t"/> of the way towards white.</summary>
    static Brush Lighten(Brush b, double t)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        byte L(byte v) => (byte)(v + (255 - v) * t);
        var r = new SolidColorBrush(Color.FromRgb(L(c.R), L(c.G), L(c.B)));
        r.Freeze();
        return r;
    }

    static Brush Alpha(Brush b, double a)
    {
        var c = b is SolidColorBrush s ? s.Color : Colors.Gray;
        var r = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B));
        r.Freeze();
        return r;
    }

    /// <summary>The sheet's data: library, sessions (replays), server bests and tier bands.</summary>
    public ScenarioData SheetData() => new(_lib, _sessions, _serverBest, TierBands);

    internal static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
}
