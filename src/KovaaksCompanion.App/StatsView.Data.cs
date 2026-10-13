using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>Server data: the player's server bests (fetched once, cached) and cloud score history. Playlist progress is calculated locally from the bundled thresholds.</summary>
public partial class StatsView
{
    const int MaxParallel = 4;

    readonly CancellationTokenSource _cts = new();
    StatsCache? _cacheStore;
    bool _syncing;
    DateTime? _serverSyncedAt;
    DispatcherTimer? _refreshTimer;
    Dictionary<string, ServerBest> _serverBest = new(StringComparer.OrdinalIgnoreCase);

    StatsCache Cache => _cacheStore ??= _host.Settings.OpenCache();

    static IEnumerable<(Benchmark B, Difficulty D)> AllDifficulties() =>
        BenchmarkCatalog.All.SelectMany(b => b.Difficulties.Select(d => (B: b, D: d))).DistinctBy(x => x.D.KovaaksBenchmarkId);

    /// <summary>The cached server bests of the account (runs off the UI thread).</summary>
    static Dictionary<string, ServerBest> LoadServerBests(StatsCache cache, string steam) =>
        steam.Length == 0 ? new(StringComparer.OrdinalIgnoreCase) : cache.LoadServerBests(steam);

    /// <summary>Stores a difficulty's progress and feeds the server-best and playlist-scenario lookups.</summary>
    void ApplyProgress(Difficulty d, BenchmarkProgress p)
    {
        _rawProgress[d.KovaaksBenchmarkId] = p;
        _progress[d.KovaaksBenchmarkId] = WithLocal(p);
        foreach (var s in p.Scenarios) if (s.Score > 0) _book.RaiseServerBest(s.Scenario, Math.Max(s.Score, _deletedBest.GetValueOrDefault(s.Scenario)));
        _index.Set(d.KovaaksBenchmarkId, p.Scenarios.Select(s => s.Scenario));
    }

    /// <summary>Builds every difficulty's progress from the bundled thresholds and the cached server bests.</summary>
    void RebuildProgress()
    {
        foreach (var (_, d) in AllDifficulties())
            if (BenchmarkProgress.FromBundle(d, n => _serverBest.TryGetValue(n, out var b) ? b.Score : null) is { } p) ApplyProgress(d, p);
    }

    /// <summary>The server progress with the local bests folded in, so tiers reflect runs the server does not have yet.</summary>
    BenchmarkProgress WithLocal(BenchmarkProgress p) =>
        p.WithLocalBests(n =>
        {
            double? local = _book.For(n).Best is > 0 and var best ? best : null;
            return _deletedBest.TryGetValue(n, out var d) && (local == null || d > local) ? d : local;
        });

    /// <summary>Re-applies the local bests after the run library changed.</summary>
    void ReapplyLocal()
    {
        foreach (var (id, raw) in _rawProgress) _progress[id] = WithLocal(raw);
    }

    static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Downloads the player's server bests (and webapp username) on the first call of each app run, then again once
    /// <see cref="SyncInterval"/> has passed. In between the server is not asked: new runs come from the local library.
    /// </summary>
    async Task SyncServerAsync()
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0 || _syncing) return;
        _syncing = true;
        var ct = _cts.Token;
        try
        {
            var cache = Cache;
            if (_serverSyncedAt is { } at && DateTime.Now - at < SyncInterval) return;
            var user = await Task.Run(() => cache.LoadUsername(steam));
            if (string.IsNullOrEmpty(user))
            {
                var likely = AllDifficulties().Select(x => x.D)
                    .OrderByDescending(d => KovaaksCompanion.Core.Benchmarks.RankData.Get(d.KovaaksBenchmarkId) is { } b ? b.Categories.Sum(c => c.Scenarios.Count(s => _lib.Runs(s.Name).Count > 0)) : 0).ToList();
                user = await _api.ResolveUsernameAsync(steam, likely, 6, ct);
                if (string.IsNullOrEmpty(user)) return;
                var name = user;
                await Task.Run(() => cache.SaveUsername(steam, name));
            }
            var bests = await _api.GetTotalPlaysAsync(user, ct);
            if (bests.Count == 0) return;
            await Task.Run(() => { try { cache.SaveServerBests(steam, bests); } catch (IOException) { } });
            _serverBest = bests;
            _serverSyncedAt = DateTime.Now;
            RebuildProgress();
            ScheduleRefresh();
            if (_popupOpen && _popupItem is { } open) ShowBenchmark(open, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!ct.IsCancellationRequested) { } // offline or rate limited: the next reload tries again
        finally { _syncing = false; UpdateTierMarks(false); } // server-side changes are no celebration
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

    // Server score history, kept in the score book

    readonly Dictionary<string, DateTime> _cloudFetched = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _cloudInflight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Puts a scenario's server scores into the score book; Home is redrawn when they changed. Returns whether they changed.</summary>
    bool RememberCloud(string scenario, IReadOnlyList<CloudScore> scores)
    {
        if (!_book.SetCloud(scenario, scores)) return false;
        ScheduleRefresh();
        return true;
    }

    /// <summary>
    /// Brings the server scores of the scenarios up to date: only those never fetched are requested with at most
    /// <see cref="MaxParallel"/> requests at a time, cached and put into the score book. Scenarios the server has no plays
    /// of are skipped, and nothing is requested before the username is known. Returns whether any scores changed.
    /// </summary>
    async Task<bool> EnsureCloudAsync(IEnumerable<string> scenarios)
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0) return false;
        var stale = scenarios.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => (_serverBest.Count == 0 || _serverBest.ContainsKey(n)) && !_cloudFetched.ContainsKey(n) && _cloudInflight.Add(n)).ToList();
        if (stale.Count == 0) return false;
        var ct = _cts.Token;
        var cache = Cache;
        var changed = false;
        try
        {
            var user = await Task.Run(() => cache.LoadUsername(steam));
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
                    changed |= RememberCloud(n, cache.WithCached(n, l));
                }
                catch (Exception) when (!ct.IsCancellationRequested) { } // offline or rate limited: keep what is cached
                finally { gate.Release(); }
            }));
            if (fresh.Count > 0) _ = Task.Run(() => { try { cache.SaveScoresBatch(fresh); } catch (IOException) { } });
        }
        catch (OperationCanceledException) { }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally { foreach (var n in stale) _cloudInflight.Remove(n); }
        return changed;
    }

    /// <summary>Refreshes the server scores of the open playlist's scenarios and re-renders the page when they changed.</summary>
    async Task LoadCloudAsync(int tok, BrowserItem item, BenchmarkProgress p)
    {
        var changed = await EnsureCloudAsync(p.Scenarios.Where(sc => sc.RankMaxes.Count > 0).Select(sc => sc.Scenario));
        if (changed && tok == _token && _popupOpen) ShowBenchmark(item, true);
    }
}
