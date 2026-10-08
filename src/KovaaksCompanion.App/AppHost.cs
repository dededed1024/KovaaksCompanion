using System.Diagnostics;
using System.IO;
using System.Windows;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Session;
using KovaaksCompanion.Core.Update;
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
    System.Drawing.Icon? _trayIcon;
    static System.Drawing.Icon LoadTrayIcon() { using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream; return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize); }
    MainWindow? _main;
    string _lastError = "";
    ReleaseInfo? _pendingUpdate;
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    sealed class Pending(string scenario, DateTime end) { public string Scenario = scenario; public DateTime End = end; public double Progress; }
    readonly List<Pending> _pending = [];

    public AppHost(Application app) { _app = app; _uiFile = _settings.UiFile; Ui = UiState.Load(_uiFile); }
    readonly string _uiFile;

    public AppSettings Settings => _settings;
    /// <summary>True while the game window is being recorded.</summary>
    public bool GameActive => _video?.IsRecording == true;
    public string SessionsFolder => _settings.SessionsFolder;
    /// <summary>Checks if a run's replay is currently being saved.</summary>
    public bool IsPending(string scenario, DateTime end) => PendingProgress(scenario, end) >= 0;

    /// <summary>Encoding progress 0..1 of a run's clip, or -1 when it is not being saved.</summary>
    public double PendingProgress(string scenario, DateTime end)
    {
        lock (_pending)
        {
            return _pending.FirstOrDefault(p => p.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - end).TotalSeconds) < SessionStore.RunMatchTolerance.TotalSeconds)?.Progress ?? -1;
        }
    }

    /// <summary>Raised on a thread-pool thread as a pending run's clip encodes (scenario, run end, 0..1).</summary>
    public event Action<string, DateTime, double>? EncodeProgress;
    /// <summary>Favorites and chart selection. Mutate on the UI thread, then call <see cref="SaveUi"/>.</summary>
    public UiState Ui { get; }
    /// <summary>Raised on the calling (UI) thread after <see cref="SaveUi"/>.</summary>
    public event Action? UiChanged;
    /// <summary>Raised on a thread-pool thread after a session folder is complete.</summary>
    public event Action<SessionInfo, string>? SessionSaved;
    /// <summary>Raised on a thread-pool thread as soon as a run's stats CSV is complete (before the session clip is saved).</summary>
    public event Action? RunFinished;
    /// <summary>Raised on a thread-pool thread when <see cref="GameActive"/> may have changed.</summary>
    public event Action? GameStateChanged;

    /// <summary>The embedded (extracted + verified) ffmpeg, else detection. Null when none.</summary>
    public static string? ResolveFfmpeg() => ResolveFfmpeg(out _);

    static string? ResolveFfmpeg(out string source)
    {
        var bundled = FfmpegBundle.Ensure(() => typeof(AppHost).Assembly.GetManifestResourceStream("ffmpeg.exe"));
        if (bundled != null) { source = "embedded"; return bundled; }
        AppLog.Write("ffmpeg", "embedded ffmpeg unavailable, trying PathDetector");
        var found = PathDetector.FindFfmpeg();
        source = found != null ? "PathDetector" : "none";
        return found;
    }

    void SetError(string m) { _lastError = m; AppLog.Write("error", m); }

    public void Start(bool showMain)
    {
        AppLog.Write("app", $"start: version {typeof(AppHost).Assembly.GetName().Version}, OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), 64-bit={Environment.Is64BitProcess}");
        var detected = _settings.WithDetectedPaths();
        if (detected != _settings)
        {
            _settings = detected;
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        _anchor = ClockAnchor.Now();
        _buffer = new MouseRingBuffer(new VideoOptions().BufferMinutes * 60.0, _anchor.Frequency, _anchor.Qpc);
        StartCapture();

        var ffmpeg = ResolveFfmpeg(out var ffmpegSource);
        AppLog.Write("ffmpeg", $"resolved: {ffmpeg ?? "(none)"} source={ffmpegSource}");
        if (ffmpeg != null) StartVideo(ffmpeg);
        else SetError("ffmpeg missing (video disabled)");

        _saver = new SessionSaver(_settings.SessionsFolder, _buffer, ClockAnchor.Now,
            (s, e, p, prog) => _video?.ExtractClipAsync(s, e, p, prog) ?? Task.FromResult<ClipResult?>(null));
        _saver.Error += SetError;
        _saver.Saved += (info, folder) =>
        {
            lock (_pending)
            {
                _pending.RemoveAll(p => p.Scenario.Equals(info.Scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - info.End).TotalSeconds) < SessionStore.RunMatchTolerance.TotalSeconds);
            }
            AppLog.Write("session", $"saved {folder}");
            SessionSaved?.Invoke(info, folder);
        };

        _watcher = new StatsWatcher(_settings.StatsFolder, DateTime.Now);
        _watcher.RunFinished += (run, csv) =>
        {
            AppLog.Write("session", $"run finished: {csv}");
            var item = new Pending(run.Scenario, run.End);
            lock (_pending) { _pending.Add(item); }
            RunFinished?.Invoke(); // the run shows now, with a progress ring until the clip is saved
            _ = _saver.Enqueue(run, csv, f => { item.Progress = f; EncodeProgress?.Invoke(item.Scenario, item.End, f); }).ContinueWith(_ =>
            {
                lock (_pending)
                {
                    if (_pending.RemoveAll(p => p.Scenario.Equals(run.Scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - run.End).TotalSeconds) < SessionStore.RunMatchTolerance.TotalSeconds) > 0)
                    {
                        RunFinished?.Invoke();
                    }
                }
            });
        };
        try { _watcher.Start(); } catch (Exception e) { SetError("stats folder: " + e.Message); }

        BuildTray();
        Autostart.Refresh(_settings.StartWithWindows);
        if (showMain) ShowMain("Stats");
        _ = CheckForUpdate();
    }

    /// <summary>One background release check per run; a newer release is offered in the main window, now or when it next opens.</summary>
    async Task CheckForUpdate()
    {
        try
        {
            var latest = await UpdateCheck.Fetch(Http);
            if (!UpdateCheck.IsNewer(latest, App.CurrentVersion)) return;
            _pendingUpdate = latest;
            _app.Dispatcher.Invoke(OfferUpdate);
        }
        catch (Exception e) { AppLog.Write("update", "check failed: " + e.Message); }
    }

    /// <summary>Opens the update popup for a release found by a manual check.</summary>
    public void ShowUpdate(KovaaksCompanion.Core.Update.ReleaseInfo release) => _app.Dispatcher.Invoke(() => _main?.ShowUpdate(release));

    void OfferUpdate()
    {
        if (_main == null || _pendingUpdate == null) return;
        var r = _pendingUpdate; _pendingUpdate = null;
        _main.ShowUpdate(r);
    }


    void StartVideo(string ffmpegPath)
    {
        if (_video != null) return;
        _video = new VideoCaptureService(new VideoOptions
        {
            FfmpegPath = ffmpegPath, BufferDir = _settings.BufferFolder, Quality = _settings.VideoQuality.ToCq(), MaxHeight = _settings.VideoQuality.MaxHeight(),
            Fps = _settings.VideoFps,
        }, () => HandCamConfig.From(_settings));
        _video.Error += m => SetError("video: " + m);
        _video.StateChanged += () => GameStateChanged?.Invoke();
        try { _video.Start(); } catch (Exception e) { SetError("video: " + e.Message); }
    }

    void StartCapture()
    {
        try { _capture = new RawInputCapture(_buffer.Add); _capture.Start(); }
        catch (Exception e) { _capture = null; SetError("mouse: " + e.Message); }
    }

    void BuildTray()
    {
        var menu = new WinForms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, Font = new System.Drawing.Font("Malgun Gothic", 10f) };
        menu.Items.Add("Open", null, (_, _) => ShowMain());
        menu.Items.Add("Exit", null, (_, _) => _app.Dispatcher.Invoke(_app.Shutdown));

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon = LoadTrayIcon(), Text = "KovaaK's Companion", Visible = true, ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMain(); };
        if (_lastError.Length > 0) _tray.ShowBalloonTip(5000, "KovaaK's Companion", _lastError, WinForms.ToolTipIcon.Warning);
    }

    public void ShowMain(string? page = null) => _app.Dispatcher.Invoke(() =>
    {
        if (_main == null)
        {
            _main = new MainWindow(this);
            _main.Closed += (_, _) => _main = null;
            _main.Show();
        }
        else { if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal; _main.Activate(); }
        if (page != null) _main.ShowPage(page);
        OfferUpdate();
    });

    /// <summary>Opens the replay popup (scenario overview) over the main window; <paramref name="origin"/> is the clicked point in window coordinates.</summary>
    public void ShowScenario(string scenario, Point? origin) => _app.Dispatcher.Invoke(() =>
    {
        ShowMain();
        _main!.ShowScenario(scenario, origin);
    });

    /// <summary>Opens the replay popup on a play session; <paramref name="origin"/> is the clicked point in window coordinates.</summary>
    public void ShowSession(KovaaksCompanion.Core.Library.PlaySession session, Point? origin) => _app.Dispatcher.Invoke(() =>
    {
        ShowMain();
        _main!.ShowSession(session, origin);
    });

    /// <summary>Opens the replay of a past run; false when that run was not recorded.</summary>
    public bool ShowRun(string scenario, DateTime end) => _app.Dispatcher.Invoke(() =>
    {
        ShowMain();
        return _main!.ShowRun(scenario, end);
    });

    public void SaveUi()
    {
        try { Ui.Save(_uiFile); } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { SetError("ui state: " + e.Message); }
        UiChanged?.Invoke();
    }

    public void ApplySettings(AppSettings s)
    {
        _settings = s;
        _settings.Save();
    }

    public void Dispose()
    {
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _watcher?.Dispose();
        _capture?.Dispose();
        _video?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8));
    }
}

/// <summary>Dark flat tray menu: translucent-feel hover, accent-free, no borders.</summary>
sealed class DarkMenuRenderer : WinForms.ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? System.Drawing.Color.White : System.Drawing.Color.FromArgb(161, 161, 168);
        base.OnRenderItemText(e);
    }

    sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        static System.Drawing.Color C(int r, int g, int b) => System.Drawing.Color.FromArgb(r, g, b);
        public override System.Drawing.Color ToolStripDropDownBackground => C(44, 44, 46);
        public override System.Drawing.Color MenuItemSelected => C(10, 100, 216);
        public override System.Drawing.Color MenuItemBorder => C(10, 100, 216);
        public override System.Drawing.Color MenuBorder => C(58, 58, 60);
        public override System.Drawing.Color SeparatorDark => C(72, 72, 74);
        public override System.Drawing.Color ImageMarginGradientBegin => C(44, 44, 46);
        public override System.Drawing.Color ImageMarginGradientMiddle => C(44, 44, 46);
        public override System.Drawing.Color ImageMarginGradientEnd => C(44, 44, 46);
    }
}
