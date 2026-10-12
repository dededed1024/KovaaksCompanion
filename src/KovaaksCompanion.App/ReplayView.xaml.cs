using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using KovaaksCompanion.Core.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Session;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

/// <summary>What the popup reads: local runs, saved sessions (replays), server bests and tier thresholds.</summary>
public sealed record ScenarioData(ScoreBook Book, IReadOnlyList<SessionInfo> Sessions, Func<string, List<ChartBand>?> TierBands, Func<string, Task<bool>> LoadCloud, IReadOnlySet<(string Scenario, DateTime End)> Hidden)
{
    public RunLibrary Lib => Book.Lib;
}

public sealed record SessionItem(string Folder, SessionInfo Info);

/// <summary>One metric chip: colored dot, name and the value at the playhead. Hidden ones render dimmed.</summary>
public sealed class MetricRow(ChartSeries series, bool visible) : INotifyPropertyChanged
{
    string _value = "–";
    bool _visible = visible;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ChartSeries Series => series;
    public string Name => ChartSeriesInfo.Name(series);
    public Brush Brush => ChartSeriesInfo.Brush(series);
    public double Opacity => _visible ? 1 : 0.4;

    public bool Visible
    {
        get => _visible;
        set { if (_visible == value) return; _visible = value; PropertyChanged?.Invoke(this, new(nameof(Opacity))); }
    }

    public string Value
    {
        get => _value;
        set { if (_value == value) return; _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
}

/// <summary>
/// Modal replay popup over the whole window (below the title bar). Right: scenario tiles, score chart and run list.
/// Left: the scenario overview, or one run (stats strip, video with trail, run chart, transport).
/// </summary>
public partial class ReplayView : UserControl
{
    const double FrameSec = 1.0 / 60;
    const string PlayGlyph = "", PauseGlyph = "";
    const int Page = 100;
    static readonly TimeSpan SeekDelay = TimeSpan.FromSeconds(0.08);
    const double NudgeStepMs = 10, NudgeMaxMs = 500;
    static readonly TimeSpan NudgeSaveDelay = TimeSpan.FromMilliseconds(500);

    readonly AppHost _host;
    readonly List<SessionItem> _items = [];
    readonly ObservableCollection<MetricRow> _metrics = [];
    readonly HashSet<ChartSeries> _visible = [];
    readonly Dictionary<DateTime, Border> _rows = [];
    readonly DispatcherTimer _seekTimer;
    readonly DispatcherTimer _nudgeTimer = new() { Interval = NudgeSaveDelay };
    (string Folder, SessionInfo Info)? _nudgePending; // unsaved sync nudge, written by FlushNudge
    readonly ScoreChart _bigChart = new() { Cursor = Cursors.Hand };
    ScenarioData? _d;
    ScenarioStats _stats = new("", []);
    HashSet<int> _pbs = [];
    string _scenario = "";
    DateTime? _selEnd;
    int _runIdx = -1;
    bool _open, _runMode;
    int _tok, _limit = Page;
    LoadedSession? _s;
    double _duration, _runLength;
    double[] _killTimes = [];
    IReadOnlyDictionary<float, ShotOutcome> _outcomes = new Dictionary<float, ShotOutcome>();
    HeldAccuracy? _held;
    double _speed = 1;
    bool _playing, _dragging, _seekPending;
    double _dragPos;
    double _shownPos;
    bool _resumeAfterDrag;
    long _holdUntil;
    FsState? _fs; // non-null while the stage fills the monitor
    readonly DispatcherTimer _fsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    Point _fsMouse;

    /// <summary>What fullscreen changed, so exiting restores it exactly.</summary>
    sealed record FsState(Window Win, bool Maximized, Rect Bounds, bool Topmost, Thickness Resize, double Caption, UIElement? Title,
        HorizontalAlignment HAlign, VerticalAlignment VAlign, Thickness CardMargin, CornerRadius CardRadius, Thickness CardBorder, Brush CardBg,
        System.Windows.Media.Effects.Effect? Effect, Thickness GridMargin, Thickness StageMargin);

    public ReplayView(AppHost host)
    {
        InitializeComponent();
        _host = host;
        Metrics.ItemsSource = _metrics;
        foreach (var n in host.Ui.VisibleSeries) if (Enum.TryParse<ChartSeries>(n, out var vs)) _visible.Add(vs);
        foreach (var (folder, info) in SessionStore.List(host.SessionsFolder)) _items.Add(new SessionItem(folder, info));
        SetTransport(false);
        TrailBtn.IsChecked = host.Ui.ShowTrail;
        Overlay.Visibility = host.Ui.ShowTrail ? Visibility.Visible : Visibility.Collapsed;
        _seekTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = SeekDelay };
        _seekTimer.Tick += (_, _) => CommitSeek();
        Seek.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnSeekDown), true);
        Seek.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnSeekUp), true);
        Seek.AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnSeekMove), true);
        Seek.AddHandler(UIElement.LostMouseCaptureEvent, new MouseEventHandler(OnSeekLostCapture), true);
        _fsTimer.Tick += (_, _) => HideBar();
        _nudgeTimer.Tick += (_, _) => FlushNudge();
        PreviewMouseMove += OnFsMouse;
        host.SessionSaved += OnSessionSaved;
        CompositionTarget.Rendering += OnRendering;
    }

    public bool IsOpen => _open;

    Brush DimB => (Brush)FindResource("Dim");
    Brush FgB => (Brush)FindResource("Fg");

    static Brush Solid(string hex)
    {
        var b = new SolidColorBrush(Theme.Ink((Color)ColorConverter.ConvertFromString(hex)));
        b.Freeze();
        return b;
    }

    public void Detach()
    {
        _host.SessionSaved -= OnSessionSaved;
        CompositionTarget.Rendering -= OnRendering;
        _seekTimer.Stop(); _fsTimer.Stop();
        FlushNudge();
        Media.Close();
    }

    public void Pause()
    {
        if (!_playing) return;
        _playing = false; Media.Pause(); PlayBtn.Content = PlayGlyph;
    }

    // ---- popup shell -------------------------------------------------------------------------------------------

    void OnSized(object sender, SizeChangedEventArgs e) => LayoutCard(e.NewSize);

    // The card keeps the aspect ratio it has when the window is 16:9 at its design size (1440 x 810); smaller windows shrink both sides together.
    const double CardAspect = (1440.0 - 160) / (810 - 48 - 80);

    void LayoutCard(Size size)
    {
        if (_fs is not null) return;
        var w = Math.Max(0, Math.Min(Math.Min(1500, size.Width - 160), Math.Max(0, size.Height - 48 - 80) * CardAspect));
        Card.Width = w;
        Card.Height = w / CardAspect;
        SideRow.Height = new GridLength(Math.Clamp(Card.Height * 0.2, 90, 180));
        RunChartRow.Height = new GridLength(Math.Clamp(Card.Height * 0.1, 60, 140));
        _bigChart.Height = Math.Clamp(Card.Height * 0.4, 120, 460);
    }

    void OnHeaderSized(object sender, SizeChangedEventArgs e) => FitHeader();

    /// <summary>The name is cut with an ellipsis before it reaches the Play button next to it, which sits left of the tool buttons.</summary>
    void FitHeader()
    {
        if (Header.ActualWidth <= 0) return;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        HeaderTools.Measure(inf); PlayScn.Measure(inf);
        var play = PlayScn.Visibility == Visibility.Visible ? PlayScn.DesiredSize.Width + PlayScn.Margin.Left : 0;
        Heading.MaxWidth = Math.Max(60, Header.ActualWidth - HeaderTools.DesiredSize.Width - play - 12);
    }

    void OnCardSized(object sender, SizeChangedEventArgs e) =>
        Clipper.Clip = _fs is null ? new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17) : null;

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Opens (or replaces the content of) the popup on the scenario overview. <paramref name="origin"/> is in this control's coordinates.</summary>
    public void Show(ScenarioData data, string scenario, Point? origin) => Open(data, scenario, null, origin);

    /// <summary>Opens the popup with the run that ended at <paramref name="end"/> selected; false when it has no recording.</summary>
    public bool ShowRun(ScenarioData data, string scenario, DateTime end)
    {
        if (SessionStore.FindRun(Infos(), scenario, end) < 0 && !_host.IsPending(scenario, end)) return false;
        Open(data, scenario, end, null);
        return true;
    }

    /// <summary>Opens the most recent recorded session's run; false when nothing was recorded yet.</summary>
    public bool ShowLatest(ScenarioData data)
    {
        if (_items.Count == 0) return false;
        var i = _items[0].Info;
        return ShowRun(data, i.Scenario, i.End);
    }

    void Open(ScenarioData data, string scenario, DateTime? end, Point? origin)
    {
        var changed = !_open || !_scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) || end != null || _runMode;
        _d = data; _scenario = scenario; _limit = Page;
        if (changed) UnloadMedia();
        _selEnd = null; _runIdx = -1;
        BuildAll();
        if (end is { } e) SelectRun(e); else ShowOverview();
        RunsScroll.ScrollToTop();
        var wasOpen = _open;
        var fresh = Visibility != Visibility.Visible;
        _open = true;
        Popups.Opened(this, this, Close, () => { });
        var tok = ++_tok;
        if (fresh)
        {
            Scrim.BeginAnimation(OpacityProperty, null); Scrim.Opacity = 0;
            Card.BeginAnimation(OpacityProperty, null); Card.Opacity = 0;
            Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            Scale.ScaleX = Scale.ScaleY = 0.94;
            Visibility = Visibility.Visible;
            UpdateLayout();
        }
        if (wasOpen) return;
        if (origin is { } o && Card.Width > 0 && Card.Height > 0)
        {
            var tl = Card.TranslatePoint(new Point(0, 0), this);
            Card.RenderTransformOrigin = new Point(Math.Clamp((o.X - tl.X) / Card.Width, 0, 1), Math.Clamp((o.Y - tl.Y) / Card.Height, 0, 1));
        }
        else Card.RenderTransformOrigin = new Point(0.5, 0.5);
        Animate(true, tok);
    }

    /// <summary>Closes the popup (pausing the video); false when it was not open.</summary>
    public bool Close()
    {
        if (!_open) return false;
        ExitFullscreen();
        _open = false;
        Popups.Closed(this);
        _seekTimer.Stop(); _seekPending = false; _dragging = false; _resumeAfterDrag = false;
        FlushNudge();
        Pause();
        Animate(false, ++_tok);
        return true;
    }

    void Animate(bool show, int tok)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var ms = TimeSpan.FromMilliseconds(show ? 260 : 180);
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 180));
        Scrim.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, ms) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        var sx = new DoubleAnimation(show ? 1 : 0.94, ms) { EasingFunction = ease };
        var sy = sx.Clone();
        if (!show) sx.Completed += (_, _) => { if (tok == _tok && !_open) Visibility = Visibility.Collapsed; };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx, HandoffBehavior.SnapshotAndReplace);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Keyboard shortcuts while the popup is open and a run with video is loaded.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_fs is not null && e.Key == Key.Escape) { ExitFullscreen(); e.Handled = true; return true; }
        if (!_open || !_runMode || _duration <= 0 || e.OriginalSource is TextBox) return false;
        var big = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.T: SetTrail(TrailBtn.IsChecked != true); break;
            case Key.OemOpenBrackets: Nudge(-NudgeStepMs); break;
            case Key.OemCloseBrackets: Nudge(NudgeStepMs); break;
            case Key.Left: Step(big ? -1.0 : -FrameSec); break;
            case Key.Right: Step(big ? 1.0 : FrameSec); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    // ---- sessions ----------------------------------------------------------------------------------------------

    List<SessionInfo> Infos() => _items.Select(x => x.Info).ToList();

    void OnSessionSaved(SessionInfo info, string folder) => Dispatcher.BeginInvoke(async () =>
    {
        var i = 0;
        while (i < _items.Count && _items[i].Info.Start > info.Start) i++;
        _items.Insert(i, new SessionItem(folder, info));
        if (!_open || _d is null) return;
        var statsFolder = _host.Settings.StatsFolder;
        var tok = _tok;
        var infos = Infos();
        var lib = await Task.Run(() => RunLibrary.Scan(statsFolder).Verified(infos));
        if (!_open || tok != _tok || _d is null) return;
        _d = _d with { Book = _d.Book.WithLibrary(lib) };
        BuildAll();
        if (_runMode && _selEnd is { } end)
        {
            _runIdx = NearestRun(end);
            ApplySelection();
            var si = SessionStore.FindRun(Infos(), _scenario, end);
            if (si >= 0 && (_s is null || _s.Folder != _items[si].Folder)) LoadSession(_items[si]);
        }
        else if (!_runMode) BuildOverview();
    });

    // ---- sidebar -----------------------------------------------------------------------------------------------

    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? L.F("{0}h {1}m", (int)t.TotalHours, t.Minutes) : L.F("{0}m {1}s", t.Minutes, t.Seconds);

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? L.T("just now") : d.TotalHours < 1 ? L.F("{0} min ago", (int)d.TotalMinutes) : d.TotalDays < 1 ? L.F("{0} h ago", (int)d.TotalHours)
            : d.TotalDays < 30 ? L.F("{0} d ago", (int)d.TotalDays) : L.ShortDate(t);
    }

    /// <summary>The highest tier a score reaches in the current scenario; null when it has no tiers or the score is below the first.</summary>
    ChartBand? TierOf(double score) =>
        _d?.TierBands(_scenario) is { Count: > 0 } bands ? bands.LastOrDefault(b => score >= b.Value) : null;

    /// <summary>The best of the local runs, the server scores and the server's all-time best (as the stats page does).</summary>
    double BestScore => _stats.Best;

    static string DayLabel(DateTime day) =>
        day == DateTime.Today ? L.T("Today") : day == DateTime.Today.AddDays(-1) ? L.T("Yesterday") : day.ToString("ddd", L.DateCulture) + ", " + L.MonthDay(day);

    void SetScenario(string scenario)
    {
        _scenario = scenario;
        _stats = _d!.Book.For(scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestRunIndexes());
        ShowScenarioStar();
    }

    void ShowScenarioStar()
    {
        if (_scenario.Length == 0) { ScnStar.Visibility = Visibility.Collapsed; return; }
        var fav = _host.Ui.IsFavoriteScenario(_scenario);
        ScnStar.Content = ((char)(fav ? 0xE735 : 0xE734)).ToString();
        ScnStar.Foreground = (Brush)FindResource(fav ? "Orange" : "Dim");
        ScnStar.ToolTip = fav ? L.T("Remove from favorites") : L.T("Add to favorites");
        ScnStar.Visibility = Visibility.Visible;
        FitHeader();
    }

    void OnScenarioStar(object sender, RoutedEventArgs e)
    {
        if (_scenario.Length == 0) return;
        _host.Ui.ToggleFavoriteScenario(_scenario);
        _host.SaveUi();
        ShowScenarioStar();
    }

    /// <summary>Session mode: re-reads the session from the library (a live one grows), then the header, tiles and runs.</summary>
    void OnPlayScenario(object sender, RoutedEventArgs e)
    {
        if (_scenario.Length > 0) KovaaksCompanion.Core.KovaaksLaunch.Open(KovaaksCompanion.Core.KovaaksLaunch.Scenario(_scenario));
    }

    void BuildAll()
    {
        var d = _d!;
        _stats = d.Book.For(_scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestRunIndexes());
        Heading.Text = _scenario;
        Heading.ToolTip = _scenario;
        PlayScn.Visibility = _scenario.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FitHeader();
        var played = _stats.Runs.Count > 0;
        NoRuns.Visibility = played ? Visibility.Collapsed : Visibility.Visible;
        BuildRuns();
    }

    void BuildRuns()
    {
        RunsHost.Children.Clear();
        _rows.Clear();
        var order = Enumerable.Range(0, _stats.Runs.Count).Reverse().ToList();
        foreach (var i in order.Where(i => !_d!.Hidden.Contains((_stats.Runs[i].Scenario.ToLowerInvariant(), _stats.Runs[i].End))).Take(_limit)) RunsHost.Children.Add(RunRow(_stats.Runs[i], _pbs.Contains(i)));
        var left = order.Count - _limit;
        MoreBtn.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreBtn.Content = "";
        ApplySelection();
    }

    void OnMore(object sender, RoutedEventArgs e) { _limit += Page; BuildRuns(); }

    /// <summary>One run row for a scenario.</summary>
    Border RunRow(RunRecord r, bool pb)
    {
        var replay = SessionStore.FindRun(Infos(), r.Scenario, r.End) >= 0;
        var g = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        void Put(UIElement e, int col)
        {
            if (e is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(4, 0, 4, 0); }
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
        TextBlock T(string text, Brush? fg = null, bool bold = false) => new() { Text = text, FontSize = 13, Foreground = fg ?? FgB, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        Put(T(L.MonthDay(r.End) + " " + L.Time(r.End), DimB), 0);
        if (TierOf(r.Score) is { } t)
        {
            var tierText = T(t.Label, ChartPaths.TextTone(t.Brush));
            tierText.HorizontalAlignment = HorizontalAlignment.Center;
            Put(tierText, 1);
        }
        var scoreText = T(r.Score.ToString("0.#"), TierOf(r.Score) is { } rt ? ChartPaths.TextTone(rt.Brush) : null, true);
        scoreText.HorizontalAlignment = HorizontalAlignment.Right;
        Put(scoreText, 2);
        if (pb)
        {
            var green = (Brush)FindResource("Green");
            var pill = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            pill.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = green, Opacity = 0.18 });
            pill.Children.Add(new TextBlock { Text = L.T("PB"), Foreground = green, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 1, 6, 1) });
            Put(pill, 3);
        }
        if (replay)
        {
            var play = T("", (Brush)FindResource("Accent"));
            play.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            play.FontSize = 15;
            play.HorizontalAlignment = HorizontalAlignment.Center;
            Put(play, 4);
        }
        else if (_host.IsPending(r.Scenario, r.End))
        {
            var ring = ProgressRing.Create(_host, r.Scenario, r.End, (Brush)FindResource("Accent"), 16);
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            Put(ring, 4);
        }
        var row = new Border { Height = 34, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Child = g, Cursor = Cursors.Hand };
        var end = r.End;
        _rows[end] = row;
        row.MouseEnter += (_, _) => Paint(row, end, false);
        row.MouseLeave += (_, _) => Paint(row, end, false);
        var scn = r.Scenario;
        RowClick.Attach(row, _ => { if (_selEnd == end) ShowOverview(); else SelectRun(end, scn); }, down => Paint(row, end, down));
        return row;
    }

    void Paint(Border row, DateTime end, bool down) =>
        row.Background = _runMode && _runIdx >= 0 && _runIdx < _stats.Runs.Count && _stats.Runs[_runIdx].End == end ? Solid("#330A84FF")
            : down ? Solid("#14FFFFFF") : row.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent;

    void ApplySelection()
    {
        foreach (var (end, row) in _rows) Paint(row, end, false);
    }

    int NearestRun(DateTime end)
    {
        var best = -1;
        for (var i = 0; i < _stats.Runs.Count; i++)
        {
            var gap = (_stats.Runs[i].End - end).Duration();
            if (gap < SessionStore.RunMatchTolerance && (best < 0 || gap < (_stats.Runs[best].End - end).Duration())) best = i;
        }
        return best;
    }

    // ---- stage: overview ---------------------------------------------------------------------------------------

    void OnOverview(object sender, RoutedEventArgs e) => ShowOverview();

    /// <summary>Overview chart click: selects the run when it is a local one (server-only scores have nothing to open).</summary>
    void OnBigChartClicked(int idx)
    {
        if (_runMode || idx < 0 || idx >= _stats.Records.Count) return;
        if (_stats.Records[idx].Run is { } r) SelectRun(r.End);
    }

    void SetMode(bool run)
    {
        if (!run) ExitFullscreen();
        _runMode = run;
        OverviewScroll.Visibility = run ? Visibility.Collapsed : Visibility.Visible;
        RunPanel.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        FolderBtn.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        RunDelBtn.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
        DisarmDelete();
        FitHeader();
    }

    bool _delArmed;

    void DisarmDelete()
    {
        _delArmed = false;
        RunDelBtn.Foreground = DimB;
        RunDelBtn.ToolTip = L.T("Delete play");
    }

    /// <summary>Trash button of the open run: the first click arms it (red), the second deletes the run and closes the popup.</summary>
    void OnDeleteRun(object sender, RoutedEventArgs e)
    {
        if (_runIdx < 0 || _runIdx >= _stats.Runs.Count) return;
        if (!_delArmed)
        {
            _delArmed = true;
            RunDelBtn.Foreground = (Brush)FindResource("Red");
            RunDelBtn.ToolTip = L.T("Click again to delete the replay for good");
            return;
        }
        var r = _stats.Runs[_runIdx];
        Pause();
        Close();
        _host.DeleteRun(r.Scenario, r.End, r.Score);
    }

    void ShowOverview()
    {
        Pause();
        _selEnd = null; _runIdx = -1;
        SetMode(false);
        ApplySelection();
        BuildOverview();
        OverviewScroll.ScrollToTop();
    }

    void BuildOverview()
    {
        OverviewHost.Children.Clear();
        var bands = _d!.TierBands(_scenario);
        if (_stats.Plays == 0) { OverviewHost.Children.Add(EmptyCard(bands)); return; }
        var s = _stats;
        OverviewHost.Children.Add(Section(null, SummaryTable(s), new Thickness(0, 0, 0, 12)));

        if (bands is { Count: > 0 }) OverviewHost.Children.Add(TierCard(bands, BestScore));

        var chartBody = new StackPanel();
        chartBody.Children.Add(new TextBlock { Text = L.T("Score per play"), Style = (Style)FindResource("Caption"), FontSize = 12.5, Margin = new Thickness(4, 0, 0, 8) });
        _bigChart.PointClicked -= OnBigChartClicked;
        _bigChart.PointClicked += OnBigChartClicked;
        if (_bigChart.Parent is Panel old) old.Children.Remove(_bigChart);
        chartBody.Children.Add(_bigChart);
        var points = s.Records.Select(r => (r.Time, r.Score)).ToList();
        _bigChart.Set(points, "0.#", ChartKind.Line, true, bands);
        OverviewHost.Children.Add(new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 4, 0, 0), Child = chartBody });

        var cards = new List<UIElement>();
        if (DistributionCard(s) is { } dist) cards.Add(dist);
        if (ProgressionCard(s, bands) is { } prog) cards.Add(prog);
        if (AccuracyCard(s) is { } acc) cards.Add(acc);
        if (TimeOfDayCard(s) is { } tod) cards.Add(tod);
        if (cards.Count > 0)
        {
            var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 12, -12, 0) };
            foreach (var c in cards) grid.Children.Add(c);
            grid.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width < 760 ? 1 : 2;
            OverviewHost.Children.Add(grid);
        }
        if (SessionOrderCard(s) is { } order) OverviewHost.Children.Add(order);
        _ = RefreshCloudAsync(_scenario);
    }

    /// <summary>A card with an optional title; <paramref name="note"/> sits dimmed at the title's right end.</summary>
    Border Section(string? title, UIElement body, Thickness margin, string? note = null)
    {
        var sp = new StackPanel();
        if (title is not null)
        {
            var head = new Grid { Margin = new Thickness(4, 0, 4, 8) };
            head.Children.Add(new TextBlock { Text = L.T(title), Style = (Style)FindResource("Caption"), FontSize = 12.5 });
            if (note is not null) head.Children.Add(new TextBlock { Text = note, Foreground = DimB, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(head);
        }
        sp.Children.Add(body);
        return new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 14), Margin = margin, Child = sp };
    }

    /// <summary>A label column and one value column per head, with a hairline above each row.</summary>
    UIElement Table(IReadOnlyList<string> heads, IReadOnlyList<(string Label, IReadOnlyList<(string Text, Brush? Tone)> Cells)> rows)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        foreach (var _ in heads) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r <= rows.Count; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(int row, int col, string text, Brush fg, double size, bool bold, bool right)
        {
            var t = new TextBlock
            {
                Text = text, Foreground = fg, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(4, row == 0 ? 0 : 7, 4, 7),
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(t, row); Grid.SetColumn(t, col);
            g.Children.Add(t);
        }
        for (var c = 0; c < heads.Count; c++) Put(0, c + 1, heads[c], DimB, 11.5, false, true);
        for (var r = 0; r < rows.Count; r++)
        {
            var line = new Border { BorderBrush = (Brush)FindResource("EdgeFaint"), BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
            Grid.SetRow(line, r + 1); Grid.SetColumnSpan(line, heads.Count + 1);
            g.Children.Add(line);
            Put(r + 1, 0, rows[r].Label, DimB, 13, false, false);
            for (var c = 0; c < rows[r].Cells.Count; c++) Put(r + 1, c + 1, rows[r].Cells[c].Text, rows[r].Cells[c].Tone ?? FgB, 13, true, true);
        }
        return g;
    }

    /// <summary>All time, last 10 plays and last 30 days side by side (a window that equals all time is left out).</summary>
    UIElement SummaryTable(ScenarioStats s)
    {
        var windows = new List<(string Head, ScoreSummary? Sum)> { (L.T("All time"), ScenarioInsights.Summarize(s.Records)) };
        if (s.Plays > 10) windows.Add((L.T("Last 10"), ScenarioInsights.Summarize(s.Records.Skip(s.Plays - 10).ToList())));
        var since = DateTime.Now.AddDays(-30);
        var recent = s.Records.Where(r => r.Time >= since).ToList();
        if (recent.Count > 0 && recent.Count < s.Plays) windows.Add((L.T("Last 30 days"), ScenarioInsights.Summarize(recent)));

        const string Dash = "–";
        Brush? Tone(double score) => TierOf(score) is { } t ? ChartPaths.TextTone(t.Brush) : null;
        var rows = new List<(string Label, IReadOnlyList<(string Text, Brush? Tone)> Cells)>();
        void Add(string label, Func<ScoreSummary, string?> f) =>
            rows.Add((L.T(label), windows.Select(w => (w.Sum is { } x && f(x) is { } t ? t : Dash, (Brush?)null)).ToList()));
        Add("Plays", x => x.Plays.ToString());
        rows.Add((L.T("Best"), windows.Select((w, c) =>
        {
            var best = c == 0 ? BestScore : w.Sum?.Best;
            return best is { } b ? (b.ToString("0.#"), Tone(b)) : (Dash, (Brush?)null);
        }).ToList()));
        Add("Average", x => x.Average.ToString("0.#"));
        Add("Median", x => x.Median.ToString("0.#"));
        Add("Consistency (CV)", x => x.Consistency?.ToString("0.0%"));
        Add("Accuracy", x => x.Accuracy?.ToString("P1"));
        Add("Drift", x => x.Drift is { } d ? (d * 100).ToString("+0.0;-0.0;0") + L.T(" pts") : null);
        Add("Time", x => x.TimePlayed > TimeSpan.Zero ? Dur(x.TimePlayed) : null);
        return Table(windows.Select(w => w.Head).ToList(), rows);
    }

    /// <summary>Histogram of every score; the dashed line is the average.</summary>
    UIElement? DistributionCard(ScenarioStats s)
    {
        var scores = s.Records.Select(r => r.Score).ToList();
        var bins = ScenarioInsights.Distribution(scores);
        if (bins.Count == 0) return null;
        var step = (int)Math.Ceiling(bins.Count / 4.0);
        var items = bins.Select((b, i) => new BarItem(i % step == 0 ? b.From.ToString("0") : null, b.Count, $"{b.From:0} – {b.To:0}")).ToList();
        var chart = new BarChart { Height = 170 };
        chart.Set(items, "0", (scores.Average() - bins[0].From) / (bins[^1].To - bins[0].From), L.T("avg"));
        return Section("Score distribution", chart, new Thickness(0, 0, 12, 12), ScenarioInsights.Consistency(scores) is { } cv ? "CV " + cv.ToString("0.0%") : null);
    }

    /// <summary>The best score so far after each play: when the personal bests came and how far they moved.</summary>
    UIElement? ProgressionCard(ScenarioStats s, List<ChartBand>? bands)
    {
        if (s.PersonalBests.Count < 2) return null;
        var best = s.BestSoFar();
        var chart = new ScoreChart { Height = 170 };
        chart.Set(s.Records.Select((r, i) => (r.Time, best[i])).ToList(), "0.#", ChartKind.Line, false, bands);
        var first = s.PersonalBests[0].Score;
        return Section("Personal best history", chart, new Thickness(0, 0, 12, 12), first > 0 ? ((s.Best - first) / first).ToString("+0.0%;-0.0%;0%") : null);
    }

    /// <summary>Accuracy against score for the local runs: whether pushing the score costs accuracy.</summary>
    UIElement? AccuracyCard(ScenarioStats s)
    {
        var runs = s.Runs.Where(r => r.Accuracy > 0).ToList();
        if (runs.Count < 5) return null;
        var chart = new ScatterChart { Height = 170 };
        chart.Set(runs.Select(r => (r.Accuracy, r.Score, L.Stamp(r.End))).ToList(), "0%", "0.#");
        double mx = runs.Average(r => r.Accuracy), my = runs.Average(r => r.Score);
        double sxy = runs.Sum(r => (r.Accuracy - mx) * (r.Score - my)), sxx = runs.Sum(r => (r.Accuracy - mx) * (r.Accuracy - mx)), syy = runs.Sum(r => (r.Score - my) * (r.Score - my));
        return Section("Accuracy vs score", chart, new Thickness(0, 0, 12, 12), sxx > 0 && syy > 0 ? "r " + (sxy / Math.Sqrt(sxx * syy)).ToString("+0.00;-0.00;0.00") : null);
    }

    /// <summary>Average score of the runs started in each part of the day.</summary>
    UIElement? TimeOfDayCard(ScenarioStats s)
    {
        var blocks = ScenarioInsights.ByTimeOfDay(s.Runs);
        if (blocks.Count < 2) return null;
        string Hour(int h) => new DateTime(2000, 1, 1, h % 24, 0, 0).ToString("h tt", L.DateCulture);
        var items = blocks.Select(b => new BarItem(Hour(b.Hour), b.Average, $"{Hour(b.Hour)} – {Hour(b.Hour + 3)} · {L.F("{0} plays", b.Runs)}")).ToList();
        var chart = new BarChart { Height = 170 };
        chart.Set(items, "0.#", fromZero: false);
        return Section("Time of day", chart, new Thickness(0, 0, 12, 12));
    }

    /// <summary>First, second and later plays of the scenario within one session: how much warming up matters.</summary>
    UIElement? SessionOrderCard(ScenarioStats s)
    {
        var slots = ScenarioInsights.BySessionPosition(PlaySession.Group(_d!.Lib.AllRuns), _scenario);
        if (slots.Count < 2 || s.Average <= 0) return null;
        string Name(SessionSlot x) => x.OrLater ? L.T("3rd or later") : x.Position == 1 ? L.T("1st") : L.T("2nd");
        var rows = slots.Select(x =>
        {
            var vs = (x.Average - s.Average) / s.Average;
            Brush? tone = vs > 0.0005 ? (Brush)FindResource("Green") : vs < -0.0005 ? (Brush)FindResource("Red") : null;
            return (Name(x), (IReadOnlyList<(string Text, Brush? Tone)>)new List<(string, Brush?)> { (x.Plays.ToString(), null), (x.Average.ToString("0.#"), null), (vs.ToString("+0.0%;-0.0%;0%"), tone) });
        }).ToList();
        return Section("Play order in a session", Table([L.T("Plays"), L.T("Average"), L.T("vs overall avg")], rows), new Thickness(0, 0, 0, 0));
    }

    /// <summary>Brings the server scores up to date (older runs, other PCs) and rebuilds the sheet from the score book when they changed.</summary>
    async Task RefreshCloudAsync(string scenario)
    {
        if (!await _d!.LoadCloud(scenario) || scenario != _scenario) return;
        _stats = _d.Book.For(scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestRunIndexes());
        if (!_runMode) BuildOverview();
        BuildRuns();
    }

    /// <summary>Current tier pill and progress to the next tier. A band's value is the score that reaches it.</summary>
    UIElement TierCard(List<ChartBand> bands, double best)
    {
        var cur = bands.LastOrDefault(b => b.Value <= best);
        var next = bands.FirstOrDefault(b => b.Value > best);
        var sp = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var pillBrush = cur?.Brush ?? Solid("#8E8E93");
        var pill = new Grid { VerticalAlignment = VerticalAlignment.Center };
        pill.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = pillBrush, Opacity = 0.22 });
        pill.Children.Add(new TextBlock { Text = cur?.Label ?? L.T("UNRANKED"), Foreground = ChartPaths.TextTone(pillBrush), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 4, 12, 4) });
        head.Children.Add(pill);
        head.Children.Add(new TextBlock
        {
            Text = next is null ? L.T("Top tier reached") : L.F("{0} to {1} ({2})", (next.Value - best).ToString("#,0.#"), next.Label, next.Value.ToString("#,0.##")),
            Foreground = DimB, FontSize = 13, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(head);
        var lo = cur?.Value ?? 0;
        var frac = next is null ? 1 : Math.Clamp((best - lo) / Math.Max(1e-9, next.Value - lo), 0, 1);
        var bar = new Grid { Height = 8, Margin = new Thickness(0, 12, 0, 0) };
        bar.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = Solid("#14FFFFFF") });
        var fill = new Grid();
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
        fill.Children.Add(new Border { CornerRadius = new CornerRadius(4), Background = (next ?? cur)!.Brush });
        bar.Children.Add(fill);
        sp.Children.Add(bar);
        var ends = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        ends.Children.Add(new TextBlock { Text = lo.ToString("#,0.##"), Foreground = DimB, FontSize = 11 });
        if (next is not null) ends.Children.Add(new TextBlock { Text = next.Value.ToString("#,0.##"), Foreground = DimB, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
        sp.Children.Add(ends);
        return new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 12), Margin = new Thickness(0, 0, 0, 12), Child = sp };
    }

    UIElement EmptyCard(List<ChartBand>? bands)
    {
        var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        sp.Children.Add(new TextBlock { Text = L.T("No local plays yet"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (bands is { Count: > 0 })
        {
            sp.Children.Add(new TextBlock { Text = L.T("TIER THRESHOLDS"), Foreground = DimB, FontSize = 11, Margin = new Thickness(0, 16, 0, 8) });
            var wrap = new WrapPanel();
            foreach (var b in bands)
            {
                var chip = new StackPanel { Orientation = Orientation.Horizontal };
                chip.Children.Add(new TextBlock { Text = b.Label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ChartPaths.TextTone(b.Brush), Margin = new Thickness(0, 0, 8, 0) });
                chip.Children.Add(new TextBlock { Text = b.Value.ToString("#,0.##"), FontSize = 12.5, Foreground = FgB });
                wrap.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = Solid("#14FFFFFF"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 8), Child = chip });
            }
            sp.Children.Add(wrap);
        }
        return new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 18, 20, 14), Child = sp };
    }

    // ---- stage: one run ----------------------------------------------------------------------------------------

    void SelectRun(DateTime end, string? scenario = null)
    {
        if (scenario is not null) SetScenario(scenario);
        _selEnd = end;
        _runIdx = NearestRun(end);
        if (_runIdx >= 0) _selEnd = _stats.Runs[_runIdx].End;
        var si = SessionStore.FindRun(Infos(), _scenario, end);
        SetMode(true);
        ApplySelection();
        if (si < 0) { if (_host.IsPending(_scenario, end)) ShowEncoding(end); else NoRecording(); return; }
        var item = _items[si];
        if (_s is not null && _s.Folder == item.Folder && (_duration > 0 || Notice.Visibility == Visibility.Visible)) return;
        LoadSession(item);
    }

    void SetRecorded(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        ChartCard.Visibility = v; Seek.Visibility = v; TransportBar.Visibility = v;
    }

    /// <summary>The run's clip is still being encoded: a progress ring takes the video's place.</summary>
    void ShowEncoding(DateTime end)
    {
        UnloadMedia();
        SetRecorded(false);
        Notice.Visibility = Visibility.Collapsed;
        PendingHost.Child = ProgressRing.Create(_host, _scenario, end, (Brush)FindResource("Accent"), 56, 4);
        PendingHost.Visibility = Visibility.Visible;
    }

    void NoRecording()
    {
        UnloadMedia();
        SetRecorded(false);
        Notice.Text = L.T("No recording for this play");
        Notice.Visibility = Visibility.Visible;
    }

    /// <summary>Stops playback and releases the loaded session and its video.</summary>
    void UnloadMedia()
    {
        FlushNudge();
        ExitFullscreen();
        _playing = false; PlayBtn.Content = PlayGlyph;
        _seekTimer.Stop(); _seekPending = false; _dragging = false; _resumeAfterDrag = false; _holdUntil = 0;
        Media.Close();
        Media.Source = null;
        PendingHost.Child = null; PendingHost.Visibility = Visibility.Collapsed;
        _s = null;
        _duration = 0;
        SetTransport(false);
        Overlay.Set(null, default, 0, 0, 0);
        Chart.Set([], 0);
        Chart.SetGhost([]);
        PrevChip.Visibility = Visibility.Collapsed;
        _metrics.Clear();
        TimeNow.Text = "0:00"; TimeTotal.Text = "0:00";
        FitScreen();
    }

    void LoadSession(SessionItem item)
    {
        UnloadMedia();
        SetRecorded(true);
        try { _s = SessionStore.Load(item.Folder); }
        catch (Exception ex) { _s = null; SetRecorded(false); Notice.Text = ex.Message; Notice.Visibility = Visibility.Visible; return; }

        ShowNudge();
        _runLength = (_s.Info.End - _s.Info.Start).TotalSeconds;
        _killTimes = _s.Stats is { } st
            ? st.KillEvents.Select(k => (k.Time - st.Start).TotalSeconds).ToArray()
            : (_s.Trajectory?.Events.Where(x => x.Type == TrajectoryEventType.Kill).Select(x => (double)x.TSec).ToArray() ?? []);
        _outcomes = ClassifyShots();
        _held = _s.Trajectory is { } tj ? new HeldAccuracy(tj.Events, _s.Perf, _runLength) : null;

        if (_s.VideoPath is { } path)
        {
            Notice.Visibility = Visibility.Collapsed;
            Media.Source = new Uri(path);
            Media.Play(); // paused again in MediaOpened; a loaded-but-never-played element renders no frame
        }
        else
        {
            Media.Source = null;
            SetRecorded(false);
            Notice.Text = L.T("No video for this session");
            Notice.Visibility = Visibility.Visible;
        }
    }

    IReadOnlyDictionary<float, ShotOutcome> ClassifyShots()
    {
        if (_s?.Trajectory is not { } traj || _s.Stats is not { } st) return new Dictionary<float, ShotOutcome>();
        var kills = st.KillEvents.Select((k, i) => new KillShots(_killTimes[i], k.Shots, k.Hits)).ToList();
        return ShotClassifier.Classify(traj.Events, kills, _s.Perf, out _);
    }

    // ---- player ------------------------------------------------------------------------------------------------

    void SetTransport(bool on)
    {
        TransportBar.IsEnabled = on; Seek.IsEnabled = on; FolderBtn.IsEnabled = on && _s?.VideoPath is { } p && File.Exists(p);
        NudgeBox.Visibility = on && _s?.Trajectory is { Samples.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- trail sync nudge --------------------------------------------------------------------------------------
    // SyncNudgeMs is added to VideoTime (mouse -> video), so + shows each mouse moment later in the video: the trail lags.

    void OnNudgeMinus(object sender, RoutedEventArgs e) => Nudge(-NudgeStepMs);
    void OnNudgePlus(object sender, RoutedEventArgs e) => Nudge(NudgeStepMs);
    void OnNudgeReset(object sender, MouseButtonEventArgs e) { SetNudge(0); e.Handled = true; }

    void Nudge(double deltaMs) { if (_s is not null) SetNudge(_s.Info.SyncNudgeMs + deltaMs); }

    void SetNudge(double ms)
    {
        if (_s is null || NudgeBox.Visibility != Visibility.Visible) return;
        ms = Math.Clamp(Math.Round(ms), -NudgeMaxMs, NudgeMaxMs);
        if (ms == _s.Info.SyncNudgeMs) return;
        _s = _s with { Info = _s.Info with { SyncNudgeMs = ms } };
        var i = _items.FindIndex(x => x.Folder == _s.Folder);
        if (i >= 0) _items[i] = _items[i] with { Info = _s.Info };
        ShowNudge();
        BuildChart(); // chart points and kill markers sit at video times; the trail follows on the next frame
        _nudgePending = (_s.Folder, _s.Info);
        _nudgeTimer.Stop(); _nudgeTimer.Start();
    }

    void ShowNudge()
    {
        var ms = _s?.Info.SyncNudgeMs ?? 0;
        NudgeText.Text = ms == 0 ? "0 ms" : (ms > 0 ? "+" : "−") + Math.Abs(ms).ToString("0") + " ms";
        NudgeText.Foreground = ms == 0 ? DimB : FgB;
    }

    /// <summary>Writes the pending nudge to its session.json now (the debounce timer, session change, close).</summary>
    void FlushNudge()
    {
        _nudgeTimer.Stop();
        if (_nudgePending is not var (folder, info)) return;
        _nudgePending = null;
        try { SessionStore.WriteInfo(folder, info); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ---- fullscreen --------------------------------------------------------------------------------------------
    // The window itself goes borderless over its monitor and everything but the stage is collapsed, so the one
    // MediaElement keeps playing. TrailOverlay sizes its video rect from its own ActualSize, so it follows.

    UIElement[] Bar => [BarScrim, ChartCard, Seek, TransportBar];

    Brush? _scrimDefault;

    /// <summary>
    /// Fullscreen bar: the scrim fades into the theme background and the controls take black or white by the background's luminance,
    /// so they read on every theme however the video behind them looks.
    /// </summary>
    void StyleBar(Color bg)
    {
        _scrimDefault ??= BarScrim.Background;
        BarScrim.Background = new LinearGradientBrush(
        [
            new GradientStop(Color.FromArgb(0, bg.R, bg.G, bg.B), 0),
            new GradientStop(Color.FromArgb(0xE0, bg.R, bg.G, bg.B), 0.45),
        ], new Point(0, 0), new Point(0, 1));
        var light = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255 > 0.5;
        var ink = light ? Colors.Black : Colors.White;
        SolidColorBrush Ink(byte a) { var b = new SolidColorBrush(Color.FromArgb(a, ink.R, ink.G, ink.B)); b.Freeze(); return b; }
        foreach (var el in new FrameworkElement[] { ChartCard, TransportBar })
        {
            el.Resources["Fg"] = Ink(0xF2);
            el.Resources["Dim"] = Ink(0xB3);
            el.Resources["Wash"] = Ink(0x1A);
            el.Resources["WashPress"] = Ink(0x2E);
            el.Resources["Hover"] = Ink(0x1F);
            el.Resources["Selected"] = Ink(0x29);
            el.Resources["Card"] = Ink(0x14);
            el.Resources["Tint"] = Ink(0x0D);
            el.Resources["EdgeFaint"] = Ink(0x1F);
        }
    }

    void OnFsClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    void OnDelLeave(object sender, MouseEventArgs e) => DisarmDelete();

    void OnScreenDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left) { ToggleFullscreen(); e.Handled = true; }
    }

    void ToggleFullscreen()
    {
        if (_fs is not null) ExitFullscreen();
        else EnterFullscreen();
    }

    void EnterFullscreen()
    {
        if (_fs is not null || !_open || !_runMode || _duration <= 0 || Window.GetWindow(this) is not { } win) return;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(win);
        if (chrome is null || PresentationSource.FromVisual(win) is not System.Windows.Interop.HwndSource src) return;
        var max = win.WindowState == WindowState.Maximized;
        var px = System.Windows.Forms.Screen.FromHandle(src.Handle).Bounds;
        var tl = src.CompositionTarget.TransformFromDevice.Transform(new Point(px.Left, px.Top));
        var br = src.CompositionTarget.TransformFromDevice.Transform(new Point(px.Right, px.Bottom));
        _fs = new FsState(win, max, max ? win.RestoreBounds : new Rect(win.Left, win.Top, win.Width, win.Height), win.Topmost, chrome.ResizeBorderThickness, chrome.CaptionHeight,
            win.FindName("TitleBar") as UIElement, Card.HorizontalAlignment, Card.VerticalAlignment, Card.Margin, Card.CornerRadius, Card.BorderThickness, Card.Background,
            Card.Effect, ((Grid)Clipper.Child).Margin, Stage.Margin);
        if (_fs.Title is { } title) title.Visibility = Visibility.Collapsed;
        chrome.ResizeBorderThickness = new Thickness(0);
        chrome.CaptionHeight = 0;
        win.Topmost = true;
        win.WindowState = WindowState.Normal;
        win.Left = tl.X; win.Top = tl.Y; win.Width = br.X - tl.X; win.Height = br.Y - tl.Y;

        Card.HorizontalAlignment = HorizontalAlignment.Stretch; Card.VerticalAlignment = VerticalAlignment.Stretch;
        Card.ClearValue(WidthProperty); Card.ClearValue(HeightProperty);
        Card.Margin = new Thickness(0); Card.CornerRadius = new CornerRadius(0); Card.BorderThickness = new Thickness(0);
        var bg = (Brush)FindResource("Bg");
        Card.Background = bg; Screen.Background = bg; Card.Effect = null; Clipper.Clip = null;
        StyleBar(bg is SolidColorBrush sb ? sb.Color : Colors.Black);
        ((Grid)Clipper.Child).Margin = new Thickness(0);
        SideRow.Height = new GridLength(0); GapRow.Height = new GridLength(0); Side.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Collapsed;
        Stage.Margin = new Thickness(0); Screen.CornerRadius = new CornerRadius(0);
        Grid.SetRow(Stage, 0); Grid.SetRowSpan(Stage, 5);
        BarScrim.Visibility = Visibility.Visible;
        ChartCard.Margin = new Thickness(24, 0, 24, 0); Seek.Margin = new Thickness(35, 8, 35, 0); TransportBar.Margin = new Thickness(24, 8, 24, 16);
        FsBtn.Content = "\uE73F"; FsBtn.ToolTip = L.T("Exit fullscreen (F)");
        _fsMouse = Mouse.GetPosition(this);
        ShowBar();
    }

    void ExitFullscreen()
    {
        if (_fs is not { } f) return;
        _fs = null;
        _fsTimer.Stop();
        Cursor = null;
        foreach (var el in Bar) { el.Opacity = 1; el.IsHitTestVisible = true; }
        BarScrim.IsHitTestVisible = false; BarScrim.Visibility = Visibility.Collapsed;
        BarScrim.Background = _scrimDefault; Screen.Background = Brushes.Black;
        ChartCard.Resources.Clear(); TransportBar.Resources.Clear();
        FsBtn.Content = "\uE740"; FsBtn.ToolTip = L.T("Fullscreen (F)");
        ChartCard.Margin = new Thickness(0, 12, 0, 0); Seek.Margin = new Thickness(11, 8, 11, 0); TransportBar.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(Stage, 1); Grid.SetRowSpan(Stage, 1);
        Stage.Margin = f.StageMargin; Screen.CornerRadius = new CornerRadius(14);
        Side.Visibility = Visibility.Visible; Header.Visibility = Visibility.Visible; GapRow.Height = new GridLength(12);
        ((Grid)Clipper.Child).Margin = f.GridMargin;
        Card.HorizontalAlignment = f.HAlign; Card.VerticalAlignment = f.VAlign; Card.Margin = f.CardMargin;
        Card.CornerRadius = f.CardRadius; Card.BorderThickness = f.CardBorder; Card.Background = f.CardBg; Card.Effect = f.Effect;
        LayoutCard(f.Bounds.Size); // SizeChanged corrects it once the window is back
        var win = f.Win;
        var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(win);
        win.Left = f.Bounds.Left; win.Top = f.Bounds.Top; win.Width = f.Bounds.Width; win.Height = f.Bounds.Height;
        if (chrome is not null) { chrome.ResizeBorderThickness = f.Resize; chrome.CaptionHeight = f.Caption; }
        win.Topmost = f.Topmost;
        if (f.Maximized) win.WindowState = WindowState.Maximized;
        if (f.Title is { } title) title.Visibility = Visibility.Visible;
    }

    void OnFsMouse(object sender, MouseEventArgs e)
    {
        if (_fs is null) return;
        var p = e.GetPosition(this);
        if ((p - _fsMouse).LengthSquared < 1) return; // layout changes raise MouseMove without movement
        _fsMouse = p;
        ShowBar();
    }

    void ShowBar()
    {
        Cursor = null;
        foreach (var el in Bar) { el.Opacity = 1; if (el != BarScrim) el.IsHitTestVisible = true; }
        _fsTimer.Stop(); _fsTimer.Start();
    }

    void HideBar()
    {
        if (_fs is null) { _fsTimer.Stop(); return; }
        if (_dragging || Seek.IsMouseOver || ChartCard.IsMouseOver || TransportBar.IsMouseOver || SpeedPop.IsOpen) return; // stay while in use; re-check next tick
        _fsTimer.Stop();
        foreach (var el in Bar) { el.Opacity = 0; el.IsHitTestVisible = false; }
        Cursor = Cursors.None;
    }

    void OnTrailClick(object sender, RoutedEventArgs e) => SetTrail(TrailBtn.IsChecked == true);

    void SetTrail(bool on)
    {
        TrailBtn.IsChecked = on;
        Overlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _host.Ui.ShowTrail = on;
        _host.SaveUi();
    }

    void OnShowVideo(object sender, RoutedEventArgs e)
    {
        if (_s?.VideoPath is { } p && File.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_s is null || !Media.NaturalDuration.HasTimeSpan) return;
        _duration = Media.NaturalDuration.TimeSpan.TotalSeconds;
        Seek.Maximum = _duration;
        Media.SpeedRatio = _speed;
        Media.Pause();
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(_s.Info.VideoTime(0) - 1, 0, _duration));
        BuildChart();
        SetTransport(true);
        FitScreen();
        if (!_playing) TogglePlay();
    }

    void OnStageSized(object sender, SizeChangedEventArgs e) => FitScreen();

    void FitScreen()
    {
        double W = Stage.ActualWidth, H = Stage.ActualHeight;
        if (Media.NaturalVideoHeight <= 0 || W <= 0 || H <= 0) { Screen.Margin = new Thickness(0); return; }
        var a = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var w = Math.Min(W, H * a);
        var h = w / a;
        Screen.Margin = new Thickness((W - w) / 2, (H - h) / 2, (W - w) / 2, (H - h) / 2);
    }

    void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false; PlayBtn.Content = PlayGlyph;
        Media.Pause();
    }

    void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        AppLog.Write("replay", "media failed: " + e.ErrorException);
        ErrorReporter.Report("replay media", e.ErrorException);
        SetTransport(false);
        Notice.Text = L.T("Video failed to load: ") + e.ErrorException.Message;
        Notice.Visibility = Visibility.Visible;
    }

    void OnMetricClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MetricRow m }) return;
        m.Visible = !m.Visible;
        if (m.Visible) _visible.Add(m.Series); else _visible.Remove(m.Series);
        Chart.SetVisible(_visible);
        _host.Ui.VisibleSeries = ChartSeriesInfo.All.Where(_visible.Contains).Select(x => x.ToString()).ToList();
        _host.SaveUi();
    }

    /// <summary>Playhead in video seconds: the pending drag position while a seek is not yet applied, else the media clock.</summary>
    double CurPos => _dragging || _seekPending ? _shownPos : Environment.TickCount64 < _holdUntil ? _dragPos : Media.Position.TotalSeconds;

    void OnRendering(object? sender, EventArgs e)
    {
        if (!_open || !_runMode || _s is null || _duration <= 0) return;
        var pos = CurPos;
        if (!_dragging) Seek.Value = Math.Clamp(pos, 0, _duration);
        var t = _s.Info.MouseTime(pos);
        TimeNow.Text = Clock(Math.Min(t, _runLength)); TimeTotal.Text = Clock(_runLength);
        Chart.SetPlayhead(pos);
        var cur = Chart.At(pos);
        foreach (var m in _metrics) m.Value = cur is { } c ? ChartSeriesInfo.Format(m.Series, c[m.Series]) : "–";

        if (!_host.Ui.ShowTrail) return;
        if (_s.Trajectory is not { Samples.Count: > 0 } traj || Media.NaturalVideoHeight == 0 || t < 0 || t > _runLength + 0.5)
        {
            Overlay.Set(null, default, 0, 0, 0);
            return;
        }
        double videoAspect = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var st = _s.Info.Settings;
        var renderAspect = ViewProjection.ParseAspect(st.Resolution) ?? videoAspect;
        var hfov = ViewProjection.HorizontalFovDeg(st.Fov, st.FovScale, renderAspect);
        Overlay.Set(CameraPath.BuildTrail(traj.Samples, traj.Events, t, outcomes: _outcomes), CameraPath.At(traj.Samples, t), hfov, videoAspect, renderAspect, _held);
    }
    void BeginDrag()
    {
        _shownPos = Media.Position.TotalSeconds;
        if (_playing) { Media.Pause(); _resumeAfterDrag = true; }
    }



    // Scrubbing: the playhead, metrics and trail follow the drag at once (cheap); the video only seeks once the
    // marker has rested for SeekDelay, or on release, because decoding a frame per mouse move lags.
    void DragTo(double sec)
    {
        _dragPos = Math.Clamp(sec, 0, _duration);
        _seekPending = true;
        if (!_seekTimer.IsEnabled) _seekTimer.Start();
    }

    void CommitSeek()
    {
        _seekTimer.Stop();
        if (!_seekPending) return;
        _seekPending = false;
        _shownPos = _dragPos;
        _holdUntil = Environment.TickCount64 + 250; // Media.Position settles asynchronously; keep showing the target meanwhile
        Media.Position = TimeSpan.FromSeconds(_dragPos);
    }

    bool _trackScrub;

    void OnSeekDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        BeginDrag();
        _dragging = true;
        if (Seek.Template.FindName("PART_Track", Seek) is System.Windows.Controls.Primitives.Track t && !t.Thumb.IsMouseOver)
        {
            _trackScrub = true;
            Seek.Value = t.ValueFromPoint(e.GetPosition(t));
            DragTo(Seek.Value);
            Seek.CaptureMouse();
        }
    }
    void OnSeekMove(object sender, MouseEventArgs e)
    {
        if (!_trackScrub || !Seek.IsMouseCaptured) return;
        if (Seek.Template.FindName("PART_Track", Seek) is System.Windows.Controls.Primitives.Track t)
        {
            Seek.Value = t.ValueFromPoint(e.GetPosition(t));
            DragTo(Seek.Value);
        }
    }
    void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        if (_trackScrub && Seek.IsMouseCaptured) { Seek.ReleaseMouseCapture(); return; } // LostMouseCapture finishes it
        EndSeek();
    }
    void OnSeekLostCapture(object sender, MouseEventArgs e)
    {
        if (_trackScrub && !Seek.IsMouseCaptured) EndSeek();
    }
    void EndSeek()
    {
        if (!_dragging) return;
        _dragging = false;
        _trackScrub = false;
        DragTo(Seek.Value);
        CommitSeek();
        if (_resumeAfterDrag) { _resumeAfterDrag = false; Media.Play(); }
    }
    void OnChartDown(object sender, MouseButtonEventArgs e) { if (_duration <= 0) return; BeginDrag(); _dragging = true; Chart.CaptureMouse(); SeekToChart(e); }
    void OnChartMove(object sender, MouseEventArgs e) { if (_dragging && Chart.IsMouseCaptured) SeekToChart(e); }
    void OnChartUp(object sender, MouseButtonEventArgs e)
    {
        if (!Chart.IsMouseCaptured) return;
        SeekToChart(e);
        Chart.ReleaseMouseCapture();
        _dragging = false;
        CommitSeek();
        if (_resumeAfterDrag) { _resumeAfterDrag = false; Media.Play(); }
    }
    void SeekToChart(MouseEventArgs e)
    {
        var sec = Math.Clamp(e.GetPosition(Chart).X / Math.Max(1, Chart.ActualWidth), 0, 1) * _duration;
        DragTo(sec);
        Seek.Value = sec;
    }

    void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dragging && _duration > 0) DragTo(Seek.Value);
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_duration <= 0) return;
        CommitSeek();
        _playing = !_playing;
        if (_playing)
        {
            if (Media.Position.TotalSeconds >= _duration - 0.05) Media.Position = TimeSpan.FromSeconds(Math.Max(0, _s!.Info.VideoTime(0)));
            Media.Play();
        }
        else Media.Pause();
        PlayBtn.Content = _playing ? PauseGlyph : PlayGlyph;
    }

    void Step(double sec)
    {
        if (_duration <= 0) return;
        if (_playing) { _playing = false; Media.Pause(); PlayBtn.Content = PlayGlyph; }
        _dragPos = Math.Clamp(CurPos + sec, 0, _duration);
        _seekPending = false; _seekTimer.Stop();
        _holdUntil = Environment.TickCount64 + 250;
        Media.Position = TimeSpan.FromSeconds(_dragPos);
    }
    void OnStepBack(object sender, RoutedEventArgs e) => Step(-FrameSec);
    void OnStepForward(object sender, RoutedEventArgs e) => Step(FrameSec);

    static readonly double[] Speeds = [0.25, 0.5, 1, 1.5, 2];
    static string SpeedLabel(double s) => s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";

    void BuildSpeedMenu()
    {
        SpeedMenu.Children.Clear();
        foreach (var s in Speeds)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var check = new TextBlock { Text = s == _speed ? "" : "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12, Foreground = FgB, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = SpeedLabel(s), FontSize = 13, Foreground = FgB, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            Grid.SetColumn(label, 1);
            row.Children.Add(check); row.Children.Add(label);
            var btn = new Button { Content = row, Tag = s, Style = (Style)FindResource("PlayerButton"), MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0), Padding = new Thickness(6, 0, 6, 0) };
            btn.Click += (_, _) => { SetSpeed(s); SpeedPop.IsOpen = false; };
            SpeedMenu.Children.Add(btn);
        }
    }

    void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        BuildSpeedMenu();
        SpeedPop.IsOpen = !SpeedPop.IsOpen;
    }

    void SetSpeed(double speed)
    {
        _speed = speed;
        Media.SpeedRatio = speed;
        SpeedBtn.Content = SpeedLabel(speed);
    }

    static string Clock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Running accuracy per second from the .perf, else per kill.</summary>
    void BuildChart()
    {
        if (_s?.Perf is { Buckets.Count: > 0 } perf && _duration > 0)
        {
            Chart.Set(PerfSeries.Build(perf).Select(p => new ChartPoint(_s.Info.VideoTime(p.TSec), p.Accuracy, p.Score, p.Kills, p.Shots, p.Hits, p.DamageEff)).ToList(), _duration, KillVideoTimes());
            ShowMetrics();
            return;
        }
        if (_s?.Stats is not { KillEvents.Count: > 0 } st || _duration <= 0) { Chart.Set([], 0); ShowMetrics(); return; }
        int shots = 0, hits = 0;
        var pts = new List<ChartPoint>();
        for (var k = 0; k < st.KillEvents.Count; k++)
        {
            var ev = st.KillEvents[k];
            shots += ev.Shots; hits += ev.Hits;
            pts.Add(new ChartPoint(_s.Info.VideoTime(_killTimes[k]), shots == 0 ? 0 : (double)hits / shots, Kills: k + 1, Shots: shots, Hits: hits));
        }
        Chart.Set(pts, _duration, KillVideoTimes());
        ShowMetrics();
    }

    double[] KillVideoTimes() => _killTimes.Select(k => _s!.Info.VideoTime(k)).ToArray();

    /// <summary>One chip per series that has data; applies the saved selection to the chart.</summary>
    void ShowMetrics()
    {
        _metrics.Clear();
        foreach (var series in ChartSeriesInfo.All)
            if (Chart.Has(series)) _metrics.Add(new MetricRow(series, _visible.Contains(series)));
        Chart.SetVisible(_visible);
        UpdatePrev();
    }

    (string Folder, PerfData Perf)? _prev;

    /// <summary>The newest earlier local run of the scenario that has a .perf, parsed once per folder.</summary>
    (string Folder, PerfData Perf, SessionInfo Info)? FindPrev()
    {
        if (_s is null) return null;
        foreach (var it in _items.Where(x => x.Info.Start < _s.Info.Start && x.Info.Scenario.Equals(_s.Info.Scenario, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Info.Start))
        {
            if (_prev is { } c && c.Folder == it.Folder) return (c.Folder, c.Perf, it.Info);
            if (!Directory.Exists(it.Folder) || Directory.GetFiles(it.Folder, SessionFolder.PerfPattern).FirstOrDefault() is not { } path) continue;
            try
            {
                var perf = PerfParser.Parse(File.ReadAllBytes(path));
                if (perf.Buckets.Count == 0) continue;
                _prev = (it.Folder, perf);
                return (it.Folder, perf, it.Info);
            }
            catch (Exception e) when (e is IOException or PerfFormatException) { }
        }
        return null;
    }

    /// <summary>Shows the chip when an earlier recorded run exists; draws that run dashed on the same run-time axis while the chip is on.</summary>
    void UpdatePrev()
    {
        var prev = Chart.Has(ChartSeries.Accuracy) ? FindPrev() : null;
        PrevChip.Visibility = prev is null ? Visibility.Collapsed : Visibility.Visible;
        PrevChip.Opacity = _host.Ui.ComparePrev ? 1 : 0.5;
        if (prev is not { } p || !_host.Ui.ComparePrev || _s is null) { Chart.SetGhost([]); return; }
        PrevChip.ToolTip = L.F("Previous play · {0}", L.Stamp(p.Info.Start));
        Chart.SetGhost(PerfSeries.Build(p.Perf).Select(x => new ChartPoint(_s.Info.VideoTime(x.TSec), x.Accuracy, x.Score, x.Kills, x.Shots, x.Hits, x.DamageEff)).ToList());
    }

    void OnPrevClick(object sender, MouseButtonEventArgs e)
    {
        _host.Ui.ComparePrev = !_host.Ui.ComparePrev;
        _host.SaveUi();
        UpdatePrev();
    }
}
