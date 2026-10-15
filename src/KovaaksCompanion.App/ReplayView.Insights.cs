using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Library;
using KovaaksCompanion.Core.Perf;
using KovaaksCompanion.Core.Replays;
using KovaaksCompanion.Core.Trajectory;

namespace KovaaksCompanion.App;

public partial class ReplayView
{
    // ---- overview: extra figures over the score line, and the range bar ----------------------------------------

    readonly HashSet<string> _lineShown = [L.T("Score")];
    IReadOnlyList<ChartLine> _lines = [];
    WrapPanel? _lineChips;
    RangeBar? _rangeBar;
    IReadOnlyList<(DateTime End, double Score, RunSpeed Speed)>? _overviewSpeeds;
    string _overviewSpeedsFor = "";

    /// <summary>The figures of each record's run that can be laid over the score line. Records without a local run have none; speed figures appear once the plays' mouse data is read.</summary>
    IReadOnlyList<ChartLine> BuildLines(IReadOnlyList<ScoreRecord> recs, IReadOnlyList<(DateTime End, double Score, RunSpeed Speed)>? speeds)
    {
        double[] Col(Func<ScoreRecord, double?> get) => recs.Select(r => get(r) ?? double.NaN).ToArray();
        RunSpeed? SpeedOf(ScoreRecord r)
        {
            if (r.Run is not { } run) return null;
            if (run.Speed is { } own) return own;
            if (speeds is null) return null;
            RunSpeed? best = null;
            var gap = ReplayStore.RunMatchTolerance;
            foreach (var x in speeds)
            {
                var g = (x.End - run.End).Duration();
                if (g < gap) { gap = g; best = x.Speed; }
            }
            return best;
        }
        var lines = new List<ChartLine>();
        void Add(string name, string brushKey, string format, double[] values, bool needsSome = false)
        {
            if (needsSome ? values.Any(v => v > 0.01) : values.Any(v => !double.IsNaN(v))) lines.Add(new ChartLine(L.T(name), (Brush)FindResource(brushKey), format, values));
        }
        Add("Accuracy", "Green", "0.0%", Col(r => r.Run?.Accuracy));
        Add("Kills", "Orange", "0", Col(r => r.Run?.Kills));
        Add("TTK", "Purple", "0.00\"s\"", Col(r => r.Run?.AvgTtk));
        Add("Overshots", "Red", "0", Col(r => r.Run?.OverShots));
        Add("Reloads", "Mint", "0", Col(r => r.Run?.Reloads), true);
        Add("Distance moved", "Accent", "0.##", Col(r => r.Run?.Distance), true);
        Add("Average speed", "Yellow", "0\"°/s\"", Col(r => SpeedOf(r)?.Mean));
        Add("Reaction time", "Teal", "0\" ms\"", Col(r => SpeedOf(r)?.Reaction * 1000));
        Add("Reloads", "Pink", "0", Col(r => r.Run?.Reloads));
        Add("Distance moved", "Mint", "0.##", Col(r => r.Run?.Distance));
        return lines;
    }

    /// <summary>Applies the lines to the overview chart and redraws the toggle chips above it.</summary>
    void SetChartLines(IReadOnlyList<ChartLine> lines)
    {
        _lines = lines;
        _bigChart.ScoreName = L.T("Score");
        _bigChart.SetLines(lines, _lineShown);
        RefreshLineChips();
    }

    void RefreshLineChips()
    {
        if (_lineChips is not { } host) return;
        host.Children.Clear();
        host.Visibility = _lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var line in _lines.Prepend(new ChartLine(L.T("Score"), (Brush)FindResource("Accent"), "0.#", [])))
        {
            var name = line.Name;
            var on = _lineShown.Contains(name);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = line.Brush, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = name, Foreground = on ? FgB : DimB, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0) });
            var chip = new Border { Style = (Style)FindResource("Chip"), Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(8), Opacity = on ? 1 : 0.55, Child = row };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (!_lineShown.Remove(name)) _lineShown.Add(name);
                _bigChart.SetLines(_lines, _lineShown);
                RefreshLineChips();
            };
            host.Children.Add(chip);
        }
    }

    void SyncRange()
    {
        if (_rangeBar is not { } bar) return;
        bar.Visibility = _bigChart.CanZoom ? Visibility.Visible : Visibility.Collapsed;
        var v = _bigChart.View;
        bar.Set(_bigChart.Total, v.Off, v.Len);
    }

    /// <summary>The score chart's title, toggle chips and range bar, with the chart between them.</summary>
    UIElement ScoreChartBody()
    {
        var body = new StackPanel();
        var title = new Grid { Margin = new Thickness(4, 0, 0, 8) };
        title.Children.Add(new TextBlock { Text = L.T("Run stats"), Foreground = FgB, FontSize = 15, FontWeight = FontWeights.SemiBold });
        _rangeBar = new RangeBar { Width = 130, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, MinLen = ScoreChart.MinWindow };
        _rangeBar.Changed += (off, len) => _bigChart.SetView(off, len);
        _rangeBar.Reset += _bigChart.ResetView;
        title.Children.Add(_rangeBar);
        body.Children.Add(title);
        _lineChips = new WrapPanel { Margin = new Thickness(2, 0, 0, 6) };
        body.Children.Add(_lineChips);
        _bigChart.PointClicked -= OnBigChartClicked;
        _bigChart.PointClicked += OnBigChartClicked;
        _bigChart.ViewChanged -= SyncRange;
        _bigChart.ViewChanged += SyncRange;
        if (_bigChart.Parent is Panel old) old.Children.Remove(_bigChart);
        body.Children.Add(_bigChart);
        return body;
    }

    // ---- one run: against the average of all runs ---------------------------------------------------------------

    readonly Dictionary<Guid, IReadOnlyList<PerfPoint>?> _perfCache = [];
    readonly HashSet<CmpKind> _cmpShown = [CmpKind.Score];
    Border? _cmpHost;
    Guid _cmpMine;
    List<Guid> _cmpIds = [];

    /// <summary>The running figures the comparison can show.</summary>
    enum CmpKind { Score, Accuracy, Kills, Hits, Shots, Reloads, Distance }

    static readonly CmpKind[] CompareSeries = Enum.GetValues<CmpKind>();

    static string CmpName(CmpKind k) => k switch { CmpKind.Reloads => L.T("Reloads"), CmpKind.Distance => L.T("Distance moved"), _ => L.T(k.ToString()) };

    static Brush CmpBrush(CmpKind k) => (Brush)Application.Current.FindResource(k switch
    {
        CmpKind.Accuracy => "Accent", CmpKind.Score => "Green", CmpKind.Kills => "Orange", CmpKind.Shots => "Purple", CmpKind.Hits => "Teal", CmpKind.Reloads => "Pink", _ => "Mint",
    });

    /// <summary>Reads the per-second data of every recorded play of the scenario (once; kept for the next run opened), then fills the comparison card if that run is still open.</summary>
    async Task LoadCompareAsync(RunRecord rec, Border host)
    {
        try { await LoadCompareCoreAsync(rec, host); }
        catch (Exception e) { AppLog.Write("replay", "compare failed: " + e); }
    }

    async Task LoadCompareCoreAsync(RunRecord rec, Border host)
    {
        var scenario = rec.Scenario;
        var si = ReplayStore.FindRun(Infos(), scenario, rec.End);
        if (si < 0) return;
        var mine = _items[si].Info.Id;
        var infos = _items.Where(x => x.Info.Scenario.Equals(scenario, StringComparison.OrdinalIgnoreCase)).Select(x => x.Info).ToList();
        var ids = infos.Select(i => i.Id).ToList();
        var missing = infos.Where(i => !_perfCache.ContainsKey(i.Id)).ToList();
        if (missing.Count > 0)
        {
            var archive = _host.Replays;
            var cache = _host.Settings.OpenSeriesCache();
            var read = await Task.Run(() =>
            {
                var r = missing.AsParallel().Select(i => (Id: i.Id, Series: cache.Get(archive, i))).ToList();
                cache.Save();
                return r;
            });
            foreach (var (id, series) in read) _perfCache[id] = series;
        }
        if (host != _cmpHost || !_runMode) return;
        _cmpMine = mine; _cmpIds = ids;
        RenderCompare(host);
    }

    static double ValueOf(PerfPoint p, CmpKind s) => s switch
    {
        CmpKind.Accuracy => p.Accuracy, CmpKind.Kills => p.Kills, CmpKind.Hits => p.Hits, CmpKind.Shots => p.Shots, CmpKind.Reloads => p.Reloads, CmpKind.Distance => p.Distance, _ => p.Score,
    };

    /// <summary>The running figure of one play at each whole second; NaN once the play is over.</summary>
    static double[] Sample(IReadOnlyList<PerfPoint> pts, CmpKind s, int seconds)
    {
        var res = new double[seconds + 1];
        var j = -1;
        for (var t = 0; t <= seconds; t++)
        {
            while (j + 1 < pts.Count && pts[j + 1].TSec <= t) j++;
            res[t] = t > pts[^1].TSec + 1 ? double.NaN : j < 0 ? (double.IsNaN(ValueOf(pts[0], s)) ? double.NaN : 0) : ValueOf(pts[j], s);
        }
        return res;
    }

    void RenderCompare(Border host)
    {
        host.Child = null;
        if (_perfCache.GetValueOrDefault(_cmpMine) is not { Count: > 0 } mine) return;
        var seconds = (int)Math.Floor(mine[^1].TSec);
        if (seconds < 2) return;
        var others = _cmpIds.Select(id => _perfCache.GetValueOrDefault(id)).Where(p => p is { Count: > 0 }).Select(p => p!).ToList();

        var lines = new List<CompareLine>();
        foreach (var s in CompareSeries.Where(_cmpShown.Contains))
        {
            var run = Sample(mine, s, seconds);
            var plays = others.Select(p => Sample(p, s, seconds)).ToList();
            var avg = new double[seconds + 1];
            for (var t = 0; t <= seconds; t++)
            {
                var vals = plays.Select(p => p[t]).Where(v => !double.IsNaN(v)).ToList();
                avg[t] = vals.Count == 0 ? double.NaN : vals.Average();
            }
            var percent = s == CmpKind.Accuracy;
            lines.Add(new CompareLine(CmpName(s), CmpBrush(s), percent ? "0%" : "0.#", percent ? "0.0%" : s == CmpKind.Distance ? "0.##" : "0.#", s == CmpKind.Score, run, avg));
        }
        var chart = new CompareChart { Height = Card.Width is > 0 and var cw ? Math.Clamp(cw / CardAspect * 0.28, 120, 280) : 160, Margin = new Thickness(0, 8, 0, 6) };
        chart.Set(Enumerable.Range(0, seconds + 1).Select(i => (double)i).ToArray(), lines);

        var body = new StackPanel();
        var head = new Grid { Margin = new Thickness(4, 0, 4, 8) };
        head.Children.Add(new TextBlock { Text = L.T("Compared with average"), Foreground = FgB, FontSize = 15, FontWeight = FontWeights.SemiBold });
        // Where each shown figure ends, against the average.
        var deltas = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var l in lines)
        {
            var endRun = l.Run.Reverse().FirstOrDefault(v => !double.IsNaN(v));
            var endAvg = l.Avg.Reverse().FirstOrDefault(v => !double.IsNaN(v));
            var d = endRun - endAvg;
            var sign = d >= 0 ? "+" : "−";
            var delta = l.Percent ? $"{sign}{Math.Abs(d) * 100:0.0} pt" : endAvg > 0 ? $"{sign}{Math.Abs(d):0.#} ({sign}{Math.Abs(d) / endAvg * 100:0.0}%)" : $"{sign}{Math.Abs(d):0.#}";
            deltas.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = l.Brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(deltas.Children.Count == 0 ? 0 : 14, 0, 5, 0) });
            deltas.Children.Add(new TextBlock { Text = delta, Foreground = (Brush)FindResource(d >= 0 ? "Green" : "Red"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        }
        head.Children.Add(deltas);
        body.Children.Add(head);

        var bar = new Grid { Margin = new Thickness(2, 0, 0, 0) };
        var chips = new WrapPanel();
        foreach (var option in CompareSeries)
        {
            var on = _cmpShown.Contains(option);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = CmpBrush(option), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = CmpName(option), Foreground = on ? FgB : DimB, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0) });
            var chip = new Border
            {
                Style = (Style)FindResource("Chip"), Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(8), Opacity = on ? 1 : 0.55,
                Child = row,
            };
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (_cmpShown.Count == 1 && _cmpShown.Contains(option)) return;
                _cmpShown.Clear();
                _cmpShown.Add(option);
                RenderCompare(host);
            };
            chips.Children.Add(chip);
        }
        bar.Children.Add(chips);
        // Solid line = this run, dashed = the average (each in its figure's colour).
        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 4, 0) };
        void Key(string text, bool dashed)
        {
            var line = new System.Windows.Shapes.Line { X1 = 0, X2 = 16, Y1 = 0, Y2 = 0, Stroke = FgB, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(legend.Children.Count == 0 ? 0 : 12, 0, 5, 0) };
            if (dashed) line.StrokeDashArray = [2, 1.5];
            legend.Children.Add(line);
            legend.Children.Add(new TextBlock { Text = text, Foreground = DimB, FontSize = 12 });
        }
        Key(L.T("This run"), false);
        Key(L.T("Average"), true);
        bar.Children.Add(legend);
        body.Children.Add(bar);
        body.Children.Add(chart);
        host.Child = new Border { Style = (Style)FindResource("SheetCard"), Padding = new Thickness(20, 16, 20, 10), Margin = new Thickness(0, 4, 0, 0), Child = body };
    }
}
