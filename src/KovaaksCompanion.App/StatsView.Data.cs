using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>Server data: the player's server bests (fetched once, cached) and cloud score history. Playlist progress is calculated locally from the bundled thresholds.</summary>
public partial class StatsView
{
    const int MaxParallel = 4;

    readonly CancellationTokenSource _cts = new();
    StatsStore? _store;
    bool _syncing;
    DispatcherTimer? _refreshTimer;

    StatsStore Store => _store ??= _host.Settings.OpenStore();

    static IEnumerable<(Benchmark B, Difficulty D)> AllDifficulties() =>
        BenchmarkCatalog.All.SelectMany(b => b.Difficulties.Select(d => (B: b, D: d))).DistinctBy(x => x.D.KovaaksBenchmarkId);

    /// <summary>Stores a difficulty's progress and feeds the server-best lookups.</summary>
    void ApplyProgress(Difficulty d, BenchmarkProgress p)
    {
        _rawProgress[d.KovaaksBenchmarkId] = p;
        _progress[d.KovaaksBenchmarkId] = WithLocal(p);
        foreach (var s in p.Scenarios) if (s.Score > 0) _book.RaiseKnownBest(s.Scenario, Math.Max(s.Score, _archivedBest.GetValueOrDefault(s.Scenario)));
    }

    /// <summary>Builds every difficulty's progress from the bundled thresholds and the best scores in the archive.</summary>
    void RebuildProgress()
    {
        foreach (var (_, d) in AllDifficulties())
            if (BenchmarkProgress.FromBundle(d, n => _archivedBest.TryGetValue(n, out var b) ? b : null) is { } p) ApplyProgress(d, p);
    }

    /// <summary>The server progress with the local bests folded in, so tiers reflect runs the server does not have yet.</summary>
    BenchmarkProgress WithLocal(BenchmarkProgress p) =>
        p.WithLocalBests(n =>
        {
            double? local = _book.For(n).Best is > 0 and var best ? best : null;
            return _archivedBest.TryGetValue(n, out var d) && (local == null || d > local) ? d : local;
        });

    /// <summary>Re-applies the local bests after the run library changed.</summary>
    void ReapplyLocal()
    {
        foreach (var (id, raw) in _rawProgress) _progress[id] = WithLocal(raw);
    }

    static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Downloads the player's best score of every scenario once, the first time the username is known, and keeps it in the archive.
    /// After that new plays come from the local library and from the last scores of the scenarios that are opened.
    /// </summary>
    async Task SyncCloudAsync()
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0 || _syncing) return;
        _syncing = true;
        var ct = _cts.Token;
        try
        {
            var store = Store;
            var synced = await Task.Run(store.CloudBestsSynced);
            var user = _host.Settings.UsernameFor(steam);
            if (string.IsNullOrEmpty(user))
            {
                ShowSync(0, 0);
                var likely = AllDifficulties().Select(x => x.D)
                    .OrderByDescending(d => KovaaksCompanion.Core.Benchmarks.TierData.Get(d.KovaaksBenchmarkId) is { } b ? b.Categories.Sum(c => c.Scenarios.Count(s => _lib.Runs(s.Name).Count > 0)) : 0)
                    .Select(d => d.KovaaksBenchmarkId);
                user = await _api.ResolveUsernameAsync(steam, likely, 6, ct);
                if (string.IsNullOrEmpty(user)) return;
                _host.ApplySettings(_host.Settings with { WebUsername = user, WebUsernameSteamId = steam });
            }
            if (!synced)
            {
                ShowSync(0, 0);
                var bests = await _api.GetBestScoresAsync(user, ct);
                if (bests.Count == 0) return;
                await Task.Run(() => { try { store.SaveCloudBests(bests); } catch (IOException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("cloud bests save", e); } });
                foreach (var (n, v) in bests)
                {
                    if (v > _archivedBest.GetValueOrDefault(n)) _archivedBest[n] = v;
                    _book.RaiseKnownBest(n, v);
                }
                RebuildProgress();
            }
            var first = FirstFetchScenarios();
            if (first.Count > 0)
            {
                ShowSync(0, first.Count);
                await EnsureCloudAsync(first, new Progress<int>(done => ShowSync(done, first.Count)));
            }
            if (!synced || first.Count > 0)
            {
                // Reload ran before any server data was here: redo what it derived from it. The cards built then still show the empty progress, and RefreshHome only redraws the hero and the lists.
                ReapplyLocal();
                AssignSessionIds();
                ShowHome();
            }
            else ScheduleRefresh();
            if (_popupOpen && _popupItem is { } open) ShowBenchmark(open, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!ct.IsCancellationRequested) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("cloud load", e); } // offline or rate limited: the next reload tries again
        finally { _syncing = false; HideSync(); UpdateTierMarks(false); } // server-side changes are no celebration
    }

    /// <summary>The scenarios whose server scores were never fetched (a new or restored archive); the page waits for these.</summary>
    List<string> FirstFetchScenarios() =>
        _rawProgress.Values.SelectMany(p => p.Scenarios).Where(sc => sc.TierThresholds.Count > 0).Select(sc => sc.Scenario)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => !_everFetched.Contains(n) && (_archivedBest.ContainsKey(n) || _lib.Runs(n).Count > 0)).ToList();

    void ShowSync(int done, int total)
    {
        if (SyncOverlay.Visibility != Visibility.Visible)
        {
            SyncSpinHost.Child = Spinner.Create((Brush)FindResource("Accent"), 32);
            SyncOverlay.Visibility = Visibility.Visible;
        }
        SyncCount.Text = total > 0 ? $"{done} / {total}" : "";
    }

    void HideSync()
    {
        SyncOverlay.Visibility = Visibility.Collapsed;
        SyncSpinHost.Child = null; // stops the animation
    }

    /// <summary>Redraws the hero and the playlist lists at most every 400 ms while progress updates arrive in a burst (a debounce would starve the redraw until the whole burst ends, leaving favourite cards empty).</summary>
    void ScheduleRefresh()
    {
        _refreshTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        if (_refreshTimer.IsEnabled) return;
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    void OnRefreshTick(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        RefreshHome();
    }

    // Cloud score history, kept in the score book

    readonly Dictionary<string, DateTime> _cloudFetched = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _cloudInflight = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _everFetched = new(StringComparer.OrdinalIgnoreCase); // fetched at any time, however long ago

    /// <summary>Puts a scenario's cloud scores into the score book; Home is redrawn when they changed. Returns whether they changed.</summary>
    bool RememberCloud(string scenario, IReadOnlyList<CloudScore> scores)
    {
        if (!_book.SetCloud(scenario, scores)) return false;
        ScheduleRefresh();
        return true;
    }

    /// <summary>
    /// Brings the cloud scores of the scenarios up to date: only those never fetched are requested with at most
    /// <see cref="MaxParallel"/> requests at a time, cached and put into the score book. Scenarios the server has no plays
    /// of are skipped, and nothing is requested before the username is known. Returns whether any scores changed.
    /// </summary>
    async Task<bool> EnsureCloudAsync(IEnumerable<string> scenarios, IProgress<int>? progress = null)
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0) return false;
        var stale = scenarios.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => (_archivedBest.Count == 0 || _archivedBest.ContainsKey(n) || _lib.Runs(n).Count > 0) && !_cloudFetched.ContainsKey(n) && _cloudInflight.Add(n)).ToList();
        if (stale.Count == 0) return false;
        var ct = _cts.Token;
        var store = Store;
        var changed = false;
        var finished = 0;
        try
        {
            var user = _host.Settings.UsernameFor(steam);
            if (string.IsNullOrEmpty(user)) return false;
            var gate = new SemaphoreSlim(MaxParallel);
            var fresh = new Dictionary<string, List<CloudScore>>();
            await Task.WhenAll(stale.Select(async n =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var l = await _api.GetLastScoresAsync(user, n, ct);
                    fresh[n] = l;
                    _cloudFetched[n] = DateTime.Now;
                    _everFetched.Add(n);
                    changed |= RememberCloud(n, store.WithCached(n, l));
                }
                catch (Exception e) when (!ct.IsCancellationRequested) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("scores load", e); } // offline or rate limited: keep what is cached
                finally { gate.Release(); progress?.Report(++finished); }
            }));
            if (fresh.Count > 0) _ = Task.Run(() => { try { store.SaveScoresBatch(fresh); } catch (IOException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("scores save", e); } });
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (!ct.IsCancellationRequested) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("scores reload", e); }
        finally { foreach (var n in stale) _cloudInflight.Remove(n); }
        return changed;
    }

    /// <summary>Refreshes the cloud scores of the open playlist's scenarios and re-renders the page when they changed.</summary>
    async Task LoadCloudAsync(int tok, BrowserItem item, BenchmarkProgress p)
    {
        var changed = await EnsureCloudAsync(p.Scenarios.Where(sc => sc.TierThresholds.Count > 0).Select(sc => sc.Scenario));
        if (changed && tok == _token && _popupOpen) ShowBenchmark(item, true);
    }
}
