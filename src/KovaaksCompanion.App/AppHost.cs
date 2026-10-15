﻿using System.Diagnostics;
using System.IO;
using System.Windows;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Benchmarks;
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
    RecordingService? _video;
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
        Live = new LiveSession(_settings.ArchiveDbFile);
        var loaded = _settings;
        _settings = loaded.WithDetectedPaths(); // the default data root depends on where KovaaK's is
        _settingsDirty = _settings != loaded;
        AppLog.FilePath = _settings.LogFile;
        _settings.MigrateLocalState();
        FolderAccess.Register(); _uiFile = _settings.UiFile; Ui = UiState.Load(_uiFile);
        L.Set(_settings.Language);
        Theme.SetCustom(_settings.CustomHue, _settings.CustomSaturation, _settings.CustomBrightness);
        Theme.Apply(_settings.Theme);
        // opening the archive database checks it and imports the earlier files, which can take a while: not before the window
        var archiveDb = _settings.ArchiveDbFile;
        _ = Task.Run(() =>
        {
            var ids = RunSessionIds.Load(archiveDb);
            if (PlaySession.RunSessionIds.Count == 0) PlaySession.RunSessionIds = ids;
            // the favorites move into the archive database; the views redraw once they are loaded
            _app.Dispatcher.BeginInvoke(() => { if (Ui.UseArchive(archiveDb, _uiFile)) UiChanged?.Invoke(); });
        });
        ErrorReporter.Configure(Http, App.CurrentVersion.ToString(3), () => _settings.SendErrorReports);
    }
    readonly string _uiFile;
    readonly bool _settingsDirty;

    public AppSettings Settings => _settings;
    /// <summary>True while the game window is being recorded.</summary>
    public bool GameActive => _video?.IsRecording == true;
    /// <summary>The replays: info and data in the archive, videos in the videos folder.</summary>
    public ReplayArchive Replays => _settings.OpenReplays();
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
    /// <summary>Raised on a thread-pool thread after a replay is kept.</summary>
    public event Action<ReplayInfo>? ReplaySaved;
    /// <summary>Raised on a thread-pool thread as soon as a run's stats CSV is complete (before the replay clip is saved).</summary>
    public event Action? RunFinished;
    /// <summary>Raised on a thread-pool thread when <see cref="GameActive"/> may have changed.</summary>
    public event Action? GameStateChanged;

    /// <summary>The embedded libobs runtime, unpacked and complete. Null when it cannot be extracted.</summary>
    static string? _libobsRoot;

    public static string? ResolveLibobs()
    {
        // Ensure hashes the whole embedded zip: do it once per process, not on every replay that opens
        if (_libobsRoot != null) return _libobsRoot;
        var root = ObsBundle.Ensure(() => typeof(AppHost).Assembly.GetManifestResourceStream("libobs.zip"));
        AppLog.Write("obs", $"resolved: {root ?? "(none)"}");
        return _libobsRoot = root;
    }

    /// <summary>Starts libobs (idempotent) so its device lists work; false when it is unavailable.</summary>
    public static bool EnsureObs()
    {
        if (ObsRuntime.IsStarted) return true;
        var root = ResolveLibobs();
        return root != null && ObsRuntime.Start(root);
    }

    readonly object _bestGate = new();
    ScoreBook? _pbBook;
    readonly Dictionary<string, double> _best = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The score book the PB badge uses: local runs, archived runs and cloud scores, read once on the first finished run.</summary>
    ScoreBook LoadPbBook()
    {
        var store = _settings.OpenStore();
        var (library, archive) = RunLibrary.Load(store, new Dictionary<string, int>(Ui.DpiFixes));
        var book = new ScoreBook(library);
        foreach (var (scenario, cached) in archive.Scores()) book.SetCloud(scenario, cached.Scores);
        return book;
    }

    /// <summary>True when the run beats every earlier record of its scenario, local or cloud (the PB badge's rule). The records are read once; later runs update the map.</summary>
    bool IsPersonalBest(KovaaksCompanion.Core.Stats.RunStats run)
    {
        lock (_bestGate)
        {
            _pbBook ??= LoadPbBook();
            if (!_best.TryGetValue(run.Scenario, out var best)) best = _best[run.Scenario] = _pbBook.For(run.Scenario).BestAsOf(run.Start) ?? double.NegativeInfinity;
            var pb = run.Score > best;
            if (pb) { _best[run.Scenario] = run.Score; Live.NotePersonalBest(run.Scenario, run.End); }
            return pb;
        }
    }

    /// <summary>The tier name the run's score reaches, from the bundled thresholds of a favorite playlist that has the scenario (else the first that does); null when none or below tier 1.</summary>
    string? TierNameOf(KovaaksCompanion.Core.Stats.RunStats run)
    {
        try
        {
            var hits = BenchmarkCatalog.All.SelectMany(b => b.Difficulties).DistinctBy(d => d.KovaaksBenchmarkId)
                .Select(d => (d.KovaaksBenchmarkId, Data: TierData.Get(d.KovaaksBenchmarkId)))
                .Where(x => x.Data != null)
                .Select(x => (x.KovaaksBenchmarkId, x.Data, Sc: x.Data!.Categories.SelectMany(c => c.Scenarios)
                    .FirstOrDefault(s => s.TierThresholds.Count > 0 && s.Name.Equals(run.Scenario, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.Sc != null)
                .OrderByDescending(x => Ui.IsFavorite($"{x.KovaaksBenchmarkId}"))
                .FirstOrDefault();
            if (hits.Sc == null) return null;
            var tier = TierLadder.TierOf(run.Score, hits.Sc.TierThresholds).Tier;
            return tier > 0 && tier < hits.Data!.TierNames.Count ? hits.Data.TierNames[tier] : null;
        }
        catch (Exception e) { AppLog.Write("error", $"tier lookup failed: {e.Message}"); return null; }
    }

    void SetError(string m) { _lastError = m; AppLog.Write("error", m); }

    readonly object _cleanGate = new();

    /// <summary>
    /// Deletes the videos of the replays <paramref name="pick"/> chooses (never favorite runs or runs of favorite sessions, nor personal bests when kept); the rest of each replay stays.
    /// Right after a save it runs before the views reload, so they never list a video that is about to go. Returns true when something was deleted.
    /// </summary>
    bool DeleteVideos(Func<List<StoredReplay>, Func<ReplayInfo, bool>, List<StoredReplay>> pick)
    {
        try
        {
            lock (_cleanGate)
            {
                var s = _settings;
                var favs = _app.Dispatcher.Invoke(() => Ui.FavoriteRunKeys());
                var archive = s.OpenReplays();
                var all = StorageCleaner.Scan(archive);
                var pbs = s.KeepPersonalBests ? StorageCleaner.PersonalBests(all, s.OpenStore().LoadAllScores().ToDictionary(kv => kv.Key, kv => kv.Value.Scores)).Select(p => UiState.RunKey(p.Info.Scenario, p.Info.End)).ToHashSet() : [];
                bool Keep(ReplayInfo i) => favs.Contains(UiState.RunKey(i.Scenario, i.End)) || pbs.Contains(UiState.RunKey(i.Scenario, i.End));
                var n = StorageCleaner.Delete(pick(all, Keep), archive);
                if (n > 0) AppLog.Write("storage", $"deleted {n} video(s)");
                return n > 0;
            }
        }
        catch (Exception e) { AppLog.Write("storage", "cleanup failed: " + e.Message); return false; }
    }

    /// <summary>
    /// A replay was saved: deletes the videos of its live session that a later run of the same scenario beat (see <see cref="LiveSession.Saved"/>).
    /// Other sessions are never touched.
    /// </summary>
    void DeleteBeatenRuns(ReplayInfo saved)
    {
        try
        {
            lock (_cleanGate)
            {
                var s = _settings;
                var favs = s.DeleteBeatenRuns ? _app.Dispatcher.Invoke(() => Ui.FavoriteRunKeys()) : [];
                var victims = Live.Saved(saved, s.DeleteBeatenRuns, i => favs.Contains(UiState.RunKey(i.Scenario, i.End)), s.KeepPersonalBests);
                if (victims.Count == 0) return;
                var n = StorageCleaner.Delete(victims, s.OpenReplays());
                if (n > 0) AppLog.Write("storage", $"deleted {n} beaten video(s)");
            }
        }
        catch (Exception e) { AppLog.Write("storage", "beaten runs cleanup failed: " + e.Message); }
    }

    /// <summary>Storage limits: videos older than the maximum age, then the oldest while over the size limit.</summary>
    bool EnforceStorageLimits()
    {
        var s = _settings;
        if (s.StorageLimitGb <= 0 && s.MaxAgeDays <= 0) return false;
        return DeleteVideos((all, keep) =>
        {
            var victims = StorageCleaner.Expired(all, s.MaxAgeDays, DateTime.Now, keep);
            victims.AddRange(StorageCleaner.OverLimit(all.Except(victims).ToList(), StorageCleaner.LimitBytes(s.StorageLimitGb), keep));
            return victims;
        });
    }

    /// <summary>
    /// Imports the replay folders of earlier versions (see <see cref="ReplayArchive.MigrateLegacy"/>). True when any was imported.
    /// </summary>
    bool MigrateReplays()
    {
        try
        {
            var archive = _settings.OpenReplays();
            var roots = new[] { _settings.ReplaysFolder, _settings.ArchiveReplaysFolder };
            var legacy = archive.LegacyInfos(roots);
            if (legacy.Count == 0) return false; // the usual case: nothing to read, no session ids to load
            var n = archive.MigrateLegacy(ReplaySessions.FolderOf(archive.List().Concat(legacy), RunSessionIds.Load(_settings.ArchiveDbFile)), roots);
            if (n > 0) AppLog.Write("archive", $"imported {n} replay folder(s)");
            return n > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("archive", "importing replays failed: " + e.Message); return false; }
    }

    /// <summary>
    /// The play session a finished run belongs to: the open live session, else the one <see cref="ReplaySessions.Of"/> finds. It is recorded for the run.
    /// </summary>
    (DateTime Since, Guid Id) SessionOf(KovaaksCompanion.Core.Stats.RunStats run)
    {
        if (Live.Id is { } live) return (Live.Since, live);
        var ids = PlaySession.RunSessionIds;
        var session = ReplaySessions.Of(run, ids, _settings.OpenReplays().LastEndedBy);
        PlaySession.RunSessionIds = RunSessionIds.Add(ids, run.Scenario, run.End, session.Id);
        return session;
    }

    /// <summary>
    /// Moves the videos that lie directly in the videos folder into a folder per play session. A run without a session id keeps its video where it is
    /// until it has one (the stats page gives every run one). True when any was moved.
    /// </summary>
    bool MoveVideosIntoSessions()
    {
        if (_settings.VideoByRun) return false;
        try
        {
            var archive = _settings.OpenReplays();
            var renamed = archive.RenameOldSessionFolders();
            if (renamed > 0) AppLog.Write("archive", $"renamed {renamed} session folder(s)");
            var n = archive.MoveVideosIntoSessions(ReplaySessions.FolderOf(archive.List(), RunSessionIds.Load(_settings.ArchiveDbFile)));
            if (n > 0) AppLog.Write("archive", $"moved {n} video(s) into session folders");
            return n > 0 || renamed > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("archive", "sorting videos into sessions failed: " + e.Message); return false; }
    }

    /// <summary>
    /// Keeps the statistics of the replays in the run archive (all of them, or only <paramref name="only"/>), so they count even when the stats folder loses the run.
    /// Runs the archive already holds statistics of are skipped.
    /// </summary>
    void CacheReplays(ReplayInfo? only = null)
    {
        try
        {
            var store = _settings.OpenStore();
            var archive = _settings.OpenReplays();
            var have = only == null ? store.ArchivedKeys() : new HashSet<(string, DateTime)>();
            var replays = only != null ? [only] : archive.List().Where(i => !have.Contains((i.Scenario.ToLowerInvariant(), i.End))).ToList();
            store.Archive(replays.Select(archive.ReadRecord).OfType<RunRecord>().ToList());
            if (only != null) return;
            // runs archived before pauses, reloads and distance were kept: fill those in from the replay's data
            var lacking = store.ArchivedRuns().Where(r => r.Reloads is null).Select(r => (r.Scenario.ToLowerInvariant(), r.End)).ToHashSet();
            if (lacking.Count > 0)
                store.AddRunFigures(archive.List().Where(i => lacking.Contains((i.Scenario.ToLowerInvariant(), i.End))).Select(archive.ReadRecord).OfType<RunRecord>().Where(r => r.Reloads != null).ToList());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("archive", "keeping replays failed: " + e.Message); }
    }

    /// <summary>Moves runs played while the app was not running from the stats folder into the archive. True when it added any.</summary>
    bool IngestStats()
    {
        try
        {
            var n = StatsIngest.Run(_settings.StatsFolder, _settings.OpenStore());
            if (n > 0) AppLog.Write("archive", $"took in {n} run(s) from the stats folder");
            return n > 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write("archive", "reading the stats folder failed: " + e.Message); return false; }
    }

    /// <summary>Deletes the run's video and keeps the rest of its replay and its statistics.</summary>
    public async Task<bool> DeleteRun(ReplayInfo info)
    {
        var archive = _settings.OpenReplays();
        if (!await Task.Run(() => archive.DeleteVideo(info))) return false;
        RunFinished?.Invoke();
        return true;
    }

    public void Start(bool showMain)
    {
        AppLog.Write("app", $"start: version {typeof(AppHost).Assembly.GetName().Version}, OS {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({Environment.OSVersion.Version}), 64-bit={Environment.Is64BitProcess}");
        var detected = _settings.WithDetectedPaths();
        if (detected != _settings || _settingsDirty)
        {
            _settings = detected;
            AppLog.FilePath = _settings.LogFile;
            SaveSettings();
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

        _saver = new ReplaySaver(_settings.OpenReplays(), _buffer, ClockAnchor.Now,
            (s, e, p, prog) => _video?.ExtractClipAsync(s, e, p, prog) ?? Task.FromResult<ClipResult?>(null),
            IsPersonalBest, SessionOf, () => _settings.VideoPbOnly, TierNameOf, () => _settings.VideoByRun);
        _saver.Error += SetError;
        _saver.Saved += info =>
        {
            lock (_pending)
            {
                _pending.RemoveAll(p => p.Scenario.Equals(info.Scenario, StringComparison.OrdinalIgnoreCase) && Math.Abs((p.End - info.End).TotalSeconds) < ReplayStore.RunMatchTolerance.TotalSeconds);
            }
            AppLog.Write("replay", $"saved {info.Id:N}");
            CacheReplays(info);
            DeleteBeatenRuns(info); // before the views reload, so they never list a run that is about to go
            ReplaySaved?.Invoke(info);
        };

        StartWatcher();

        BuildTray();
        Autostart.Refresh(_settings.StartWithWindows);
        _ = Task.Run(() => { var moved = MigrateReplays() | MoveVideosIntoSessions(); CacheReplays(); if (EnforceStorageLimits() | IngestStats() | moved) RunFinished?.Invoke(); });
        _ = Task.Run(() => { try { _settings.OpenStore().BackupArchive(); } catch (IOException e) { AppLog.Write("archive", "backup failed: " + e.Message); } });
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
        _video = new RecordingService(new VideoOptions
        {
            ObsRoot = obsRoot, BufferDir = _settings.BufferFolder, Quality = _settings.VideoQuality.ToCq(), MaxHeight = _settings.VideoQuality.MaxHeight(),
            Fps = _settings.VideoFps,
        }, () => WebcamConfig.From(_settings));
        _video.Error += m => { SetError("video: " + m); ErrorReporter.Report("video", new VideoProblem(m)); };
        _video.GameLaunched += Live.Launch;
        _video.GameExited += () => _ = Live.Exit();
        _video.StateChanged += () => GameStateChanged?.Invoke();
        _video.GameSized += (w, h) => { if (w != _settings.GameWidth || h != _settings.GameHeight) ApplySettings(_settings with { GameWidth = w, GameHeight = h }); };
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

    void SaveSettings()
    {
        try { _settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { SetError("settings not saved: " + AppSettings.DefaultFile + ": " + e.Message); }
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
        SaveSettings();
        PreflightAccess();
        var customChanged = s.Theme == "Custom" && (s.CustomHue != old.CustomHue || s.CustomSaturation != old.CustomSaturation || s.CustomBrightness != old.CustomBrightness);
        if (s.Language != old.Language || s.Theme != old.Theme || customChanged)
        {
            L.Set(s.Language);
            Theme.SetCustom(s.CustomHue, s.CustomSaturation, s.CustomBrightness);
            Theme.Apply(s.Theme);
            // A new language needs every text rebuilt, and Black has no Mica backdrop (set when the window opens): those open a new window. Other theme changes redraw in place.
            var newWindow = s.Language != old.Language || s.Theme == "Black" || old.Theme == "Black";
            _app.Dispatcher.BeginInvoke(newWindow ? RebuildMain : () => _main?.RefreshTheme());
        }
        else
        {
            if (s.UiScale != old.UiScale) _app.Dispatcher.Invoke(() => _main?.ApplyScale(s.UiScale));
            if (s.Grain != old.Grain) _app.Dispatcher.Invoke(() => _main?.ApplyGrain(s.Grain));
        }
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
