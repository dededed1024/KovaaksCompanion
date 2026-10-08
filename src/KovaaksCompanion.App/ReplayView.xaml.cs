using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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
public sealed record ScenarioData(RunLibrary Lib, IReadOnlyList<SessionInfo> Sessions, IReadOnlyDictionary<string, double> ServerBest, Func<string, List<ChartBand>?> TierBands);

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
    readonly ScoreChart _bigChart = new();
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
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
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

    void LayoutCard(Size size)
    {
        if (_fs is not null) return;
        Card.Width = Math.Max(0, Math.Min(1500, size.Width - 160));
        Card.Height = Math.Max(0, Math.Min(940, size.Height - 48 - 80));
        SideCol.Width = new GridLength(Math.Clamp(Card.Width * 0.28, 360, 500));
        RunChartRow.Height = new GridLength(Math.Clamp(Card.Height * 0.12, 80, 160));
        SideChart.Height = Math.Clamp(Card.Height * 0.17, 110, 180);
        _bigChart.Height = Math.Clamp(Card.Height * 0.4, 220, 460);
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
        _d = _d with { Lib = lib };
        BuildAll();
        if (_runMode && _selEnd is { } end)
        {
            _runIdx = NearestRun(end);
            ApplySelection();
            var si = SessionStore.FindRun(Infos(), _scenario, end);
            ShowRunStrip(si);
            if (si >= 0 && (_s is null || _s.Folder != _items[si].Folder)) LoadSession(_items[si]);
        }
        else if (!_runMode) BuildOverview();
    });

    // ---- sidebar -----------------------------------------------------------------------------------------------

    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : d.TotalDays < 30 ? $"{(int)d.TotalDays} d ago" : t.ToString("yyyy-MM-dd");
    }

    /// <summary>The highest tier a score reaches in the current scenario; null when it has no tiers or the score is below the first.</summary>
    ChartBand? TierOf(double score) =>
        _d?.TierBands(_scenario) is { Count: > 0 } bands ? bands.LastOrDefault(b => score >= b.Value) : null;

    /// <summary>A "Tier" tile in the tier's colour, or none.</summary>
    void AddTierTile(List<StatTile> tiles, double score)
    {
        if (TierOf(score) is { } t) tiles.Add(new("Tier", t.Label, Accent: ChartPaths.TextTone(t.Brush)));
    }

    double ServerBest =>_d!.ServerBest.TryGetValue(_scenario, out var sv) ? sv : 0;

    /// <summary>The local best unless the server holds a different score (as the stats page does).</summary>
    double BestScore => KovaaksApi.Reconcile(_stats.Plays > 0 ? _stats.Best : null, ServerBest) ?? 0;

    static string DayLabel(DateTime day) =>
        day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("ddd, MMM d", System.Globalization.CultureInfo.InvariantCulture);

    void SetScenario(string scenario)
    {
        _scenario = scenario;
        _stats = ScenarioStats.For(_d!.Lib, scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestIndexes());
    }

    /// <summary>Session mode: re-reads the session from the library (a live one grows), then the header, tiles and runs.</summary>
    void OnPlayScenario(object sender, RoutedEventArgs e)
    {
        if (_scenario.Length > 0) KovaaksCompanion.Core.KovaaksLaunch.Open(KovaaksCompanion.Core.KovaaksLaunch.Scenario(_scenario));
    }

    void BuildAll()
    {
        var d = _d!;
        SideChartCard.Visibility = Visibility.Visible;
        _stats = ScenarioStats.For(d.Lib, _scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestIndexes());
        Heading.Text = _scenario;
        Heading.ToolTip = _scenario;
        PlayScn.Visibility = _scenario.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var played = _stats.Plays > 0;
        List<StatTile>? side = null;
        if (played)
        {
            side = [new("Best", BestScore.ToString("0.#"), Accent: TierOf(BestScore) is { } bt ? ChartPaths.TextTone(bt.Brush) : null)];
            AddTierTile(side, BestScore);
            side.Add(new("Average", _stats.Average.ToString("0.#")));
            side.Add(new("Plays", _stats.Plays.ToString()));
        }
        else if (ServerBest > 0)
        {
            side = [new("Server best", ServerBest.ToString("0.#"), Accent: TierOf(ServerBest) is { } st ? ChartPaths.TextTone(st.Brush) : null)];
            AddTierTile(side, ServerBest);
        }
        SideTiles.ItemsSource = side;
        SideChart.Set(_stats.Runs.Select(r => (r.End, r.Score)).ToList(), "0.#");
        NoRuns.Visibility = played ? Visibility.Collapsed : Visibility.Visible;
        BuildRuns();
    }

    void BuildRuns()
    {
        RunsHost.Children.Clear();
        _rows.Clear();
        var order = Enumerable.Range(0, _stats.Runs.Count).Reverse().ToList();
        foreach (var i in order.Take(_limit)) RunsHost.Children.Add(RunRow(_stats.Runs[i], _pbs.Contains(i)));
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
        Put(T(r.End.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " " + ClockFormat.Clock(r.End), DimB), 0);
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
            pill.Children.Add(new TextBlock { Text = "PB", Foreground = green, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 1, 6, 1) });
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

    void SetMode(bool run)
    {
        if (!run) ExitFullscreen();
        _runMode = run;
        OverviewScroll.Visibility = run ? Visibility.Collapsed : Visibility.Visible;
        RunPanel.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
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
        var tiles = new List<StatTile>
        {
            new("Best", BestScore.ToString("0.#"), Accent: TierOf(BestScore) is { } bt ? ChartPaths.TextTone(bt.Brush) : null),
        };
        AddTierTile(tiles, BestScore);
        tiles.Add(new("Average", s.Average.ToString("0.#")));
        tiles.Add(new("Plays", s.Plays.ToString()));
        tiles.Add(new("Last played", Ago(s.LastPlayed!.Value)));
        var recent = s.Runs.Skip(Math.Max(0, s.Plays - 10)).ToList();
        var recentAvg = recent.Average(r => r.Score);
        tiles.Add(new($"Last {recent.Count} avg", recentAvg.ToString("0.#")));
        if (s.Plays > recent.Count && s.Average > 0)
        {
            var vs = (recentAvg - s.Average) / s.Average;
            tiles.Add(new("vs overall avg", vs.ToString("+0.0%;-0.0%;0%"), StatTile.ToneOf(vs)));
        }
        tiles.Add(new("Accuracy", s.AverageAccuracy.ToString("P1")));
        tiles.Add(new("Time", Dur(s.TimePlayed)));
        if (s.Trend() is { } t) tiles.Add(new("Trend", t.ToString("+0.0%;-0.0%;0%"), StatTile.ToneOf(t)));
        if (s.AverageDrift is { } dr) tiles.Add(new("Drift", (dr * 100).ToString("+0.0;-0.0;0") + " pts", StatTile.ToneOf(dr)));
        OverviewHost.Children.Add(new ItemsControl { Style = (Style)FindResource("StatTiles"), ItemsSource = tiles, Margin = new Thickness(0, 0, 0, 4) });

        var sv = ServerBest;
        if (sv > 0 && KovaaksApi.Reconcile(s.Best, sv) is { } m && Math.Abs(m - s.Best) > 0.005)
            OverviewHost.Children.Add(new TextBlock { Text = $"Server best {sv:0.#} · local best {s.Best:0.#}", Foreground = DimB, FontSize = 12.5, Margin = new Thickness(2, 0, 0, 8) });

        if (bands is { Count: > 0 }) OverviewHost.Children.Add(TierCard(bands, BestScore));

        var chartBody = new StackPanel();
        chartBody.Children.Add(new TextBlock { Text = "Score per run", Style = (Style)FindResource("Caption"), FontSize = 12.5, Margin = new Thickness(4, 0, 0, 8) });
        if (_bigChart.Parent is Panel old) old.Children.Remove(_bigChart);
        chartBody.Children.Add(_bigChart);
        _bigChart.Set(s.Runs.Select(r => (r.End, r.Score)).ToList(), "0.#", ChartKind.Line, true, bands);
        OverviewHost.Children.Add(new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 4, 0, 0), Child = chartBody });
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
        pill.Children.Add(new TextBlock { Text = cur?.Label ?? "UNRANKED", Foreground = ChartPaths.TextTone(pillBrush), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 4, 12, 4) });
        head.Children.Add(pill);
        head.Children.Add(new TextBlock
        {
            Text = next is null ? "Top tier reached" : $"{next.Value - best:#,0.#} to {next.Label} ({next.Value:#,0.##})",
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
        sp.Children.Add(new TextBlock { Text = "No local runs yet", FontSize = 16, FontWeight = FontWeights.SemiBold });
        if (ServerBest > 0) sp.Children.Add(new TextBlock { Text = $"Server best {ServerBest:0.#}", Foreground = DimB, FontSize = 13, Margin = new Thickness(0, 4, 0, 0) });
        if (bands is { Count: > 0 })
        {
            sp.Children.Add(new TextBlock { Text = "TIER THRESHOLDS", Foreground = DimB, FontSize = 11, Margin = new Thickness(0, 16, 0, 8) });
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
        ShowRunStrip(si);
        ApplySelection();
        if (si < 0) { if (_host.IsPending(_scenario, end)) ShowEncoding(end); else NoRecording(); return; }
        var item = _items[si];
        if (_s is not null && _s.Folder == item.Folder && (_duration > 0 || Notice.Visibility == Visibility.Visible)) return;
        LoadSession(item);
    }

    void ShowRunStrip(int sessionIdx)
    {
        var info = sessionIdx >= 0 ? _items[sessionIdx].Info : null;
        var run = _runIdx >= 0 ? _stats.Runs[_runIdx] : null;
        var tiles = new List<StatTile>();
        double? score = run?.Score ?? info?.Score;
        var acc = run?.Accuracy ?? info?.Accuracy;
        var kills = run?.Kills ?? info?.Kills;
        if (score is { } sc)
        {
            tiles.Add(new("Score", sc.ToString("0.##"), Accent: TierOf(sc) is { } rt ? ChartPaths.TextTone(rt.Brush) : null));
            AddTierTile(tiles, sc);
        }
        if (acc is { } a) tiles.Add(new("Accuracy", a.ToString("P1")));
        if (kills is { } k) tiles.Add(new("Kills", k.ToString()));
        if (run is not null)
        {
            tiles.Add(new("Rank", $"#{_stats.Runs.Count(r => r.Score > run.Score) + 1}/{_stats.Runs.Count}"));
            var vs = (run.Score - _stats.Average) / Math.Max(1e-9, _stats.Average);
            tiles.Add(new("vs average", vs.ToString("+0.0%;-0.0%;0%"), StatTile.ToneOf(vs)));
        }
        RunTiles.ItemsSource = tiles;
        PbBadge.Visibility = _runIdx >= 0 && _pbs.Contains(_runIdx) ? Visibility.Visible : Visibility.Collapsed;
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
        Notice.Text = "No recording for this run";
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
        _metrics.Clear();
        RunNote.Visibility = Visibility.Collapsed;
        TimeText.Text = "";
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
        var notes = new List<string>();
        if (_s.Info.Partial) notes.Add("Partial: mouse or video does not cover the whole run");
        if (!_s.Info.DegreesAvailable) notes.Add("Unknown sens scale: no trail");
        RunNote.Text = string.Join(" · ", notes);
        RunNote.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
            Notice.Text = "No video for this session";
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

    void OnFsClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

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
        Card.Background = Brushes.Black; Card.Effect = null; Clipper.Clip = null;
        ((Grid)Clipper.Child).Margin = new Thickness(0);
        SideCol.Width = new GridLength(0); GapCol.Width = new GridLength(0); Side.Visibility = Visibility.Collapsed;
        RunStrip.Visibility = Visibility.Collapsed;
        Stage.Margin = new Thickness(0); Screen.CornerRadius = new CornerRadius(0);
        Grid.SetRow(Stage, 0); Grid.SetRowSpan(Stage, 5);
        BarScrim.Visibility = Visibility.Visible;
        ChartCard.Margin = new Thickness(24, 0, 24, 0); Seek.Margin = new Thickness(35, 8, 35, 0); TransportBar.Margin = new Thickness(24, 8, 24, 16);
        FsBtn.Content = "\uE73F"; FsBtn.ToolTip = "Exit fullscreen (F)";
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
        FsBtn.Content = "\uE740"; FsBtn.ToolTip = "Fullscreen (F)";
        ChartCard.Margin = new Thickness(0, 12, 0, 0); Seek.Margin = new Thickness(11, 8, 11, 0); TransportBar.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(Stage, 1); Grid.SetRowSpan(Stage, 1);
        Stage.Margin = f.StageMargin; Screen.CornerRadius = new CornerRadius(14);
        RunStrip.Visibility = Visibility.Visible; Side.Visibility = Visibility.Visible; GapCol.Width = new GridLength(16);
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
        SetTransport(false);
        Notice.Text = "Video failed to load: " + e.ErrorException.Message;
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
        TimeText.Text = $"{Math.Max(0, t):0.00} / {_runLength:0.00} s";
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
    }
}
