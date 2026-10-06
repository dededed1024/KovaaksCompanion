using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
    static readonly double[] Speeds = [0.25, 0.5, 1, 1.5, 2];
    const double FrameSec = 1.0 / 60;

    readonly AppHost _host;
    readonly ObservableCollection<SessionItem> _items = [];
    LoadedSession? _s;
    double _duration, _runLength;
    double[] _killTimes = [];
    bool _playing, _dragging;

    public ViewerWindow(AppHost host)
    {
        InitializeComponent();
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
        ShowStats();
        NudgeText.Text = NudgeLabel();

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
            + (i.DegreesAvailable ? "" : "\nUnknown sens scale: no trail");
        var flicks = _s.Trajectory is { } t ? KillFlicks.Compute(t.Samples, _killTimes) : null;
        var ttks = _s.Stats?.KillEvents;
        Kills.ItemsSource = _killTimes.Select((_, k) => new KillRow(k + 1,
            ttks is null ? "" : $"{ttks[k].Ttk.TotalSeconds:0.00}s",
            flicks is null ? "" : $"{flicks[k].FlickTimeSec:0.00}s",
            flicks is null ? "" : $"{flicks[k].PathDeg:0.0}°")).ToList();
    }

    void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_s is null || !Media.NaturalDuration.HasTimeSpan) return;
        _duration = Media.NaturalDuration.TimeSpan.TotalSeconds;
        Seek.Maximum = _duration;
        Media.SpeedRatio = Speeds[Speed.SelectedIndex];
        Media.Pause();
        Media.Position = TimeSpan.FromSeconds(Math.Clamp(_s.Info.VideoTime(0), 0, _duration));
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
            var r = new Rectangle { Width = 2, Height = 10, Fill = Brushes.OrangeRed };
            Canvas.SetLeft(r, x - 1); Canvas.SetTop(r, 0);
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

        if (_s.Trajectory is not { Samples.Count: > 0 } traj || Media.NaturalVideoHeight == 0 || t < 0 || t > _runLength + 0.5)
        {
            Overlay.Set(null, default, 0, 0);
            return;
        }
        double aspect = (double)Media.NaturalVideoWidth / Media.NaturalVideoHeight;
        var st = _s.Info.Settings;
        var hfov = ViewProjection.HorizontalFovDeg(st.Fov, st.FovScale, aspect);
        Overlay.Set(CameraPath.BuildTrail(traj.Samples, traj.Events, t), CameraPath.At(traj.Samples, t), hfov, aspect);
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

    void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Media != null && Speed.SelectedIndex >= 0) Media.SpeedRatio = Speeds[Speed.SelectedIndex];
    }

    string NudgeLabel() => _s is null ? "" : $"{_s.Info.SyncNudgeMs:+0;-0;0} ms";

    void Nudge(double ms)
    {
        if (_s is null) return;
        var info = _s.Info with { SyncNudgeMs = _s.Info.SyncNudgeMs + ms };
        try { SessionStore.WriteInfo(_s.Folder, info); }
        catch (Exception ex) { Notice.Text = "Could not save sync: " + ex.Message; Notice.Visibility = Visibility.Visible; return; }
        _s = _s with { Info = info };
        NudgeText.Text = NudgeLabel();
        DrawMarkers();
    }
    void OnNudgeMinus(object sender, RoutedEventArgs e) => Nudge(-10);
    void OnNudgePlus(object sender, RoutedEventArgs e) => Nudge(10);
}
