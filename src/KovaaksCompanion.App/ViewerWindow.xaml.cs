using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Session;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

public sealed record SessionItem(string Folder, SessionInfo Info)
{
    public string Title => Info.Scenario;
    public string Subtitle => $"{Info.Start:yyyy-MM-dd HH:mm}  {Info.Score:0.#} pts  {Info.Accuracy:P0}  {Info.Kills} kills{(Info.Partial ? "  partial" : "")}";
}

public sealed record KillRow(int Index, string Ttk, string Flick, string Path);

public partial class ViewerWindow : Window
{
    const double FrameSec = 1.0 / 60;

    readonly AppHost _host;
    readonly ObservableCollection<SessionItem> _items = [];
    LoadedSession? _s;
    double _duration, _runLength;
    double[] _killTimes = [];
    IReadOnlyDictionary<float, ShotOutcome> _outcomes = new Dictionary<float, ShotOutcome>();
    double _speed = 1;
    bool _playing, _dragging;

    public ViewerWindow(AppHost host)
    {
        InitializeComponent();
        Backdrop.Apply(this);
        _host = host;
        List.ItemsSource = _items;
        foreach (var (folder, info) in SessionStore.List(host.SessionsFolder)) _items.Add(new SessionItem(folder, info));
        host.SessionSaved += OnSessionSaved;
        CompositionTarget.Rendering += OnRendering;
        Closed += (_, _) =>
        {
            host.SessionSaved -= OnSessionSaved;
            CompositionTarget.Rendering -= OnRendering;
            Media.Close();
        };
    }

    void OnSessionSaved(SessionInfo info, string folder) => Dispatcher.BeginInvoke(() =>
    {
        var i = 0;
        while (i < _items.Count && _items[i].Info.Start > info.Start) i++;
        _items.Insert(i, new SessionItem(folder, info));
    });

    void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not SessionItem item) return;
        _playing = false; PlayBtn.Content = "Play";
        Media.Close();
        _duration = 0;
        Overlay.Set(null, default, 0, 0);
        Markers.Children.Clear();
        try { _s = SessionStore.Load(item.Folder); }
        catch (Exception ex) { _s = null; Notice.Text = ex.Message; Notice.Visibility = Visibility.Visible; StatsText.Text = ""; Kills.ItemsSource = null; return; }

        _runLength = (_s.Info.End - _s.Info.Start).TotalSeconds;
        _killTimes = _s.Stats is { } st
            ? st.KillEvents.Select(k => (k.Time - st.Start).TotalSeconds).ToArray()
            : (_s.Trajectory?.Events.Where(x => x.Type == TrajectoryEventType.Kill).Select(x => (double)x.TSec).ToArray() ?? []);
        _outcomes = ClassifyShots();
        ShowStats();
        Chart.Set([], 0);
        AccText.Text = "";

        if (_s.VideoPath is { } path)
        {
            Notice.Visibility = Visibility.Collapsed;
            Media.Source = new Uri(path);
            Media.Play(); // paused again in MediaOpened; a loaded-but-never-played element renders no frame
        }
        else
        {
            Media.Source = null;
            Notice.Text = "No video for this session";
            Notice.Visibility = Visibility.Visible;
        }
    }

    void ShowStats()
    {
        var i = _s!.Info;
        StatsText.Text = $"{i.Scenario}\n{i.Start:yyyy-MM-dd HH:mm:ss}\nScore: {i.Score:0.##}\nAccuracy: {i.Accuracy:P1} ({i.HitCount}/{i.HitCount + i.MissCount})\nKills: {i.Kills}"
            + (i.Partial ? "\nPartial: mouse or video does not cover the whole run" : "")
            + (i.DegreesAvailable ? "" : "\nUnknown sens scale: no trail")
            + ShotSummary();
        var flicks = _s.Trajectory is { } t ? KillFlicks.Compute(t.Samples, _killTimes) : null;
        var ttks = _s.Stats?.KillEvents;
        Kills.ItemsSource = _killTimes.Select((_, k) => new KillRow(k + 1,
            ttks is null ? "" : $"{ttks[k].Ttk.TotalSeconds:0.00}s",
            flicks is null ? "" : $"{flicks[k].FlickTimeSec:0.00}s",
            flicks is null ? "" : $"{flicks[k].PathDeg:0.0}°")).ToList();
    }

    IReadOnlyDictionary<float, ShotOutcome> ClassifyShots()
    {
        if (_s?.Trajectory is not { } traj || _s.Stats is not { } st) return new Dictionary<float, ShotOutcome>();
        var kills = st.KillEvents.Select((k, i) => new KillShots(_killTimes[i], k.Shots, k.Hits)).ToList();
        return ShotClassifier.Classify(traj.Events, kills, _s.Perf);
    }

    string ShotSummary()
    {
        if (_outcomes.Count == 0) return "";
        int hit = 0, miss = 0;
        foreach (var o in _outcomes.Values) { if (o == ShotOutcome.Hit) hit++; else if (o == ShotOutcome.Miss) miss++; }
        return $"\nShots: {hit} hit (green), {miss} miss (red), {_outcomes.Count - hit - miss} unknown (grey)";
    }

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_s is null || !Media.NaturalDuration.HasTimeSpan) return;
        _duration = Media.NaturalDuration.TimeSpan.TotalSeconds;
        Seek.Maximum = _duration;
        Media.SpeedRatio = _speed;
        Media.Pause();
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(_s.Info.VideoTime(0), 0, _duration));
        BuildChart();
        DrawMarkers();
    }

    void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _playing = false; PlayBtn.Content = "Play";
        Media.Pause();
    }

    void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Notice.Text = "Video failed to load: " + e.ErrorException.Message;
        Notice.Visibility = Visibility.Visible;
    }

    void OnMarkersResized(object sender, SizeChangedEventArgs e) => DrawMarkers();

    void DrawMarkers()
    {
        Markers.Children.Clear();
        if (_s is null || _duration <= 0) return;
        foreach (var k in _killTimes)
        {
            var x = Math.Clamp(_s.Info.VideoTime(k) / _duration, 0, 1) * Markers.ActualWidth;
            var r = new Rectangle { Width = 2, Height = 6, RadiusX = 1, RadiusY = 1, Fill = (Brush)FindResource("Red") };
            Canvas.SetLeft(r, x - 1); Canvas.SetBottom(r, 0);
            Markers.Children.Add(r);
        }
    }

    void OnRendering(object? sender, EventArgs e)
    {
        if (_s is null || _duration <= 0) return;
        var pos = Media.Position.TotalSeconds;
        if (!_dragging) Seek.Value = Math.Clamp(pos, 0, _duration);
        var t = _s.Info.MouseTime(pos);
        TimeText.Text = $"{Math.Max(0, t):0.00} / {_runLength:0.00} s";
        Chart.SetPlayhead(pos);
        if (Chart.At(pos) is { } cur)
            AccText.Text = double.IsNaN(cur.Score) ? $"{cur.Accuracy:P0}" : $"{cur.Accuracy:P0}  ·  10s {cur.RecentAccuracy:P0}  ·  {cur.Score:0} pts";
        else AccText.Text = "–";

        if (_s.Trajectory is not { Samples.Count: > 0 } traj || Media.NaturalVideoHeight == 0 || t < 0 || t > _runLength + 0.5)
        {
            Overlay.Set(null, default, 0, 0);
            return;
        }
        double aspect = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var st = _s.Info.Settings;
        var hfov = ViewProjection.HorizontalFovDeg(st.Fov, st.FovScale, aspect);
        Overlay.Set(CameraPath.BuildTrail(traj.Samples, traj.Events, t, outcomes: _outcomes), CameraPath.At(traj.Samples, t), hfov, aspect);
    }

    void OnSeekDown(object sender, MouseButtonEventArgs e) => _dragging = true;
    void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Media.Position = TimeSpan.FromSeconds(Seek.Value);
    }
    void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_dragging && _duration > 0) Media.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) return;
        _playing = !_playing;
        if (_playing)
        {
            if (Media.Position.TotalSeconds >= _duration - 0.05) Media.Position = TimeSpan.FromSeconds(Math.Max(0, _s!.Info.VideoTime(0)));
            Media.Play();
        }
        else Media.Pause();
        PlayBtn.Content = _playing ? "Pause" : "Play";
    }

    void Step(double sec)
    {
        if (_duration <= 0) return;
        if (_playing) { _playing = false; Media.Pause(); PlayBtn.Content = "Play"; }
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(Media.Position.TotalSeconds + sec, 0, _duration));
    }
    void OnStepBack(object sender, RoutedEventArgs e) => Step(-FrameSec);
    void OnStepForward(object sender, RoutedEventArgs e) => Step(FrameSec);

    void OnSpeedChecked(object sender, RoutedEventArgs e)
    {
        if (Media is null || sender is not RadioButton { Tag: string tag }) return;
        _speed = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        Media.SpeedRatio = _speed;
    }

    /// <summary>Running accuracy per second from the .perf, else per kill.</summary>
    void BuildChart()
    {
        if (_s?.Perf is { Buckets.Count: > 0 } perf && _duration > 0)
        {
            Chart.Set(PerfSeries.Build(perf).Select(p => new ChartPoint(_s.Info.VideoTime(p.TSec), p.Accuracy, p.RecentAccuracy, p.Score)).ToList(), _duration);
            return;
        }
        if (_s?.Stats is not { KillEvents.Count: > 0 } st || _duration <= 0) { Chart.Set([], 0); return; }
        int shots = 0, hits = 0;
        var pts = new List<ChartPoint>();
        for (var k = 0; k < st.KillEvents.Count; k++)
        {
            var ev = st.KillEvents[k];
            shots += ev.Shots; hits += ev.Hits;
            pts.Add(new ChartPoint(_s.Info.VideoTime(_killTimes[k]),
                shots == 0 ? 0 : (double)hits / shots));
        }
        Chart.Set(pts, _duration);
    }
}
