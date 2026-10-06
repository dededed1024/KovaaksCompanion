using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary><see cref="Diff"/> is set for benchmark entries (Playlists and Categories tabs).</summary>
public sealed record BrowserItem(string Key, string Title, string Subtitle, Difficulty? Diff = null);

/// <summary>One table row. <see cref="Scenario"/> is set when double-click should open that scenario.</summary>
public sealed record TableRow(string[] Cells, string? Scenario = null);

/// <summary>
/// Benchmarks (the benchmark site's playlists and categories, from the embedded snapshot), per-scenario score history from the
/// stats folder, and the player's server scores. A different non-zero server score wins over the local best.
/// </summary>
public partial class StatsWindow : Window
{
    readonly AppHost _host;
    readonly KovaaksApi _api = new();
    readonly Dictionary<int, BenchmarkProgress> _progress = [];
    readonly Dictionary<string, double> _serverBest = new(StringComparer.OrdinalIgnoreCase);
    RunLibrary _lib = new([]);
    string _mode = "Playlists";
    string? _pendingKey;
    bool _suppressSearch;

    public StatsWindow(AppHost host)
    {
        InitializeComponent();
        Backdrop.Apply(this);
        _host = host;
        Heading.Text = "Loading...";
        host.SessionSaved += OnSessionSaved;
        Closed += (_, _) => host.SessionSaved -= OnSessionSaved;
        Loaded += (_, _) => Reload();
    }

    void OnSessionSaved(Core.Session.SessionInfo info, string folder) => Dispatcher.InvokeAsync(() => { _progress.Clear(); Reload(); });

    async void Reload()
    {
        var folder = _host.Settings.StatsFolder;
        _lib = await Task.Run(() => RunLibrary.Scan(folder));
        FillList();
    }

    void OnModeChecked(object sender, RoutedEventArgs e)
    {
        _mode = (string)((RadioButton)sender).Tag;
        if (!IsLoaded) return;
        _suppressSearch = true;
        Search.Text = "";
        _suppressSearch = false;
        FillList();
    }

    void OnSearchChanged(object sender, TextChangedEventArgs e) { if (IsLoaded && !_suppressSearch) FillList(); }

    void FillList()
    {
        var q = Search.Text.Trim();
        bool Match(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        IEnumerable<BrowserItem> items = _mode == "Scenarios"
            ? _lib.Scenarios.Where(Match).Select(n =>
            {
                var s = ScenarioStats.For(_lib, n);
                return new BrowserItem(n, n, $"{s.Plays} plays  best {s.Best:0.#}  last {s.LastPlayed:yyyy-MM-dd}");
            })
            : BenchmarkCatalog.All.SelectMany(b => b.Difficulties.Select(d => (B: b, D: d)))
                .Where(x => Match(ItemTitle(x.B, x.D)))
                .Select(x => new BrowserItem($"{x.D.KovaaksBenchmarkId}", ItemTitle(x.B, x.D), $"{x.D.ScenarioCount} scenarios  {x.B.Abbreviation}", x.D));

        var list = items.ToList();
        List.ItemsSource = list;
        var want = _pendingKey;
        _pendingKey = null;
        var sel = (want != null ? list.FirstOrDefault(i => i.Key == want) : null) ?? list.FirstOrDefault();
        List.SelectedItem = sel;
        if (sel == null) ShowEmpty();
        else if (want != null) List.ScrollIntoView(sel);
    }

    static string ItemTitle(Benchmark b, Difficulty d) =>
        b.Difficulties.Count == 1 ? b.BenchmarkName : $"{b.BenchmarkName} - {d.DifficultyName}";

    void ShowEmpty()
    {
        Heading.Text = "Nothing to show";
        Summary.Text = "";
        ChartTitle.Text = "";
        Chart.Set([]);
        Rows.ItemsSource = null;
    }

    void OnItemSelected(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is not BrowserItem item) return;
        if (_mode == "Scenarios") ShowScenario(item.Key);
        else ShowBenchmark(item);
    }

    async void ShowBenchmark(BrowserItem item)
    {
        var d = item.Diff!;
        Heading.Text = item.Title;
        Chart.Set([]);
        ChartTitle.Text = "";
        Rows.ItemsSource = null;

        var steam = _host.Settings.EffectiveSteamId;
        if (steam.Length == 0)
        {
            Summary.Text = "No Steam ID found. Set it in Settings to load server scores.\n" + Structure(d);
            return;
        }

        if (!_progress.TryGetValue(d.KovaaksBenchmarkId, out var p))
        {
            Summary.Text = "Loading server scores...";
            try { p = await _api.GetProgressAsync(d, steam); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
            {
                if (ReferenceEquals(List.SelectedItem, item)) Summary.Text = $"Could not load server scores: {ex.Message}\n" + Structure(d);
                return;
            }
            _progress[d.KovaaksBenchmarkId] = p;
            foreach (var s in p.Scenarios) if (s.Score > 0) _serverBest[s.Scenario] = s.Score;
        }
        if (!ReferenceEquals(List.SelectedItem, item)) return;

        var report = PlaylistReport.Build(item.Title, p.Scenarios.Select(s => s.Scenario), _lib);
        var byName = report.Scenarios.ToDictionary(s => s.Scenario, StringComparer.OrdinalIgnoreCase);
        Summary.Text = $"{(p.OverallRankName.Length > 0 ? p.OverallRankName : "Unranked")}  |  {p.Progress:0.#} pts  |  "
            + $"{report.PlayedCount}/{report.ScenarioCount} scenarios played locally  |  {report.TotalPlays} local plays  |  {Dur(report.TimePlayed)}"
            + (report.LastPlayed is { } last ? $"  |  last {last:yyyy-MM-dd}" : "");

        if (_mode == "Playlists")
        {
            ChartTitle.Text = "Local progress: mean of best-so-far / personal best over the playlist's scenarios";
            Chart.Set(report.ProgressOverTime().Select(x => (x.When, x.Progress)).ToList(), "0%");
            SetColumns(("Category", 170), ("Scenario", 300), ("Score", 80), ("Rank", 90), ("Source", 60), ("Plays", 52), ("Last played", 100));
            Rows.ItemsSource = p.Subcategories.SelectMany(sub => sub.Scenarios.Select(sc =>
            {
                var local = byName[sc.Scenario];
                var best = KovaaksApi.Reconcile(local.Plays > 0 ? local.Best : null, sc.Score);
                var fromServer = best != null && (local.Plays == 0 || Math.Abs(best.Value - local.Best) > 0.005);
                return new TableRow(
                [
                    Join(sub.Parent, sub.Name), sc.Scenario, best is { } b ? b.ToString("0.#") : "-", p.RankName(sc.Rank),
                    best == null ? "" : fromServer ? "server" : "local", local.Plays.ToString(),
                    local.LastPlayed is { } d2 ? d2.ToString("yyyy-MM-dd") : "-",
                ], local.Plays > 0 ? sc.Scenario : null);
            })).ToList();
        }
        else
        {
            ChartTitle.Text = "Local plays per day";
            Chart.Set(report.PlaysPerDay().Select(x => (x.Day, (double)x.Plays)).ToList(), "0");
            SetColumns(("Category", 170), ("Subcategory", 170), ("Rank", 90), ("Points", 80), ("Scenarios", 70), ("Plays", 52), ("Trend", 64));
            Rows.ItemsSource = p.Subcategories.Select(sub =>
            {
                var local = sub.Scenarios.Select(s => byName[s.Scenario]).ToList();
                var trends = local.Select(l => l.Trend()).OfType<double>().ToList();
                return new TableRow(
                [
                    sub.Parent, sub.Name, p.RankName(sub.Rank), sub.Progress.ToString("0.#"), sub.Scenarios.Count.ToString(),
                    local.Sum(l => l.Plays).ToString(), trends.Count > 0 ? trends.Average().ToString("+0.0%;-0.0%;0%") : "-",
                ]);
            }).ToList();
        }
    }

    static string Join(string parent, string sub) => parent.Length == 0 ? sub : sub.Length == 0 ? parent : $"{parent} / {sub}";

    static string Structure(Difficulty d) =>
        string.Join("  |  ", d.Categories.Select(c => $"{c.CategoryName} ({c.Subcategories.Sum(s => s.ScenarioCount)})"));

    void ShowScenario(string name)
    {
        var s = ScenarioStats.For(_lib, name);
        Heading.Text = name;
        Summary.Text = $"{s.Plays} plays  |  best {s.Best:0.#}  |  average {s.Average:0.#}  |  accuracy {s.AverageAccuracy:P1}  |  {Dur(s.TimePlayed)}"
            + (s.Trend() is { } t ? $"  |  trend {t:+0.0%;-0.0%;0%} (last 5 vs previous 5)" : "")
            + (s.AverageDrift is { } dr ? $"  |  accuracy drift {dr * 100:+0.0;-0.0;0} pts (2nd half vs 1st)" : "")
            + (_serverBest.TryGetValue(name, out var sv) && KovaaksApi.Reconcile(s.Best, sv) is { } m && Math.Abs(m - s.Best) > 0.005
                ? $"\nServer best {sv:0.#} differs from local best {s.Best:0.#}." : "");
        ChartTitle.Text = "Score per run (green = new personal best, dashed = best so far)";
        Chart.Set(s.Runs.Select(r => (r.End, r.Score)).ToList(), "0.#", s.PersonalBestIndexes(), s.BestSoFar());

        SetColumns(("Date", 150), ("Score", 80), ("Accuracy", 80), ("Kills", 60), ("1st half", 70), ("2nd half", 70), ("Best 10s", 70), ("", 60));
        var pbs = new HashSet<int>(s.PersonalBestIndexes());
        Rows.ItemsSource = s.Runs.Select((r, i) => new TableRow(
            [r.End.ToString("yyyy-MM-dd HH:mm"), r.Score.ToString("0.#"), r.Accuracy.ToString("P1"), r.Kills.ToString(),
                r.Perf is { } p ? p.FirstHalfAccuracy.ToString("P0") : "-", r.Perf is { } p2 ? p2.SecondHalfAccuracy.ToString("P0") : "-",
                r.Perf is { } p3 ? p3.BestWindowScore.ToString("0.#") : "-", pbs.Contains(i) ? "PB" : ""]))
            .Reverse().ToList();
    }

    void SetColumns(params (string Header, double Width)[] cols)
    {
        RowsView.Columns.Clear();
        for (var i = 0; i < cols.Length; i++)
            RowsView.Columns.Add(new GridViewColumn { Header = cols[i].Header, Width = cols[i].Width, DisplayMemberBinding = new Binding($"Cells[{i}]") });
    }

    void OnRowDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Rows.SelectedItem is not TableRow { Scenario: { } scenario } || _mode == "Scenarios") return;
        _pendingKey = _lib.Scenarios.FirstOrDefault(n => n.Equals(scenario, StringComparison.OrdinalIgnoreCase)) ?? scenario;
        ModeBar.Children.OfType<RadioButton>().First(r => (string)r.Tag == "Scenarios").IsChecked = true;
    }

    static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
}
