using System.Diagnostics;
using System.Windows;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Session;
using KovaaksCompanion.Core.Video;
using WinForms = System.Windows.Forms;

namespace KovaaksCompanion.App;

/// <summary>Wires capture, video, watcher and saver together and owns the tray icon (APP-001/002).</summary>
public sealed class AppHost : IDisposable
{
    readonly Application _app;
    AppSettings _settings = AppSettings.Load();
    MouseRingBuffer _buffer = null!;
    ClockAnchor _anchor;
    RawInputCapture? _capture;
    VideoCaptureService? _video;
    StatsWatcher? _watcher;
    SessionSaver _saver = null!;
    WinForms.NotifyIcon? _tray;
    WinForms.ToolStripMenuItem _status = null!, _pause = null!;
    ViewerWindow? _viewer;
    bool _paused;
    string _lastSaved = "none yet", _lastError = "";

    public AppHost(Application app) => _app = app;

    public string SessionsFolder => _settings.SessionsFolder;
    /// <summary>Raised on a thread-pool thread after a session folder is complete.</summary>
    public event Action<SessionInfo, string>? SessionSaved;

    public void Start()
    {
        _anchor = ClockAnchor.Now();
        _buffer = new MouseRingBuffer(Math.Max(600, _settings.BufferMinutes * 60.0), _anchor.Frequency, _anchor.Qpc);
        StartCapture();

        _video = new VideoCaptureService(new VideoOptions
        {
            FfmpegPath = _settings.FfmpegPath, Fps = _settings.Fps, BufferMinutes = _settings.BufferMinutes, BufferDir = _settings.BufferFolder,
        });
        _video.Error += m => _lastError = "video: " + m;
        try { _video.Start(); } catch (Exception e) { _lastError = "video: " + e.Message; }

        _saver = new SessionSaver(_settings.SessionsFolder, _buffer, _anchor, (s, e, p) => _video.ExtractClipAsync(s, e, p));
        _saver.Error += m => _lastError = m;
        _saver.Saved += (info, folder) =>
        {
            _lastSaved = $"{info.Scenario} {info.Start:HH:mm:ss}{(info.Partial ? " (partial)" : "")}";
            SessionSaved?.Invoke(info, folder);
        };

        _watcher = new StatsWatcher(_settings.StatsFolder, DateTime.Now);
        _watcher.RunFinished += (run, csv) => { if (!_paused) _ = _saver.Enqueue(run, csv); };
        try { _watcher.Start(); } catch (Exception e) { _lastError = "stats folder: " + e.Message; }

        BuildTray();
    }

    void StartCapture()
    {
        try { _capture = new RawInputCapture(_buffer.Add); _capture.Start(); }
        catch (Exception e) { _capture = null; _lastError = "mouse: " + e.Message; }
    }

    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        _status = new WinForms.ToolStripMenuItem("") { Enabled = false };
        _pause = new WinForms.ToolStripMenuItem("Pause recording", null, (_, _) => TogglePause());
        menu.Items.Add(_status);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Open viewer", null, (_, _) => ShowViewer());
        menu.Items.Add(_pause);
        menu.Items.Add("Settings...", null, (_, _) => _app.Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _app.Dispatcher.Invoke(_app.Shutdown));
        menu.Opening += (_, _) => _status.Text = StatusText();

        _tray = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application, Text = "KovaaK's Companion", Visible = true, ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowViewer();
    }

    /// <summary>APP-002 status line: game / recording / last saved session.</summary>
    string StatusText()
    {
        var rec = _video?.IsRecording == true;
        var s = $"{(rec ? "Game detected, recording" : "Waiting for game")}{(_paused ? " (paused)" : "")} | last: {_lastSaved}";
        return _lastError.Length > 0 ? s + " | " + _lastError : s;
    }

    void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
        {
            _capture?.Dispose(); _capture = null;
            _buffer.Pause(Stopwatch.GetTimestamp());
        }
        else
        {
            _buffer.Resume(Stopwatch.GetTimestamp());
            StartCapture();
        }
        _pause.Text = _paused ? "Resume recording" : "Pause recording";
    }

    public void ShowViewer() => _app.Dispatcher.Invoke(() =>
    {
        if (_viewer == null)
        {
            _viewer = new ViewerWindow(this);
            _viewer.Closed += (_, _) => _viewer = null;
            _viewer.Show();
        }
        else { if (_viewer.WindowState == WindowState.Minimized) _viewer.WindowState = WindowState.Normal; _viewer.Activate(); }
    });

    void ShowSettings()
    {
        var w = new SettingsWindow(_settings);
        if (w.ShowDialog() == true)
        {
            _settings = w.Result;
            _settings.Save();
            MessageBox.Show("Settings saved. Restart the app to apply them.", "KovaaK's Companion");
        }
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _watcher?.Dispose();
        _capture?.Dispose();
        _video?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8));
    }
}
