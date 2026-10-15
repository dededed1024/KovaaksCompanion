﻿using System.Collections.ObjectModel;
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
using KovaaksCompanion.Core.Replays;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

/// <summary>What the popup reads: local runs, saved replays, server bests and tier thresholds.</summary>
public sealed record ScenarioData(ScoreBook Book, IReadOnlyList<ReplayInfo> Replays, Func<string, List<ChartBand>?> TierBands, Func<string, Task<bool>> LoadCloud)
{
    public RunLibrary Lib => Book.Lib;
}

public sealed record ReplayItem(ReplayInfo Info);

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

    readonly AppHost _host;
    readonly List<ReplayItem> _items = [];
    readonly ObservableCollection<MetricRow> _metrics = [];
    readonly HashSet<ChartSeries> _visible = [];
    readonly Dictionary<DateTime, Border> _rows = [];
    readonly DispatcherTimer _seekTimer;
    readonly ScoreChart _bigChart = new() { Cursor = Cursors.Hand, Zoomable = true };
    ScenarioData? _d;
    ScenarioHistory _stats = new("", []);
    HashSet<int> _pbs = [];
    string _scenario = "";
    DateTime? _selEnd;
    int _runIdx = -1;
    bool _open, _runMode;
    int _tok, _limit = Page;
    LoadedReplay? _replay;
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
        RefreshItems();
        SetTransport(false);
        TrailBtn.IsChecked = host.Ui.ShowTrail;
        Overlay.Visibility = host.Ui.ShowTrail ? Visibility.Visible : Visibility.Collapsed;
        _seekTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = SeekDelay };
        _seekTimer.Tick += (_, _) => CommitSeek();
        _fsTimer.Tick += (_, _) => HideBar();
        PreviewMouseMove += OnFsMouse;
        host.ReplaySaved += OnReplaySaved;
        CompositionTarget.Rendering += OnRendering;
        Media.MediaOpened += OnMediaOpened;
        Media.MediaEnded += OnMediaEnded;
        Media.MediaFailed += OnMediaFailed;
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
        _host.ReplaySaved -= OnReplaySaved;
        CompositionTarget.Rendering -= OnRendering;
        _seekTimer.Stop(); _fsTimer.Stop();
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
    const double CardAspect = 1.7;

    /// <summary>The run list's share of the card width.</summary>
    const double SideShare = 0.28;

    /// <summary>Narrowest the run list gets, so a row's date and score never clip; below <see cref="CompactSide"/> the rows drop their tier column.</summary>
    const double MinSide = 236, CompactSide = 300;
    bool _compactRows;

    /// <summary>Aspect of the last opened video, kept while the next one loads so the card does not jump.</summary>
    double _videoAspect;

    /// <summary>Width of the stage (and so of the chart below it) for a card of width <paramref name="w"/>.</summary>
    static double StageWidth(double w) => Math.Max(0, w - 2 - 32 - 12 - Math.Max(w * SideShare, MinSide));

    void LayoutCard(Size size)
    {
        if (_fs is not null || size.Width < 1 || size.Height < 1) return; // not laid out yet: SizeChanged calls again
        if (Media.NaturalVideoHeight > 0) _videoAspect = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        // The overview and runs without a video get the height of a 16:9 video.
        var aspect = _runMode && !NoVideo && _videoAspect > 0 ? _videoAspect : 16.0 / 9;
        // The video fills the stage width; the card is as tall as its content, so only a too tall card narrows.
        var w = Math.Max(0, Math.Min(1500, size.Width - 160));
        var limit = Math.Max(0, size.Height - 80);
        for (var i = 0; i < 6; i++)
        {
            ApplyWidth(w);
            var sw = StageWidth(w);
            Stage.Height = sw / aspect;
            // Header and run panel are measured on their own: the overview and the run list beside the stage scroll, so their full height must not size the card.
            var vis = RunPanel.Visibility;
            RunPanel.Visibility = Visibility.Hidden; // a collapsed panel measures 0
            Header.Measure(new Size(sw, double.PositiveInfinity));
            RunPanel.Measure(new Size(sw, double.PositiveInfinity));
            var h = Header.DesiredSize.Height + RunPanel.DesiredSize.Height + 2 + 32;
            RunPanel.Visibility = vis;
            Card.Height = h;
            var over = h + 48 - limit;
            if (over <= 0.5 || w <= 400) break;
            w = Math.Max(400, w - over * aspect / (1 - SideShare));
        }
    }

    void ApplyWidth(double w)
    {
        Card.Width = w;
        var nominal = w / CardAspect;
        SideCol.Width = new GridLength(Math.Max(Card.Width * SideShare, MinSide));
        var compact = SideCol.Width.Value < CompactSide;
        if (compact != _compactRows)
        {
            _compactRows = compact;
            if (IsLoaded && _rows.Count > 0) BuildRuns(); // rows drop or regain the tier column
        }
        RunChartRow.Height = new GridLength(Math.Clamp(nominal * 0.16, 90, 220));
        _bigChart.Height = Math.Clamp(nominal * 0.4, 120, 460);
    }

    void OnHeaderSized(object sender, SizeChangedEventArgs e) => FitHeader();

    /// <summary>The name is cut with an ellipsis before it reaches the Play button next to it, which sits left of the tool buttons.</summary>
    void FitHeader()
    {
        if (Header.ActualWidth <= 0) return;
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        HeaderTools.Measure(inf); PlayScn.Measure(inf); ScnStar.Measure(inf);
        var play = PlayScn.Visibility == Visibility.Visible ? PlayScn.DesiredSize.Width + PlayScn.Margin.Left : 0;
        Heading.MaxWidth = Math.Max(60, Header.ActualWidth - HeaderTools.DesiredSize.Width - ScnStar.DesiredSize.Width - play - 12);
    }

    void OnCardSized(object sender, SizeChangedEventArgs e) =>
        Clipper.Clip = _fs is null ? new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17) : null;

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>Opens (or replaces the content of) the popup on the scenario overview. <paramref name="origin"/> is in this control's coordinates.</summary>
    public void Show(ScenarioData data, string scenario, Point? origin) { RefreshItems(); Open(data, scenario, null, origin); }

    /// <summary>Opens the popup with the run that ended at <paramref name="end"/> selected; false when it has no recording, unless <paramref name="withoutRecording"/> selects it anyway.</summary>
    public bool ShowRun(ScenarioData data, string scenario, DateTime end, bool withoutRecording = false)
    {
        RefreshItems();
        if (!withoutRecording && ReplayStore.FindRun(Infos(), scenario, end) < 0 && !_host.IsPending(scenario, end)) return false;
        Open(data, scenario, end, null);
        return true;
    }

    /// <summary>Opens the most recent recorded run; false when nothing was recorded yet.</summary>
    public bool ShowLatest(ScenarioData data)
    {
        RefreshItems();
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
            LayoutCard(new Size(ActualWidth, ActualHeight));
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
        if (!_open || !_runMode || _duration <= 0 || NoVideo || e.OriginalSource is TextBox) return false;
        var big = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.T: SetTrail(TrailBtn.IsChecked != true); break;
            case Key.Left: Step(big ? -1.0 : -FrameSec); break;
            case Key.Right: Step(big ? 1.0 : FrameSec); break;
            default: return false;
        }
        e.Handled = true;
        return true;
    }

    // ---- sessions ----------------------------------------------------------------------------------------------

    List<ReplayInfo> Infos() => _items.Select(x => x.Info).ToList();

    /// <summary>Reads the replays from the archive again (a video was deleted, replays were imported or saved).</summary>
    void RefreshItems()
    {
        _items.Clear();
        _items.AddRange(_host.Replays.List().Select(i => new ReplayItem(i)));
    }

    void OnReplaySaved(ReplayInfo info) => Dispatcher.BeginInvoke(async () =>
    {
        var i = 0;
        while (i < _items.Count && _items[i].Info.Start > info.Start) i++;
        _items.Insert(i, new ReplayItem(info));
        if (!_open || _d is null) return;
        var tok = _tok;
        var dpiFixes = new Dictionary<string, int>(_host.Ui.DpiFixes);
        var lib = await Task.Run(() => RunLibrary.Load(_host.Settings.OpenStore(), dpiFixes).Library);
        if (!_open || tok != _tok || _d is null) return;
        _d = _d with { Book = _d.Book.WithLibrary(lib) };
        BuildAll();
        if (_runMode && _selEnd is { } end)
        {
            _runIdx = NearestRun(end);
            ApplySelection();
            var si = ReplayStore.FindRun(Infos(), _scenario, end);
            if (si >= 0 && (_replay is null || _replay.Info.Id != _items[si].Info.Id || _replay.Info.HasVideo != _items[si].Info.HasVideo)) LoadReplay(_items[si]);
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

    /// <summary>The best of the local runs, the cloud scores and the server's all-time best (as the stats page does).</summary>
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
        if (_scenario.Length == 0) { ScnStar.Visibility = Visibility.Collapsed; RunStar.Visibility = Visibility.Collapsed; return; }
        void Style(Button b, bool fav, string kind)
        {
            GlyphButton.Set(b, ((char)(fav ? 0xE735 : 0xE734)).ToString(), (Brush)FindResource(fav ? "Yellow" : "Dim"));
            b.ToolTip = L.T((fav ? "Unfavorite " : "Favorite ") + kind);
            b.Visibility = Visibility.Visible;
        }
        Style(ScnStar, _host.Ui.IsFavoriteScenario(_scenario), "scenario");
        if (OpenRun() is { } r) Style(RunStar, _host.Ui.IsFavoriteRun(r.Scenario, r.End), "replay");
        else RunStar.Visibility = Visibility.Collapsed;
        FitHeader();
    }

    /// <summary>The run open in the stage, or null in the overview.</summary>
    (string Scenario, DateTime End)? OpenRun() =>
        _runMode && _runIdx >= 0 && _runIdx < _stats.Runs.Count ? (_stats.Runs[_runIdx].Scenario, _stats.Runs[_runIdx].End) : null;

    void OnScenarioStar(object sender, RoutedEventArgs e)
    {
        if (_scenario.Length > 0) ToggleStar(() => _host.Ui.ToggleFavoriteScenario(_scenario));
    }

    void OnRunStar(object sender, RoutedEventArgs e)
    {
        if (OpenRun() is { } r) ToggleStar(() => _host.Ui.ToggleFavoriteRun(r.Scenario, r.End));
    }

    void ToggleStar(Action toggle)
    {
        toggle();
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
        var replay = ReplayStore.HasVideo(Infos(), r.Scenario, r.End);
        var g = new Grid { Margin = new Thickness(4, 0, 4, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = _compactRows ? new GridLength(0) : new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        void Put(UIElement e, int col)
        {
            if (e is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.Margin = new Thickness(4, 0, 4, 0); }
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }
        TextBlock T(string text, Brush? fg = null, bool bold = false) => new() { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = fg ?? FgB, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        Put(T(L.MonthDay(r.End) + " " + L.Time(r.End), DimB), 0);
        if (!_compactRows && _d?.TierBands(_scenario) is { Count: > 0 } bands)
        {
            var t = bands.LastOrDefault(b => r.Score >= b.Value);
            var tierText = t != null ? T(t.Label, ChartPaths.TextTone(t.Brush)) : T(L.T("UNRANKED"), DimB);
            tierText.HorizontalAlignment = HorizontalAlignment.Center;
            Put(tierText, 1);
        }
        var scoreText = T(r.Score.ToString("0.#"), TierOf(r.Score) is { } rt ? ChartPaths.TextTone(rt.Brush) : null, true);
        scoreText.HorizontalAlignment = HorizontalAlignment.Right;
        if (pb)
        {
            // a PB is marked by a green wash behind its score, so no extra column is reserved for the other rows
            var wash = new Grid { HorizontalAlignment = HorizontalAlignment.Right };
            wash.Children.Add(new Border { CornerRadius = new CornerRadius(7), Background = (Brush)FindResource("Green"), Opacity = 0.18 });
            scoreText.Margin = new Thickness(7, 2, 7, 2);
            wash.Children.Add(scoreText);
            Put(wash, 2);
        }
        else Put(scoreText, 2);
        if (replay)
        {
            var play = T("", (Brush)FindResource("Accent"));
            play.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            play.FontSize = 15;
            play.HorizontalAlignment = HorizontalAlignment.Center;
            Put(play, 3);
        }
        else if (_host.IsPending(r.Scenario, r.End))
        {
            var ring = ProgressRing.Create(_host, r.Scenario, r.End, (Brush)FindResource("Accent"), 16);
            ring.HorizontalAlignment = HorizontalAlignment.Center;
            Put(ring, 3);
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
        PaintOverviewRow();
    }

    void PaintOverviewRow() =>
        OverviewRow.Background = !_runMode ? Solid("#330A84FF") : OverviewRow.IsMouseOver ? Solid("#0DFFFFFF") : Brushes.Transparent;

    void OnOverviewRowHover(object sender, MouseEventArgs e) => PaintOverviewRow();

    void OnOverviewRow(object sender, MouseButtonEventArgs e) => ShowOverview();

    int NearestRun(DateTime end)
    {
        var best = -1;
        for (var i = 0; i < _stats.Runs.Count; i++)
        {
            var gap = (_stats.Runs[i].End - end).Duration();
            if (gap < ReplayStore.RunMatchTolerance && (best < 0 || gap < (_stats.Runs[best].End - end).Duration())) best = i;
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
        UpdateRunButtons();
        DisarmDelete();
        ShowScenarioStar();
        PaintOverviewRow();
        LayoutCard(new Size(ActualWidth, ActualHeight));
    }

    /// <summary>The video folder and delete buttons exist only while the open run has a video file.</summary>
    void UpdateRunButtons()
    {
        var shown = _runMode && _replay?.VideoPath != null ? Visibility.Visible : Visibility.Collapsed;
        FolderBtn.Visibility = shown;
        RunDelBtn.Visibility = shown;
    }

    bool _delArmed;

    void DisarmDelete()
    {
        _delArmed = false;
        RunDelBtn.Foreground = DimB;
        RunDelBtn.ToolTip = L.T("Delete Replay");
    }

    /// <summary>Trash button of the open run: the first click arms it (red), the second deletes the run and closes the popup.</summary>
    async void OnDeleteRun(object sender, RoutedEventArgs e)
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
        var scenario = r.Scenario;
        if (_replay is null) return;
        var info = _replay.Info;
        UnloadMedia(); // releases the video file so it can be deleted
        DisarmDelete();
        if (!await _host.DeleteRun(info))
        {
            if (_open && _d is not null) SelectRun(r.End);
            return;
        }
        if (!_open || _d is null) return;
        RefreshItems();
        var dpiFixes = new Dictionary<string, int>(_host.Ui.DpiFixes);
        var lib = await Task.Run(() => RunLibrary.Load(_host.Settings.OpenStore(), dpiFixes).Library);
        if (!_open || _d is null) return;
        _d = _d with { Book = _d.Book.WithLibrary(lib) };
        _scenario = scenario;
        BuildAll();
        ShowOverview();
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
        var tilesRow = SummaryTiles(s, bands);
        ((FrameworkElement)tilesRow).Margin = new Thickness(0, 0, -8, 4);
        OverviewHost.Children.Add(tilesRow);

        var chartBody = new StackPanel();
        chartBody.Children.Add(new TextBlock { Text = L.T("Score per run"), Foreground = FgB, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(4, 0, 0, 10) });
        _bigChart.PointClicked -= OnBigChartClicked;
        _bigChart.PointClicked += OnBigChartClicked;
        if (_bigChart.Parent is Panel old) old.Children.Remove(_bigChart);
        chartBody.Children.Add(_bigChart);
        var points = s.Records.Select(r => (r.Time, r.Score)).ToList();
        _bigChart.Set(points, "0.#", ChartKind.Line, true, bands);
        OverviewHost.Children.Add(new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(0, 4, 0, 0), Child = chartBody });

        if (SensitivityCard(s) is { } sens) OverviewHost.Children.Add(sens);
        _speedHost = new Border();
        OverviewHost.Children.Add(_speedHost);
        _ = LoadSpeedAsync(_scenario, _speedHost);
        _ = RefreshCloudAsync(_scenario);
    }

    /// <summary>A card with an optional title; <paramref name="note"/> sits dimmed at the title's right end.</summary>
    Border Section(string? title, UIElement body, Thickness margin, string? note = null)
    {
        var sp = new StackPanel();
        if (title is not null)
        {
            var head = new Grid { Margin = new Thickness(4, 0, 4, 10) };
            head.Children.Add(new TextBlock { Text = L.T(title), Foreground = FgB, FontSize = 15, FontWeight = FontWeights.SemiBold });
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
        if (heads.Any(h => h.Length > 0)) for (var c = 0; c < heads.Count; c++) Put(0, c + 1, heads[c], DimB, 11.5, false, true);
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

    /// <summary>A thin bar filled to <paramref name="frac"/> of its width; an optional tick marks a second value.</summary>
    static UIElement MeterBar(double frac, Brush fill, double height = 5, double? tick = null, Brush? tickBrush = null)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Solid("#14FFFFFF") });
        var inner = new Grid();
        frac = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 1e-6), GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 1e-6), GridUnitType.Star) });
        inner.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = fill });
        g.Children.Add(inner);
        if (tick is { } t)
        {
            t = double.IsNaN(t) ? 0 : Math.Clamp(t, 0, 1);
            var m = new Grid();
            m.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(t, 1e-6), GridUnitType.Star) });
            m.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - t, 1e-6), GridUnitType.Star) });
            m.Children.Add(new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Right, Background = tickBrush ?? Brushes.White, Margin = new Thickness(0, -2, -1, -2) });
            g.Children.Add(m);
        }
        return g;
    }

    /// <summary>The colour with less saturation and more lightness, same hue.</summary>
    static Color Washed(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        var l = (max + min) / 2;
        var sat = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        var h = d == 0 ? 0 : max == r ? ((g - b) / d % 6 + 6) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        sat = Math.Min(1, sat * 0.9); l = 0.86;
        var chroma = (1 - Math.Abs(2 * l - 1)) * sat;
        var x = chroma * (1 - Math.Abs(h % 2 - 1));
        var m = l - chroma / 2;
        var (r1, g1, b1) = (int)h switch { 0 => (chroma, x, 0.0), 1 => (x, chroma, 0.0), 2 => (0.0, chroma, x), 3 => (0.0, x, chroma), 4 => (x, 0.0, chroma), _ => (chroma, 0.0, x) };
        byte B(double v) => (byte)Math.Round(Math.Clamp(v + m, 0, 1) * 255);
        return Color.FromRgb(B(r1), B(g1), B(b1));
    }

    /// <summary>The best bar with the average bar drawn over it, so the part past the average shows how far the best reaches. When both are the same only the best bar is drawn.</summary>
    static UIElement OverlapBar(double avg, double best, Brush avgFill, Brush bestFill, double height = 6)
    {
        var g = new Grid { Height = height };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = Solid("#14FFFFFF") });
        UIElement Fill(double frac, Brush b)
        {
            frac = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 1e-6), GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 1e-6), GridUnitType.Star) });
            inner.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), Background = b });
            return inner;
        }
        g.Children.Add(Fill(best, bestFill));
        if (Math.Abs(avg - best) > 1e-6) g.Children.Add(Fill(avg, avgFill));
        return g;
    }

    /// <summary>Wraps a grid of cards: as many columns (up to <paramref name="max"/>) as fit at <paramref name="minCell"/> px each. The cards carry an 8 px right and bottom margin; the grid's own right margin takes the last one back.</summary>
    static void Responsive(UniformGrid grid, int max, double minCell)
    {
        grid.Margin = new Thickness(0, 0, -8, 0);
        grid.SizeChanged += (_, e) =>
        {
            var cols = Math.Clamp((int)(e.NewSize.Width / minCell), 1, Math.Max(1, max));
            if (grid.Columns != cols) grid.Columns = cols;
        };
    }

    /// <summary>Plays, best, accuracy, time played and tier as one row of cards; the best is the score the stats page shows (local runs, cloud scores and the server's all-time best). The tier card shows the progress to the next tier in percent, or COMPLETE at the top tier.</summary>
    UIElement SummaryTiles(ScenarioHistory s, List<ChartBand>? bands)
    {
        var sum = ScenarioInsights.Summarize(s.Records);
        const string Dash = "–";
        TextBlock Big(string text, Brush? tone = null) =>
            new() { Text = text, Foreground = tone ?? FgB, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        var tiles = new List<(string Label, UIElement Content)>
        {
            (L.T("Runs"), Big(sum?.Plays.ToString() ?? Dash)),
            (L.T("Best"), Big(BestScore > 0 ? BestScore.ToString("0.#") : Dash, TierOf(BestScore) is { } t ? ChartPaths.TextTone(t.Brush) : null)),
            (L.T("Accuracy"), Big(sum?.Accuracy is { } a ? a.ToString("P1") : Dash)),
            (L.T("Time"), Big(sum is { TimePlayed.Ticks: > 0 } ? Dur(sum.TimePlayed) : Dash)),
        };
        if (bands is { Count: > 0 })
        {
            var cur = bands.LastOrDefault(b => b.Value <= BestScore);
            var next = bands.FirstOrDefault(b => b.Value > BestScore);
            var lo = cur?.Value ?? 0;
            var frac = next is null ? 1 : Math.Clamp((BestScore - lo) / Math.Max(1e-9, next.Value - lo), 0, 1);
            tiles.Add((cur is null ? L.T("Tier") : L.T("Tier") + " · " + cur.Label,
                Big(next is null ? L.T("COMPLETE") : frac.ToString("P0"), ChartPaths.TextTone(next?.Brush ?? cur!.Brush))));
        }
        var grid = new UniformGrid { Columns = tiles.Count };
        Responsive(grid, tiles.Count, 124);
        for (var i = 0; i < tiles.Count; i++)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = tiles[i].Label, Foreground = DimB, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(tiles[i].Content);
            grid.Children.Add(new Border
            {
                Style = (Style)FindResource("SheetCard"), Padding = new Thickness(14, 10, 14, 12), Child = body,
                Margin = new Thickness(0, 0, 8, 8),
            });
        }
        return grid;
    }

    /// <summary>Score by the sensitivity (cm per 360) the local runs were played at: a row per sensitivity, and every run as a dot when more than one was used.</summary>
    UIElement? SensitivityCard(ScenarioHistory s)
    {
        var groups = ScenarioInsights.BySensitivity(s.Runs);
        if (groups.Count == 0) return null;
        // The bars zoom into the scenario's own score range: the axis starts where the lowest average fills 5% of the bar and ends at the best score.
        var hi = groups.Max(g => g.Best);
        var lo0 = groups.Min(g => g.Average);
        var floor = hi - lo0 < hi * 0.01 ? 0 : Math.Max(0, (lo0 - 0.05 * hi) / 0.95);
        double Frac(double v) => (v - floor) / Math.Max(1e-9, hi - floor);
        var body = new StackPanel();
        List<RunRecord> RunsOf(SensGroup g) => s.Runs.Where(r => r.Cm360 is > 0 && Math.Abs(Math.Round(r.Cm360.Value, 2) - g.Cm360) < 1e-6).ToList();
        // Only the listed sensitivities are plotted, each in its own colour that the row below repeats.
        var dots = s.Runs.Where(r => r.Cm360 is > 0 && groups.Any(g => Math.Abs(g.Cm360 - Math.Round(r.Cm360!.Value, 2)) < 1e-6)).ToList();
        var chart = new ScatterChart { Height = 170, Margin = new Thickness(0, 0, 0, 8) };
        chart.Set(dots.Select(r => (r.Cm360!.Value, r.Score, $"{r.Cm360!.Value:0.##} cm/360 · {L.Stamp(r.End)}")).ToList(), "0.## \"cm\"", "0.#",
            dots.Select(r => groups.FindIndex(g => Math.Abs(g.Cm360 - Math.Round(r.Cm360!.Value, 2)) < 1e-6)).ToList());
        chart.GroupNote = gi => gi < groups.Count && RunsOf(groups[gi]) is { Count: > 0 } rs ? $"DPI {rs[0].EffectiveDpi} · " + L.T("Click to edit") : "";
        chart.GroupClick += gi => { if (gi < groups.Count) ShowDpiEditor(chart, RunsOf(groups[gi])); };
        body.Children.Add(chart);
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var tone = ScatterChart.GroupColors[gi % ScatterChart.GroupColors.Length];
            var full = new SolidColorBrush(tone);
            var washed = new SolidColorBrush(Washed(tone));
            var head = new Grid { Margin = new Thickness(4, 8, 4, 5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            head.MouseLeftButtonUp += (_, _) => ShowDpiEditor(head, RunsOf(g));
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Fill = full });
            label.Children.Add(new TextBlock { Text = $"{g.Cm360:0.##} cm/360", Foreground = DimB, FontSize = 13 });
            head.Children.Add(label);
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            right.Children.Add(new TextBlock { Text = L.F("{0} runs", g.Runs), Foreground = DimB, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) });
            right.Children.Add(new TextBlock { Text = g.Average.ToString("0.#"), Foreground = washed, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(new TextBlock { Text = " / " + g.Best.ToString("0.#"), Foreground = full, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(right);
            body.Children.Add(head);
            // The bar is the sensitivity's own colour (as in the scatter above); the average part is the same hue washed out and lighter.
            var same = string.Equals(g.Average.ToString("0.#"), g.Best.ToString("0.#"));
            var bar = OverlapBar(same ? Frac(g.Best) : Frac(g.Average), Frac(g.Best), same ? full : washed, full);
            bar.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 4, 2));
            body.Children.Add(bar);
        }
        return Section("Score by sensitivity", body, new Thickness(0, 12, 0, 0), L.T("Average") + " / " + L.T("Best"));
    }

    /// <summary>A small popup to correct the DPI of the runs played at one sensitivity (KovaaK's recorded a wrong one). Applies to every scenario's runs recorded with the same DPI and sensitivity.</summary>
    void ShowDpiEditor(FrameworkElement anchor, List<RunRecord> runs)
    {
        if (runs.Count == 0) return;
        var keys = runs.Select(r => r.DpiKey).OfType<string>().Distinct().ToList();
        var recorded = runs[0].Dpi;
        if (keys.Count == 0 || recorded <= 0) return;
        var box = new TextBox { Text = runs[0].EffectiveDpi.ToString(), Width = 80, FontSize = 13, MaxLength = 6, VerticalContentAlignment = VerticalAlignment.Center };
        var popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        void Apply(int dpi)
        {
            // Every DPI recorded at this sensitivity becomes the one entered; entering a run's own recorded DPI clears its correction.
            foreach (var r in runs.Where(r => r.DpiKey is not null).DistinctBy(r => r.DpiKey)) _host.Ui.SetDpiFix([r.DpiKey!], dpi, r.Dpi);
            _host.SaveUi();
            popup.IsOpen = false;
            if (_d is null) return;
            var lib = _d.Lib.WithDpiFixes(_host.Ui.DpiFixes);
            _d = _d with { Book = _d.Book.WithLibrary(lib) };
            _stats = _d.Book.For(_scenario);
            if (!_runMode) BuildOverview();
        }
        void Commit() { if (int.TryParse(box.Text.Trim(), out var v) && v is > 0 and <= 100000) Apply(v); }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } else if (e.Key == Key.Escape) { popup.IsOpen = false; e.Handled = true; } };
        var ok = new Button { Content = L.T("Apply"), Style = (Style)FindResource("Primary"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        ok.Click += (_, _) => Commit();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = "DPI", Foreground = DimB, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(box);
        row.Children.Add(ok);
        var panel = new StackPanel();
        panel.Children.Add(row);
        if (runs.Any(r => r.FixedDpi > 0))
        {
            var reset = new TextBlock { Text = L.F("Recorded: {0}", recorded), Foreground = DimB, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand };
            reset.MouseLeftButtonUp += (_, _) => Apply(0);
            panel.Children.Add(reset);
        }
        popup.Child = new Border { Background = (Brush)FindResource("Raised"), BorderBrush = (Brush)FindResource("Separator"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 8), Child = panel };
        popup.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        popup.IsOpen = true;
    }

    // ---- mouse speed -------------------------------------------------------------------------------------------

    readonly Dictionary<Guid, RunSpeed?> _speeds = [];
    Border? _speedHost;

    readonly record struct SpeedPlay(DateTime End, double Score, ReplayItem? Item, RunSpeed? Cached);

    /// <summary>Fills the mouse-speed card from every play of the scenario that has mouse data. Reading trajectories takes a moment, so the card appears once they are in.</summary>
    async Task LoadSpeedAsync(string scenario, Border host)
    {
        var items = _items.Where(x => x.Info.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)).ToList();
        var archived = await Task.Run(() => _host.Settings.OpenStore().ArchivedRuns().Where(r => r.Speed != null && r.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)).ToList());
        // Recorded plays (trajectory on disk) and deleted ones (speed kept in the archive).
        var plays = items.Select(x => new SpeedPlay(x.Info.End, x.Info.Score, x, null))
            .Concat(archived.Select(r => new SpeedPlay(r.End, r.Score, null, r.Speed))).ToList();
        var missing = plays.Where(x => x.Item != null && !_speeds.ContainsKey(x.Item.Info.Id)).Select(x => x.Item!).ToList();
        if (missing.Count > 0)
        {
            var cache = _host.Settings.OpenSpeedCache();
            var archive = _host.Replays;
            var read = await Task.Run(() => { var r = missing.AsParallel().Select(x => (x.Info.Id, Speed: cache.Get(archive, x.Info.Id))).ToList(); cache.Save(); return r; });
            foreach (var (id, speed) in read) _speeds[id] = speed;
        }
        if (host != _speedHost || scenario != _scenario || _runMode) return;
        var runs = plays.Select(x => (x.Score, Speed: x.Item != null ? _speeds.GetValueOrDefault(x.Item.Info.Id) : x.Cached)).Where(x => x.Speed != null).Select(x => (x.Score, x.Speed!)).ToList();
        host.Child = runs.Count == 0 ? null : SpeedCard(runs);
    }

    /// <summary>
    /// Mouse-speed figures as bars, one block per unit separated by lines (bars of a block share one scale). Each figure shows the top 5%
    /// of plays by score, the average and the bottom 5%.
    /// </summary>
    UIElement? SpeedCard(IReadOnlyList<(double Score, RunSpeed Speed)> runs)
    {
        const string Speed = "{0:0}°/s", Ms = "{0:0} ms", Pct = "{0:0}%";
        (string Label, Func<RunSpeed, double?> Get, string Format)[] speed =
        [
            ("Average speed", r => r.Mean, Speed), ("Max speed", r => r.Max, Speed),
            ("Firing speed", r => r.FireSpeed, Speed), ("Non-firing speed", r => r.IdleSpeed, Speed), ("Peak flick speed", r => r.FlickPeak, Speed),
        ];
        (string Label, Func<RunSpeed, double?> Get, string Format)[] time =
        [
            ("Flick time", r => r.FlickTime * 1000, Ms), ("Reaction time", r => r.Reaction * 1000, Ms), ("Time between kills", r => r.KillInterval * 1000, Ms),
        ];
        (string Label, Func<RunSpeed, double?> Get, string Format)[] ratio =
        [
            ("Path efficiency", r => r.Efficiency * 100, Pct), ("Firing time", r => r.HoldRatio * 100, Pct), ("Speed variation", r => r.SpeedCv * 100, Pct),
        ];
        (string Label, Func<RunSpeed, double?> Get, string Format)[] other =
        [
            ("Flick distance", r => r.FlickDistance, "{0:0.0}°"), ("Direction changes", r => r.ReversalsPerSec, "{0:0.0}/s"), ("Jerk", r => r.Jerk / 1000, "{0:0.0} k°/s³"),
        ];
        string Show(double v, string format) => string.Format(System.Globalization.CultureInfo.InvariantCulture, format, v);

        var ordered = runs.OrderByDescending(r => r.Score).Select(r => r.Speed).ToList();
        var k = Math.Max(1, (int)Math.Ceiling(ordered.Count * 0.05));
        var topBrush = (Brush)FindResource("Green");
        var avgBrush = (Brush)FindResource("Accent");
        var lowBrush = (Brush)FindResource("Red");
        (Brush Brush, IReadOnlyList<RunSpeed> Runs)[] lines = [(topBrush, ordered.Take(k).ToList()), (avgBrush, ordered), (lowBrush, ordered.TakeLast(k).ToList())];

        var host = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        head.Children.Add(new TextBlock { Text = L.T("Mouse speed"), Foreground = FgB, FontSize = 15, FontWeight = FontWeights.SemiBold });
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        void Key(string text, Brush brush)
        {
            legend.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(legend.Children.Count == 0 ? 0 : 12, 0, 5, 0) });
            legend.Children.Add(new TextBlock { Text = text, Foreground = DimB, FontSize = 12 });
        }
        Key(L.T("Top 5%"), topBrush);
        Key(L.T("Average"), avgBrush);
        Key(L.T("Bottom 5%"), lowBrush);
        head.Children.Add(legend);
        host.Children.Add(head);

        const double AxisT = 1.5, Tail = 132, TailY = 12, CapMin = 0.2, CapMax = 0.95, BarH = 8, SlotH = 13, RowPad = 6, LabelW = 124, NumW = 84;
        (string Name, (string Label, Func<RunSpeed, double?> Get, string Format)[] Specs)[] groups =
        [
            ("General", [speed[0], speed[1], speed[2], speed[3], time[2], ratio[1], other[1]]),
            ("Tracking", [ratio[2], other[2]]),
            ("Flick", [speed[4], time[0], time[1], ratio[0], other[0]]),
        ];

        void Group(string name, (string Label, Func<RunSpeed, double?> Get, string Format)[] specs)
        {
            var rows = specs.Select(sp => (Spec: sp, Values: lines.Select(l => { var xs = l.Runs.Select(sp.Get).OfType<double>().ToList(); return xs.Count == 0 ? (double?)null : xs.Average(); }).ToList()))
                .Where(x => x.Values.Any(v => v != null)).ToList();
            if (rows.Count == 0) return;
            host.Children.Add(new TextBlock { Text = L.T(name), Foreground = FgB, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, host.Children.Count > 1 ? 12 : 2, 0, 3) });
            host.Children.Add(new Border { Height = 1, Background = Solid("#26FFFFFF"), Margin = new Thickness(0, 0, 0, 2) });

            // Horizontal bars: one row per figure (label left, bars of the lines stacked), each row scaled to its own largest value.
            var chart = new Grid();
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelW) });
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NumW) });
            chart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var r = 0; r < rows.Count; r++) chart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            chart.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TailY) });
            var axisColor = (Color)ColorConverter.ConvertFromString("#66FFFFFF");
            var axisClear = Color.FromArgb(0, 255, 255, 255);
            LinearGradientBrush Fade(Color from, Color to, bool horizontal) => new(from, to, horizontal ? 0 : 90);

            for (var r = 0; r < rows.Count; r++)
            {
                var (spec, values) = rows[r];
                // Length = (value / largest)^1.2: a small gap shows a little, a big gap shows a lot, all bars of a row shift left together by the shortest one's share, so the longest sits at CapMin of the width for close values, gaps intact, and moves out to CapMax as the spread grows.
                var hi = values.OfType<double>().DefaultIfEmpty(0).Max();
                var lo = values.OfType<double>().DefaultIfEmpty(0).Min();
                var low = hi > 0 ? Math.Pow(Math.Max(0, lo) / hi, 1.2) : 1;
                var shift = 1 - (CapMin + (CapMax - CapMin) * (1 - low));
                var label = new TextBlock { Text = L.T(spec.Label), Foreground = DimB, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, RowPad, 8, RowPad) };
                Grid.SetRow(label, r);
                chart.Children.Add(label);
                // Row separator = an x axis: solid across the plot, a short fixed tail fading out to the left under the numbers.
                var sep = new Border { Height = AxisT, Background = new SolidColorBrush(axisColor), VerticalAlignment = VerticalAlignment.Bottom };
                Grid.SetRow(sep, r);
                Grid.SetColumn(sep, 2);
                chart.Children.Add(sep);
                var sepTail = new Border { Height = AxisT, Width = Tail, Background = Fade(axisClear, axisColor, true), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
                Grid.SetRow(sepTail, r);
                Grid.SetColumnSpan(sepTail, 2); // spans label + number columns so the tail is not clipped to the number column
                chart.Children.Add(sepTail);

                var nums = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, RowPad, 8, RowPad) };
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, RowPad, 0, RowPad) };
                for (var i = 0; i < lines.Length; i++)
                {
                    if (values[i] is not { } y) continue;
                    var frac = hi > 0 ? Math.Max(0.01, Math.Pow(Math.Max(0, y) / hi, 1.2) - shift) : 0;
                    nums.Children.Add(new TextBlock { Text = Show(y, spec.Format), Foreground = FgB, FontSize = 12, Height = SlotH, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = SlotH });
                    var bar = new Grid { Height = SlotH };
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(frac, 0.0001), GridUnitType.Star) });
                    bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - frac, 0.0001), GridUnitType.Star) });
                    bar.Children.Add(new Border { Height = BarH, Background = lines[i].Brush, CornerRadius = new CornerRadius(0, 2, 2, 0), MinWidth = 2, VerticalAlignment = VerticalAlignment.Center });
                    stack.Children.Add(bar);
                }
                Grid.SetColumn(nums, 1);
                Grid.SetRow(nums, r);
                chart.Children.Add(nums);
                Grid.SetColumn(stack, 2);
                Grid.SetRow(stack, r);
                chart.Children.Add(stack);
            }
            // Vertical guides at 25 / 50 / 75% of the bar length, behind the bars (added first so they draw underneath).
            var guides = new Grid();
            for (var c = 0; c < 4; c++)
            {
                guides.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (c == 0) continue;
                var guide = new Border { Width = 1, Background = Solid("#17FFFFFF"), HorizontalAlignment = HorizontalAlignment.Left };
                Grid.SetColumn(guide, c);
                guides.Children.Add(guide);
            }
            Grid.SetColumn(guides, 2);
            Grid.SetRowSpan(guides, rows.Count);
            chart.Children.Insert(0, guides);
            var yAxis = new Border { Width = AxisT, Background = new SolidColorBrush(axisColor), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(yAxis, 2);
            Grid.SetRowSpan(yAxis, rows.Count);
            chart.Children.Add(yAxis);
            var yTail = new Border { Width = AxisT, Background = Fade(axisColor, axisClear, false), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(yTail, 2);
            Grid.SetRow(yTail, rows.Count);
            chart.Children.Add(yTail);
            host.Children.Add(chart);
        }
        foreach (var (name, specs) in groups) Group(name, specs);
        return new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(18, 12, 18, 2), Margin = new Thickness(0, 12, 0, 0), Child = host };
    }

    /// <summary>Brings the cloud scores up to date (older runs, other PCs) and rebuilds the sheet from the score book when they changed.</summary>
    async Task RefreshCloudAsync(string scenario)
    {
        if (!await _d!.LoadCloud(scenario) || scenario != _scenario) return;
        _stats = _d.Book.For(scenario);
        _pbs = new HashSet<int>(_stats.PersonalBestRunIndexes());
        if (!_runMode) BuildOverview();
        BuildRuns();
    }

    UIElement EmptyCard(List<ChartBand>? bands)
    {
        var sp = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
        sp.Children.Add(new TextBlock { Text = L.T("No local runs yet"), FontSize = 16, FontWeight = FontWeights.SemiBold });
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
        var si = ReplayStore.FindRun(Infos(), _scenario, end);
        SetMode(true);
        ApplySelection();
        if (si < 0) { if (_host.IsPending(_scenario, end)) ShowEncoding(end); else NoRecording(); return; }
        var item = _items[si];
        if (_replay is not null && _replay.Info.Id == item.Info.Id && _replay.Info.HasVideo == item.Info.HasVideo && (_duration > 0 || Notice.Visibility == Visibility.Visible)) return;
        LoadReplay(item);
    }

    void SetRecorded(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        ChartCard.Visibility = v; TransportBar.Visibility = v;
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
        ShowEmptyChart();
        Notice.Text = L.T("No video for this run");
        Notice.Visibility = Visibility.Visible;
    }

    /// <summary>Stops playback and releases the loaded replay and its video.</summary>
    void UnloadMedia()
    {
        ExitFullscreen();
        _playing = false; PlayBtn.Content = PlayGlyph;
        _seekTimer.Stop(); _seekPending = false; _dragging = false; _resumeAfterDrag = false; _holdUntil = 0;
        Media.Close();
        PendingHost.Child = null; PendingHost.Visibility = Visibility.Collapsed;
        _replay = null;
        _duration = 0;
        SetTransport(false);
        Overlay.Set(null, default, 0, 0, 0);
        Chart.Set([], 0);
        Chart.SetGhost([]);
        PrevChip.Visibility = Visibility.Collapsed;
        _metrics.Clear();
        TimeNow.Text = "0:00"; TimeTotal.Text = "0:00";
        FitScreen();
        UpdateRunButtons();
    }

    void LoadReplay(ReplayItem item)
    {
        LoadReplayCore(item);
        UpdateRunButtons();
    }

    void LoadReplayCore(ReplayItem item)
    {
        UnloadMedia();
        SetRecorded(true);
        try { _replay = _host.Replays.Load(item.Info); }
        catch (Exception ex) { _replay = null; ShowEmptyChart(); Notice.Text = ex.Message; Notice.Visibility = Visibility.Visible; return; }

        _runLength = (_replay.Info.End - _replay.Info.Start).TotalSeconds;
        _killTimes = _replay.Stats is { } st
            ? st.KillEvents.Select(k => (k.Time - st.Start).TotalSeconds).ToArray()
            : (_replay.Trajectory?.Events.Where(x => x.Type == TrajectoryEventType.Kill).Select(x => (double)x.TSec).ToArray() ?? []);
        _outcomes = ClassifyShots();
        _held = _replay.Trajectory is { } tj ? new HeldAccuracy(tj.Events, _replay.Perf, _runLength) : null;

        if (_replay.VideoPath is { } path)
        {
            Notice.Visibility = Visibility.Collapsed;
            Media.Open(path); // plays on its own; paused again in OnMediaOpened
        }
        else
        {
            Media.Close();
            Notice.Text = L.T("No video for this run");
            Notice.Visibility = Visibility.Visible;
            // the archive keeps perf and CSV without the video: the chart still shows, on the run-time axis (empty without stats)
            _duration = _runLength;
            ChartCard.Visibility = Visibility.Visible; TransportBar.Visibility = Visibility.Visible;
            BuildChart();
            var end = Chart.At(_duration);
            foreach (var m in _metrics) m.Value = end is { } c ? ChartSeriesInfo.Format(m.Series, c[m.Series]) : "–";
            TimeNow.Text = Clock(_runLength); TimeTotal.Text = Clock(_runLength);
        }
    }

    /// <summary>No stats to plot: the same chart card stays, with an empty chart and no transport.</summary>
    void ShowEmptyChart()
    {
        ChartCard.Visibility = Visibility.Visible; TransportBar.Visibility = Visibility.Visible;
        Chart.Set([], 0);
        ShowMetrics();
    }

    /// <summary>True for a cached replay without its video: only the chart is shown.</summary>
    bool NoVideo => _replay is not null && _replay.VideoPath is null;

    /// <summary>Chart x position of a mouse time: video seconds, or run seconds when there is no video.</summary>
    double ChartTime(double mouseSec) => NoVideo ? mouseSec : _replay!.Info.VideoTime(mouseSec);

    IReadOnlyDictionary<float, ShotOutcome> ClassifyShots()
    {
        if (_replay?.Trajectory is not { } traj || _replay.Stats is not { } st) return new Dictionary<float, ShotOutcome>();
        var kills = st.KillEvents.Select((k, i) => new KillShots(_killTimes[i], k.Shots, k.Hits)).ToList();
        return ShotClassifier.Classify(traj.Events, kills, _replay.Perf, out _);
    }

    // ---- player ------------------------------------------------------------------------------------------------

    void SetTransport(bool on)
    {
        TransportBar.IsEnabled = on;
    }

    // ---- fullscreen --------------------------------------------------------------------------------------------
    // The window itself goes borderless over its monitor and everything but the stage is collapsed, so the one
    // MediaElement keeps playing. TrailOverlay sizes its video rect from its own ActualSize, so it follows.

    UIElement[] Bar => [BarScrim, ChartCard, TransportBar];

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

    static bool _muted;

    void OnMuteClick(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        ApplyMute();
    }

    void ApplyMute()
    {
        Media.Muted = _muted;
        MuteBtn.Content = _muted ? "" : "";
        MuteBtn.ToolTip = L.T(_muted ? "Unmute" : "Mute");
    }

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
        if (_fs is not null || !_open || !_runMode || _duration <= 0 || NoVideo || Window.GetWindow(this) is not { } win) return;
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
        Card.ClearValue(WidthProperty); Card.ClearValue(HeightProperty); Stage.ClearValue(HeightProperty);
        Card.Margin = new Thickness(0); Card.CornerRadius = new CornerRadius(0); Card.BorderThickness = new Thickness(0);
        var bg = (Brush)FindResource("Bg");
        Card.Background = bg; Screen.Background = bg; Card.Effect = null; Clipper.Clip = null;
        StyleBar(bg is SolidColorBrush sb ? sb.Color : Colors.Black);
        ((Grid)Clipper.Child).Margin = new Thickness(0);
        SideCol.Width = new GridLength(0); GapCol.Width = new GridLength(0); Side.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Collapsed;
        Stage.Margin = new Thickness(0); Screen.CornerRadius = new CornerRadius(0);
        Grid.SetRow(Stage, 0); Grid.SetRowSpan(Stage, 5);
        BarScrim.Visibility = Visibility.Visible;
        ChartCard.Margin = new Thickness(24, 0, 24, 0); TransportBar.Margin = new Thickness(24, 8, 24, 16);
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
        ChartCard.Margin = new Thickness(0, 12, 0, 0); TransportBar.Margin = new Thickness(0, 8, 0, 0);
        Grid.SetRow(Stage, 1); Grid.SetRowSpan(Stage, 1);
        Stage.Margin = f.StageMargin; Screen.CornerRadius = new CornerRadius(14);
        Side.Visibility = Visibility.Visible; Header.Visibility = Visibility.Visible; GapCol.Width = new GridLength(12);
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
        if (_dragging || ChartCard.IsMouseOver || TransportBar.IsMouseOver || SpeedPop.IsOpen) return; // stay while in use; re-check next tick
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
        if (_replay is null) return;
        if (_replay.VideoPath is { } p && File.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"");
    }

    void OnMediaOpened()
    {
        if (_replay is null || Media.Duration <= 0) return;
        _duration = Media.Duration;
        Media.SpeedRatio = _speed;
        ApplyMute();
        Media.Pause();
        Media.Position = Math.Clamp(_replay.Info.VideoTime(0) - 1, 0, _duration);
        BuildChart();
        SetTransport(true);
        LayoutCard(new Size(ActualWidth, ActualHeight));
        FitScreen();
        if (!_playing) TogglePlay();
    }

    void OnStageSized(object sender, SizeChangedEventArgs e) => FitScreen();

    void FitScreen()
    {
        double W = Stage.ActualWidth, H = Stage.ActualHeight;
        if (Media.NaturalVideoHeight <= 0 || W <= 0 || H <= 0 || _fs is null)
        {
            Screen.Margin = new Thickness(0);
            return;
        }
        var a = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var w = Math.Min(W, H * a);
        var h = w / a;
        Screen.Margin = new Thickness((W - w) / 2, (H - h) / 2, (W - w) / 2, (H - h) / 2);
    }

    void OnMediaEnded()
    {
        _playing = false; PlayBtn.Content = PlayGlyph;
        Media.Pause();
    }

    void OnMediaFailed(Exception ex)
    {
        AppLog.Write("replay", "media failed: " + ex);
        ErrorReporter.Report("replay media", ex);
        SetTransport(false);
        Notice.Text = L.T("Video failed to load: ") + ex.Message;
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
    double CurPos => _dragging || _seekPending ? _shownPos : Environment.TickCount64 < _holdUntil ? _dragPos : Media.Position;

    void OnRendering(object? sender, EventArgs e)
    {
        if (!_open || !_runMode || _replay is null || _duration <= 0 || NoVideo) return;
        var pos = CurPos;
        var t = _replay.Info.MouseTime(pos);
        ShowReadout(pos, t);

        if (!_host.Ui.ShowTrail) return;
        if (_replay.Trajectory is not { Samples.Count: > 0 } traj || Media.NaturalVideoHeight == 0 || t < 0 || t > _runLength + 0.5)
        {
            Overlay.Set(null, default, 0, 0, 0);
            return;
        }
        double videoAspect = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var st = _replay.Stats?.Settings ?? new Core.Stats.InputSettings();
        var renderAspect = ViewProjection.ParseAspect(st.Resolution) ?? videoAspect;
        var hfov = ViewProjection.HorizontalFovDeg(st.Fov, st.FovScale, renderAspect);
        Overlay.Set(CameraPath.BuildTrail(traj.Samples, traj.Events, t, outcomes: _outcomes), CameraPath.At(traj.Samples, t), hfov, videoAspect, renderAspect, _held);
    }
    /// <summary>Playhead, clock and metric values at a chart position; without a video the chart axis is run seconds.</summary>
    void ShowReadout(double pos, double? runSec = null)
    {
        TimeNow.Text = Clock(Math.Min(runSec ?? pos, _runLength)); TimeTotal.Text = Clock(_runLength);
        Chart.SetPlayhead(pos);
        var cur = Chart.At(pos);
        foreach (var m in _metrics) m.Value = cur is { } c ? ChartSeriesInfo.Format(m.Series, c[m.Series]) : "–";
    }

    void BeginDrag()
    {
        _shownPos = Media.Position;
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
        Media.Position = _dragPos;
    }

    void OnChartDown(object sender, MouseButtonEventArgs e) { if (_duration <= 0) return; if (!NoVideo) BeginDrag(); _dragging = true; Chart.CaptureMouse(); SeekToChart(e); }
    void OnChartMove(object sender, MouseEventArgs e) { if (_dragging && Chart.IsMouseCaptured) SeekToChart(e); }
    void OnChartUp(object sender, MouseButtonEventArgs e)
    {
        if (!Chart.IsMouseCaptured) return;
        SeekToChart(e);
        Chart.ReleaseMouseCapture();
        _dragging = false;
        if (NoVideo) return;
        CommitSeek();
        if (_resumeAfterDrag) { _resumeAfterDrag = false; Media.Play(); }
    }
    void SeekToChart(MouseEventArgs e)
    {
        var sec = Math.Clamp(e.GetPosition(Chart).X / Math.Max(1, Chart.ActualWidth), 0, 1) * _duration;
        if (NoVideo) { ShowReadout(sec); return; }
        DragTo(sec);
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_duration <= 0) return;
        CommitSeek();
        _playing = !_playing;
        if (_playing)
        {
            if (Media.Position >= _duration - 0.05) Media.Position = Math.Max(0, _replay!.Info.VideoTime(0));
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
        Media.Position = _dragPos;
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
        if (_replay?.Perf is { Buckets.Count: > 0 } perf && _duration > 0)
        {
            var series = PerfSeries.Build(perf);
            var speeds = SpeedAt(series.Select(p => p.TSec).ToList());
            Chart.Set(series.Select((p, i) => new ChartPoint(ChartTime(p.TSec), p.Accuracy, p.Score, p.Kills, p.Shots, p.Hits, speeds[i])).ToList(), _duration, KillVideoTimes());
            ShowMetrics();
            return;
        }
        if (_replay?.Stats is not { KillEvents.Count: > 0 } st || _duration <= 0) { Chart.Set([], 0); ShowMetrics(); return; }
        int shots = 0, hits = 0;
        var pts = new List<ChartPoint>();
        var killSpeeds = SpeedAt(_killTimes);
        for (var k = 0; k < st.KillEvents.Count; k++)
        {
            var ev = st.KillEvents[k];
            shots += ev.Shots; hits += ev.Hits;
            pts.Add(new ChartPoint(ChartTime(_killTimes[k]), shots == 0 ? 0 : (double)hits / shots, Kills: k + 1, Shots: shots, Hits: hits, MouseSpeed: killSpeeds[k]));
        }
        Chart.Set(pts, _duration, KillVideoTimes());
        ShowMetrics();
    }

    /// <summary>Mean mouse speed of the stretch ending at each mouse time; all NaN without a recorded trajectory.</summary>
    double[] SpeedAt(IReadOnlyList<double> ends)
    {
        if (_replay?.Trajectory is { } t) return MouseSpeedStats.Averages(t, ends);
        var none = new double[ends.Count];
        Array.Fill(none, double.NaN);
        return none;
    }

    double[] KillVideoTimes() => _killTimes.Select(ChartTime).ToArray();

    /// <summary>One chip per series that has data; applies the saved selection to the chart.</summary>
    void ShowMetrics()
    {
        _metrics.Clear();
        foreach (var series in ChartSeriesInfo.All)
            if (Chart.Has(series) || !Chart.HasData) _metrics.Add(new MetricRow(series, _visible.Contains(series)));
        Chart.SetVisible(_visible);
        UpdatePrev();
    }

    (Guid Id, PerfData Perf)? _prev;

    /// <summary>The newest earlier local run of the scenario that has a .perf, parsed once per replay.</summary>
    (Guid Id, PerfData Perf, ReplayInfo Info)? FindPrev()
    {
        if (_replay is null) return null;
        foreach (var it in _items.Where(x => x.Info.Start < _replay.Info.Start && x.Info.Scenario.Equals(_replay.Info.Scenario, StringComparison.OrdinalIgnoreCase)).OrderByDescending(x => x.Info.Start))
        {
            if (_prev is { } c && c.Id == it.Info.Id) return (c.Id, c.Perf, it.Info);
            try
            {
                if (_host.Replays.ReadParts(it.Info.Id).Perf is not { Buckets.Count: > 0 } perf) continue;
                _prev = (it.Info.Id, perf);
                return (it.Info.Id, perf, it.Info);
            }
            catch (Exception e) when (e is IOException or ReplayFormatException) { }
        }
        return null;
    }

    /// <summary>Shows the chip when an earlier recorded run exists; draws that run dashed on the same run-time axis while the chip is on.</summary>
    void UpdatePrev()
    {
        var prev = Chart.Has(ChartSeries.Accuracy) ? FindPrev() : null;
        PrevChip.Visibility = prev is null ? Visibility.Collapsed : Visibility.Visible;
        PrevChip.Opacity = _host.Ui.ComparePrev ? 1 : 0.5;
        if (prev is not { } p || !_host.Ui.ComparePrev || _replay is null) { Chart.SetGhost([]); return; }
        PrevChip.ToolTip = L.F("Previous run · {0}", L.Stamp(p.Info.Start));
        Chart.SetGhost(PerfSeries.Build(p.Perf).Select(x => new ChartPoint(ChartTime(x.TSec), x.Accuracy, x.Score, x.Kills, x.Shots, x.Hits)).ToList());
    }

    void OnPrevClick(object sender, MouseButtonEventArgs e)
    {
        _host.Ui.ComparePrev = !_host.Ui.ComparePrev;
        _host.SaveUi();
        UpdatePrev();
    }
}
