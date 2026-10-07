using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>Server data: playlist progress for every difficulty and cloud score history, both cached as JSON and refreshed in the background.</summary>
public partial class StatsView
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromHours(12);
    const int MaxParallel = 4;

    sealed record CachedLoad(Difficulty D, BenchmarkProgress P, DateTime At);

    readonly CancellationTokenSource _cts = new();
    readonly Dictionary<int, DateTime> _fetched = [];
    StatsCache? _cacheStore;
    bool _refreshing;
    DispatcherTimer? _refreshTimer;

    StatsCache Cache => _cacheStore ??= new StatsCache(Path.Combine(_host.Settings.DataRoot, "cache"));

    static IEnumerable<(Benchmark B, Difficulty D)> AllDifficulties() =>
        BenchmarkCatalog.All.SelectMany(b => b.Difficulties.Select(d => (B: b, D: d))).DistinctBy(x => x.D.KovaaksBenchmarkId);

    /// <summary>Cache key of a difficulty's progress: per Steam account, so switching accounts never shows another player's data.</summary>
    static string ProgressKey(string steam, int benchmarkId) => $"{steam}:{benchmarkId}";

    /// <summary>Every cached progress entry that still parses (runs off the UI thread).</summary>
    static List<CachedLoad> LoadCachedProgress(StatsCache cache, string steam)
    {
        var all = cache.LoadAllProgress();
        var res = new List<CachedLoad>();
        if (all.Count == 0) return res;
        foreach (var (_, d) in AllDifficulties())
            if (all.TryGetValue(ProgressKey(steam, d.KovaaksBenchmarkId), out var c))
                try { res.Add(new(d, BenchmarkProgress.Parse(c.Json, d), c.FetchedAt)); }
                catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException) { }
        return res;
    }

    /// <summary>Stores a difficulty's progress and feeds the server-best and playlist-scenario lookups.</summary>
    void ApplyProgress(Difficulty d, BenchmarkProgress p)
    {
        _progress[d.KovaaksBenchmarkId] = p;
        foreach (var s in p.Scenarios) if (s.Score > 0) _serverBest[s.Scenario] = s.Score;
        _index.Set(d.KovaaksBenchmarkId, p.Scenarios.Select(s => s.Scenario));
    }

    /// <summary>Fetches one difficulty's progress and caches the raw JSON.</summary>
    async Task<BenchmarkProgress> FetchProgressAsync(Difficulty d, string steam)
    {
        var json = await _api.GetProgressJsonAsync(d, steam, _cts.Token);
        var p = BenchmarkProgress.Parse(json, d);
        _fetched[d.KovaaksBenchmarkId] = DateTime.Now;
        var cache = Cache;
        _ = Task.Run(() => { try { cache.SaveProgress(ProgressKey(steam, d.KovaaksBenchmarkId), json); } catch (IOException) { } });
        return p;
    }

    /// <summary>Refreshes, with at most <see cref="MaxParallel"/> requests at a time, every difficulty that is missing or older than 12 hours. Favourites go first.</summary>
    async Task RefreshProgressAsync()
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0 || _refreshing) return;
        _refreshing = true;
        var ct = _cts.Token;
        try
        {
            var now = DateTime.Now;
            var todo = AllDifficulties()
                .Where(x => !_progress.ContainsKey(x.D.KovaaksBenchmarkId) || !_fetched.TryGetValue(x.D.KovaaksBenchmarkId, out var at) || now - at > CacheTtl)
                .OrderByDescending(x => _host.Ui.IsFavorite($"{x.D.KovaaksBenchmarkId}")).ToList();
            if (todo.Count == 0) return;
            var gate = new SemaphoreSlim(MaxParallel);
            var pending = new Dictionary<string, string>();
            var cache = Cache;
            void Flush()
            {
                if (pending.Count == 0) return;
                var batch = new Dictionary<string, string>(pending);
                pending.Clear();
                _ = Task.Run(() => { try { cache.SaveProgressBatch(batch); } catch (IOException) { } });
            }
            await Task.WhenAll(todo.Select(async x =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var json = await _api.GetProgressJsonAsync(x.D, steam, ct);
                    var p = BenchmarkProgress.Parse(json, x.D);
                    var id = x.D.KovaaksBenchmarkId;
                    _fetched[id] = DateTime.Now;
                    ApplyProgress(x.D, p);
                    pending[ProgressKey(steam, id)] = json;
                    if (pending.Count >= 10) Flush();
                    ScheduleRefresh();
                    if (_popupOpen && _playlist == $"{id}" && _popupItem is { } open && _ctx is { } c && !ReferenceEquals(c.P, p) && c.P.Progress != p.Progress) ShowBenchmark(open, true);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { } // offline, rate limited or an unexpected payload: keep the cached data
                finally { gate.Release(); }
            }));
            Flush();
            ScheduleRefresh();
        }
        catch (OperationCanceledException) { }
        finally { _refreshing = false; }
    }

    /// <summary>Redraws the hero and the playlist lists shortly after the last progress update (many arrive in a burst).</summary>
    void ScheduleRefresh()
    {
        _refreshTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick -= OnRefreshTick;
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    void OnRefreshTick(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        RefreshHome();
    }

    // Cloud score history for the playlist progress chart

    /// <summary>
    /// Scenarios of the open playlist without local runs get their last server scores: cached ones first, then (12 h or older, or
    /// missing) fetched with at most <see cref="MaxParallel"/> requests at a time. The chart is rebuilt as data arrives.
    /// </summary>
    async Task LoadCloudAsync(int tok, Difficulty d, BenchmarkProgress p)
    {
        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0) return;
        var missing = p.Scenarios.Where(sc => sc.RankMaxes.Count > 0 && _lib.Runs(sc.Scenario).Count == 0)
            .Select(sc => sc.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0) return;
        var ct = _cts.Token;
        var cache = Cache;
        try
        {
            SetNote(tok, "Loading…");
            var (all, user) = await Task.Run(() => (cache.LoadAllScores(), cache.LoadUsername(steam)));
            if (tok != _token) return;
            var cloud = new Dictionary<string, IReadOnlyList<CloudScore>>(StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();
            var now = DateTime.Now;
            foreach (var n in missing)
            {
                if (all.TryGetValue(n, out var c)) { cloud[n] = c.Scores; if (now - c.FetchedAt > CacheTtl) stale.Add(n); }
                else stale.Add(n);
            }
            if (cloud.Count > 0) ApplyCloud(tok, d, cloud);
            if (stale.Count == 0) return;

            if (string.IsNullOrEmpty(user))
            {
                user = await _api.ResolveUsernameAsync(steam, p.Scenarios, ct);
                if (tok != _token) return;
                if (string.IsNullOrEmpty(user)) return;
                var name = user;
                await Task.Run(() => cache.SaveUsername(steam, name));
            }
            var gate = new SemaphoreSlim(MaxParallel);
            var fresh = new Dictionary<string, List<CloudScore>>();
            await Task.WhenAll(stale.Select(async n =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var l = await _api.GetLastScoresAsync(user, n, ct);
                    fresh[n] = l;
                    cloud[n] = l;
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                finally { gate.Release(); }
            }));
            if (fresh.Count > 0)
            {
                var batch = new Dictionary<string, List<CloudScore>>(fresh);
                _ = Task.Run(() => { try { cache.SaveScoresBatch(batch); } catch (IOException) { } });
            }
            ApplyCloud(tok, d, cloud);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { } // keep whatever is shown
        catch (OperationCanceledException) { }
        finally { SetNote(tok, _ctx?.Note ?? ""); }
    }

    /// <summary>Rebuilds the open playlist page with a progress history that includes the cloud scores.</summary>
    void ApplyCloud(int tok, Difficulty d, IReadOnlyDictionary<string, IReadOnlyList<CloudScore>> cloud)
    {
        if (tok != _token || _ctx is not { } c || c.D.KovaaksBenchmarkId != d.KovaaksBenchmarkId) return;
        IReadOnlyList<CloudScore> CloudOf(string n) => cloud.TryGetValue(n, out var l) ? l : [];
        var history = Tier.HistoryWithCloud(c.P.Scenarios.Select<ScenarioProgress, (IReadOnlyList<RunRecord>, IReadOnlyList<CloudScore>, IReadOnlyList<double>)>(
            sc => (ScenarioStats.For(_lib, sc.Scenario).Runs, CloudOf(sc.Scenario), sc.RankMaxes)));
        var used = c.P.Scenarios.Where(sc => sc.RankMaxes.Count > 0 && _lib.Runs(sc.Scenario).Count == 0 && CloudOf(sc.Scenario).Count > 0).Select(sc => sc.Scenario).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var note = used > 0 ? $"{used} from server" : "";
        if (note == c.Note && history.SequenceEqual(c.History)) return;
        _ctx = c with { History = history, Note = note };
        ShowPlaylist(PlaylistScroll.VerticalOffset);
    }

    /// <summary>Sets the chart caption in place (no rebuild).</summary>
    void SetNote(int tok, string text)
    {
        if (tok != _token || _noteBlock is not { } n) return;
        n.Text = text;
        n.Visibility = text.Length == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }
}
