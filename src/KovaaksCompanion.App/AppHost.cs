using System.Diagnostics;
using System.IO;
using System.Windows;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Input;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Replays;
using KovaaksCompanion.Core.Update;
using KovaaksCompanion.Core.Video;
using KovaaksCompanion.Core.Video.Obs;
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
    ReplaySaver _saver = null!;
    WinForms.NotifyIcon? _tray;
    System.Drawing.Icon? _trayIcon;
    static System.Drawing.Icon LoadTrayIcon() { using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream; return new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize); }
    MainWindow? _main;
    string _lastError = "";
    ReleaseInfo? _pendingUpdate;
    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    sealed class Pending(string scenario, DateTime end) { public string Scenario = scenario; public DateTime End = end; public double Progress; }
    readonly List<Pending> _pending = [];
    /// <summary>The play session of the running game.</summary>
    public LiveSession Live { get; }

    public AppHost(Application app)
    {
        _app = app;
        Live = new LiveSession(_settings.LiveRunFile);
        var loaded = _settings;
        _settings = loaded.WithDetectedPaths(); // the default data root depends on where KovaaK's is
        _settingsDirty = _settings != loaded;
        AppLog.FilePath = _settings.LogFile;
        _settings.MigrateLocalState();
        FolderAccess.Register(); _uiFile = _settings.UiFile; Ui = UiState.Load(_uiFile);
        L.Set(_settings.Language);
        Theme.Apply(_settings.Theme);
        PlaySession.LiveIds = LiveRunLog.Load(_settings.LiveRunFile);
        ErrorReporter.Configure(Http, App.CurrentVersion.ToString(3), () => _settings.SendErrorReports);
    }
    readonly string _uiFile;
    readonly bool _settingsDirty;

    public AppSettings Settings => _settings;
    /// <summary>True while the game window is being recorded.</summary>
    public bool GameActive => _video?.IsRecording == true;
    public string ReplaysFolder => _settings.ReplaysFolder;
    public string CachedReplaysFolder => _settings.CachedReplaysFolder;
    /// <summary>Checks if a run's replay is currently being saved.</summary>
    public bool IsPending(string scenario, DateTime end) => PendingProgress(scenario, end) >= 0;

    /// <summary>Encoding progress 0..1 of a run's clip, or -1 when it is not being saved.</summary>
    public double PendingProgress(string scenario, DateTime end)
    {
        lock (_pending)
        {
            return _pending.FirstOrDefault(p => p.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - end).TotalSeconds) < ReplayStore.RunMatchTolerance.TotalSeconds)?.Progress ?? -1;
        }
    }

    /// <summary>Raised on a thread-pool thread as a pending run's clip encodes (scenario, run end, 0..1).</summary>
    public event Action<string, DateTime, double>? EncodeProgress;
    /// <summary>Favorites and chart selection. Mutate on the UI thread, then call <see cref="SaveUi"/>.</summary>
    public UiState Ui { get; }
    /// <summary>Raised on the calling (UI) thread after <see cref="SaveUi"/>.</summary>
    public event Action? UiChanged;
    /// <summary>Raised on a thread-pool thread after a replay folder is complete.</summary>
    public event Action<ReplayInfo, string>? ReplaySaved;
    /// <summary>Raised on a thread-pool thread as soon as a run's stats CSV is complete (before the replay clip is saved).</summary>
    public event Action? RunFinished;
    /// <summary>Raised on a thread-pool thread when <see cref="GameActive"/> may have changed.</summary>
    public event Action? GameStateChanged;

    /// <summary>The embedded libobs runtime, unpacked and complete. Null when it cannot be extracted.</summary>
    public static string? ResolveLibobs()
    {
        var root = ObsBundle.Ensure(() => typeof(AppHost).Assembly.GetManifestResourceStream("libobs.zip"));
        AppLog.Write("obs", $"resolved: {root ?? "(none)"}");
        return root;
    }

    /// <summary>Starts libobs (idempotent) so its device lists work; false when it is unavailable.</summary>
    public static bool EnsureObs()
    {
        if (ObsRuntime.IsStarted) return true;
        var root = ResolveLibobs();
        return root != null && ObsRuntime.Start(root);
    }

    readonly object _bestGate = new();
    Dictionary<string, double>? _best;

    /// <summary>True when the run beats every earlier run of its scenario in the stats folder (same rule as the PB badge). The stats folder is scanned once; later runs update the map.</summary>
    bool IsPersonalBest(KovaaksCompanion.Core.Stats.RunStats run)
    {
        lock (_bestGate)
        {
            _best ??= KovaaksCompanion.Core.Library.RunLibrary.Scan(_settings.StatsFolder).AllRuns
                .Where(r => r.Start < run.Start)
                .GroupBy(r => r.Scenario, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Max(r => r.Score), StringComparer.OrdinalIgnoreCase);
            var pb = !_best.TryGetValue(run.Scenario, out var best) || run.Score > best;
            if (pb) { _best[run.Scenario] = run.Score; Live.NotePersonalBest(run.Scenario, run.End); }
            return pb;
        }
    }

    void SetError(string m) { _lastError = m; AppLog.Write("error", m); }

    readonly object _cleanGate = new();

    /// <summary>
    /// Deletes the replay folders <paramref name="pick"/> chooses (never favorite runs, nor personal bests when kept) and keeps their statistics in the cache.
    /// Right after a save it runs before the views reload, so they never list a run that is about to go. Returns true when something was deleted.
    /// </summary>
    bool RetireReplays(Func<List<StoredReplay>, Func<ReplayInfo, bool>, List<StoredReplay>> pick)
    {
        try
        {
            lock (_cleanGate)
            {
                var s = _settings;
                var favs = _app.Dispatcher.Invoke(() => Ui.FavoriteRuns.ToHashSet());
                var cache = s.OpenCache();
                var all = StorageCleaner.Scan(s.ReplaysFolder);
                var pbs = s.KeepPersonalBests ? StorageCleaner.PersonalBests(all, cache.LoadAllScores().ToDictionary(kv => kv.Key, kv => kv.Value.Scores)).Select(p => UiState.RunKey(p.Info.Scenario, p.Info.End)).ToHashSet() : [];
                bool Keep(ReplayInfo i) => favs.Contains(UiState.RunKey(i.Scenario, i.End)) || pbs.Contains(UiState.RunKey(i.Scenario, i.End));
                var n = StorageCleaner.Delete(pick(all, Keep), cache, s.CachedReplaysFolder);
                if (n > 0) AppLog.Write("storage", $"deleted {n} replay folder(s)");
                return n > 0;
            }
        }
        catch (Exception e) { AppLog.Write("storage", "cleanup failed: " + e.Message); return false; }
    }

    /// <summary>
    /// A replay was saved: deletes the replays of its live session that a later run of the same scenario beat (see <see cref="LiveSession.Saved"/>).
    /// Other sessions are never touched.
    /// </summary>
    void DeleteBeatenRuns(ReplayInfo saved, string folder)
    {
        try
        {
            lock (_cleanGate)
            {
                var s = _settings;
                var favs = s.DeleteBeatenRuns ? _app.Dispatcher.Invoke(() => Ui.FavoriteRuns.ToHashSet()) : [];
                var victims = Live.Saved(saved, folder, s.DeleteBeatenRuns, i => favs.Contains(UiState.RunKey(i.Scenario, i.End)), s.KeepPersonalBests);
                if (victims.Count == 0) return;
                var n = StorageCleaner.Delete(victims, s.OpenCache(), s.CachedReplaysFolder);
                if (n > 0) AppLog.Write("storage", $"deleted {n} beaten replay folder(s)");
            }
        }
        catch (Exception e) { AppLog.Write("storage", "beaten runs cleanup failed: " + e.Message); }
    }

    /// <summary>Storage limits: replays older than the maximum age, then the oldest while over the size limit.</summary>
    bool EnforceStorageLimits()
    {
        var s = _settings;
        if (s.StorageLimitGb <= 0 && s.MaxAgeDays <= 0) return false;
        return RetireReplays((all, keep) =>
        {
            var victims = StorageCleaner.Expired(all, s.MaxAgeDays, DateTime.Now, keep);
            victims.AddRange(StorageCleaner.OverLimit(all.Except(victims).ToList(), StorageCleaner.LimitBytes(s.StorageLimitGb), keep));
            return victims;
        });
    }

    /// <summary>
    /// Keeps the statistics of replay folders in the cache (all of them, or only <paramref name="only"/>), so they still count once the folder is deleted.
    /// Runs the cache already holds statistics of are skipped.
    /// </summary>
    void CacheReplays(string? only = null)
    {
        try
        {
            var cache = _settings.OpenCache();
            var have = only == null ? cache.ArchivedRuns().Select(r => (r.Scenario.ToLowerInvariant(), r.End)).ToHashSet() : new HashSet<(string, DateTime)>();
            var replays = only != null ? new List<(string Folder, ReplayInfo Info)> { (only, ReplayStore.ReadInfo(only)) }
                : ReplayStore.List(_settings.ReplaysFolder).Where(x => !have.Contains((x.Info.Scenario.ToLowerInvariant(), x.Info.End))).Select(x => (x.Folder, x.Info)).ToList();
            // a score that differs from the signed one means the stats CSV was edited; never keep it
            var records = replays.Select(x => ReplayStore.ReadRecord(x.Folder)).Zip(replays, (r, x) => (r, x.Info))
                .Where(t => t.r != null && Math.Abs(t.r.Score - t.Info.Score) < 1e-6).Select(t => t.r!).ToList();
            cache.Archive(records);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("cache", "caching replays failed: " + e.Message); }
    }

    /// <summary>Deletes the run's video and keeps the rest of its replay (trajectory, perf, CSV, info) and its statistics in the cache.</summary>
    public async void DeleteRun(string scenario, DateTime end, double score)
    {
        var s = _settings;
        try
        {
            await Task.Run(() =>
            {
                ReplayStore.Delete(s.ReplaysFolder, s.CachedReplaysFolder, scenario, end, out var record);
                s.OpenCache().Archive(record);
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        RunFinished?.Invoke();
    }

    public void Start(bool showMain)
    {
        AppLog.Write("app", $"start: version {typeof(AppHost).Assembly.GetName().Version}, OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), 64-bit={Environment.Is64BitProcess}");
        var detected = _settings.WithDetectedPaths();
        if (detected != _settings || _settingsDirty)
        {
            _settings = detected;
            AppLog.FilePath = _settings.LogFile;
            try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetError("settings not saved: " + AppSettings.DefaultFile + ": " + e.Message); }
        }
        PreflightAccess();
        BufferCleaner.Clean(_settings.BufferFolder); // leftovers of a crash; nothing records yet
        _anchor = ClockAnchor.Now();
        _buffer = new MouseRingBuffer(new VideoOptions().BufferMinutes * 60.0, _anchor.Frequency, _anchor.Qpc);
        StartCapture();

        // ResolveLibobs hashes the whole embedded libobs zip (and unpacks it on first run): off the UI thread, the window must not wait for it
        _ = Task.Run(() =>
        {
            var libobs = ResolveLibobs();
            if (libobs != null) StartVideo(libobs);
            else SetError("libobs missing (video disabled)");
        });

        _saver = new ReplaySaver(_settings.ReplaysFolder, _buffer, ClockAnchor.Now,
            (s, e, p, prog) => _video?.ExtractClipAsync(s, e, p, prog) ?? Task.FromResult<ClipResult?>(null),
            IsPersonalBest, () => _settings.VideoPbOnly);
        _saver.Error += SetError;
        _saver.Saved += (info, folder) =>
        {
            lock (_pending)
            {
                _pending.RemoveAll(p => p.Scenario.Equals(info.Scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - info.End).TotalSeconds) < ReplayStore.RunMatchTolerance.TotalSeconds);
            }
            AppLog.Write("replay", $"saved {folder}");
            CacheReplays(folder);
            DeleteBeatenRuns(info, folder); // before the views reload, so they never list a run that is about to go
            ReplaySaved?.Invoke(info, folder);
        };

        StartWatcher();

        BuildTray();
        Autostart.Refresh(_settings.StartWithWindows);
        _ = Task.Run(() => { CacheReplays(); if (EnforceStorageLimits()) RunFinished?.Invoke(); });
        if (showMain) ShowMain("Stats");
        _ = CheckForUpdate();
    }

    /// <summary>Starts the stats folder watcher; also restarts it after the folder access was granted.</summary>
    void StartWatcher()
    {
        _watcher = new StatsWatcher(_settings.StatsFolder, DateTime.Now);
        _watcher.RunFinished += (run, csv) =>
        {
            AppLog.Write("replay", $"run finished: {csv}");
            Live.Tag(run.Scenario, run.End);
            var item = new Pending(run.Scenario, run.End);
            lock (_pending) { _pending.Add(item); }
            RunFinished?.Invoke(); // the run shows now, with a progress ring until the clip is saved
            _ = _saver.Enqueue(run, csv, f => { item.Progress = f; EncodeProgress?.Invoke(item.Scenario, item.End, f); }).ContinueWith(_ =>
            {
                lock (_pending)
                {
                    if (_pending.RemoveAll(p => p.Scenario.Equals(run.Scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - run.End).TotalSeconds) < ReplayStore.RunMatchTolerance.TotalSeconds) > 0)
                    {
                        RunFinished?.Invoke();
                    }
                }
            });
        };
        _watcher.AccessDenied += folder =>
        {
            if (!AccessGuard.Grant(folder, false)) { SetError("stats folder: access denied: " + folder); return; }
            AppLog.Write("stats", "read access granted: " + folder);
            _app.Dispatcher.Invoke(() => { _watcher?.Dispose(); StartWatcher(); });
        };
        try { _watcher.Start(); } catch (Exception e) { SetError("stats folder: " + e.Message); }
    }

    /// <summary>One background release check per run; a newer release is offered in the main window, now or when it next opens.</summary>
    async Task CheckForUpdate()
    {
        try
        {
            var latest = await UpdateCheck.Fetch(Http);
            if (!UpdateCheck.IsNewer(latest, App.CurrentVersion)) return;
            _pendingUpdate = latest;
            UpdateAvailable = true;
            _app.Dispatcher.Invoke(OfferUpdate);
        }
        catch (Exception e) { AppLog.Write("update", "check failed: " + e.Message); }
    }

    /// <summary>Opens the update popup for a release found by a manual check.</summary>
    public void ShowUpdate(KovaaksCompanion.Core.Update.ReleaseInfo release) => _app.Dispatcher.Invoke(() => { UpdateAvailable = true; _main?.ShowUpdate(release); });

    /// <summary>A newer release than the running one was found this run.</summary>
    public bool UpdateAvailable { get; private set; }

    void OfferUpdate()
    {
        if (_main == null || _pendingUpdate == null) return;
        var r = _pendingUpdate; _pendingUpdate = null;
        _main.ShowUpdate(r);
    }


    void StartVideo(string obsRoot)
    {
        if (_video != null) return;
        _video = new VideoCaptureService(new VideoOptions
        {
            ObsRoot = obsRoot, BufferDir = _settings.BufferFolder, Quality = _settings.VideoQuality.ToCq(), MaxHeight = _settings.VideoQuality.MaxHeight(),
            Fps = _settings.VideoFps,
        }, () => HandCamConfig.From(_settings));
        _video.Error += m => { SetError("video: " + m); ErrorReporter.Report("video", new VideoProblem(m)); };
        _video.GameLaunched += Live.Launch;
        _video.GameExited += () => _ = Live.Exit();
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
        menu.Items.Add(L.T("Open"), null, (_, _) => ShowMain());
        menu.Items.Add(L.T("Exit"), null, (_, _) => _app.Dispatcher.Invoke(_app.Shutdown));

        _tray = new WinForms.NotifyIcon
        {
            Icon = _trayIcon = LoadTrayIcon(), Text = "KovaaK's Companion", Visible = true, ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowMain(); };
        if (_lastError.Length > 0) _tray.ShowBalloonTip(5000, "KovaaK's Companion", _lastError, WinForms.ToolTipIcon.Warning);
    }

    void RebuildTray()
    {
        if (_tray?.ContextMenuStrip is not { } m || m.Items.Count < 2) return;
        m.Items[0].Text = L.T("Open"); m.Items[1].Text = L.T("Exit");
    }

    public void ShowMain(string? page = null) => _app.Dispatcher.Invoke(() =>
    {
        if (_main == null)
        {
            OpenMain();
        }
        else { if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal; _main.Activate(); }
        if (page != null) _main?.ShowPage(page);
        OfferUpdate();
    });

    void OpenMain(Rect? bounds = null, WindowState state = WindowState.Normal)
    {
        var w = _main = new MainWindow(this);
        w.Closed += (_, _) => { if (_main == w) _main = null; };
        if (bounds is { } b) { w.WindowStartupLocation = WindowStartupLocation.Manual; w.Left = b.Left; w.Top = b.Top; w.Width = b.Width; w.Height = b.Height; }
        if (state == WindowState.Maximized) w.WindowState = state;
        w.Show();
    }

    /// <summary>Replaces the main window with a fresh one at the same place, so every view is built with the new language, theme and accent.</summary>
    void RebuildMain()
    {
        var old = _main;
        if (old == null) return;
        var bounds = old.WindowState == WindowState.Normal ? new Rect(old.Left, old.Top, old.Width, old.Height) : old.RestoreBounds;
        var state = old.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        OpenMain(bounds, state);
        old.Close();
        _main!.ShowPage("Settings");
        RebuildTray();
    }

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

    /// <summary>Opens the replay of a past run; false when that run was not recorded, unless <paramref name="withoutRecording"/> opens it selected anyway.</summary>
    public bool ShowRun(string scenario, DateTime end, bool withoutRecording = false) => _app.Dispatcher.Invoke(() =>
    {
        ShowMain();
        return _main!.ShowRun(scenario, end, withoutRecording);
    });

    public void SaveUi()
    {
        try { Ui.Save(_uiFile); } catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { SetError("ui state: " + e.Message); }
        UiChanged?.Invoke();
    }

    /// <summary>Checks every folder the app uses and fixes all blocked ones with one UAC prompt.</summary>
    void PreflightAccess()
    {
        var blocked = AccessGuard.Preflight(
        [
            (_settings.DataRoot, true),
            (AppSettings.DefaultDataFolder, true),
            (_settings.KovaaksPath.Trim().Length > 0 ? _settings.StatsFolder : "", false),
        ]);
        foreach (var b in blocked) SetError("access denied: " + b);
    }

    public void ApplySettings(AppSettings s)
    {
        var old = _settings;
        _settings = s;
        AppLog.FilePath = s.LogFile;
        try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetError("settings not saved: " + AppSettings.DefaultFile + ": " + e.Message); }
        PreflightAccess();
        if (s.Language != old.Language || s.Theme != old.Theme)
        {
            L.Set(s.Language);
            Theme.Apply(s.Theme);
            _app.Dispatcher.BeginInvoke(RebuildMain);
        }
        else if (s.UiScale != old.UiScale) _app.Dispatcher.Invoke(() => _main?.ApplyScale(s.UiScale));
        if (s.StorageLimitGb != old.StorageLimitGb || s.MaxAgeDays != old.MaxAgeDays)
            _ = Task.Run(() => { CacheReplays(); if (EnforceStorageLimits()) RunFinished?.Invoke(); });
    }

    public void Dispose()
    {
        Live.Flush();
        _tray?.Dispose();
        _trayIcon?.Dispose();
        _watcher?.Dispose();
        _capture?.Dispose();
        var stopped = _video?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(8)) ?? true;
        if (stopped) BufferCleaner.Clean(_settings.BufferFolder); // libobs is shut down, so its chunks are junk
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
