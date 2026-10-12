using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.App;

/// <summary>After a session: playlists, subcategories and scenarios that gained a tier or reached COMPLETE get a card under the profile card and a confetti burst.</summary>
public partial class StatsView
{
    const int MaxCelebrated = 4;

    readonly record struct TierMark(int Rank, bool Complete);

    Dictionary<string, TierMark>? _tierMarks;
    bool _celebrateNext, _confettiPending;
    List<string> _celebrated = [];
    List<Color> _celebrationColors = [];

    /// <summary>The tier and COMPLETE state of every playlist, subcategory and scenario, keyed like the profile items.</summary>
    Dictionary<string, TierMark> TierSnapshot()
    {
        var res = new Dictionary<string, TierMark>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, d) in AllDifficulties())
        {
            if (!_progress.TryGetValue(d.KovaaksBenchmarkId, out var p)) continue;
            var id = $"{d.KovaaksBenchmarkId}";
            var top = Math.Max(p.RankNames.Count - 1, 0);
            res[UiState.PlaylistKey(id)] = new(p.OverallRank, p.IsTierComplete(p.OverallRank));
            foreach (var s in p.Subcategories)
            {
                var cat = CatOf(s);
                var sub = s.Name.Trim() == cat ? "" : s.Name.Trim();
                var rank = Math.Min(s.Rank, top);
                res[UiState.SubcategoryKey(id, cat, sub)] = new(rank, top > 0 && rank >= top);
            }
            foreach (var sc in p.Scenarios)
            {
                if (sc.RankMaxes.Count == 0) continue;
                var rank = Math.Min(sc.Rank, top);
                res.TryAdd(UiState.ScenarioKey(sc.Scenario), new(rank, top > 0 && rank >= top));
            }
        }
        return res;
    }

    /// <summary>Takes a new snapshot; when <paramref name="compare"/> is set, the keys that rose or completed since the last one become the celebrated cards.</summary>
    void UpdateTierMarks(bool compare)
    {
        var now = TierSnapshot();
        if (compare && _tierMarks is { } old)
        {
            var won = now.Where(kv => old.TryGetValue(kv.Key, out var o) && (kv.Value.Rank > o.Rank || (kv.Value.Complete && !o.Complete)))
                .OrderByDescending(kv => kv.Value.Complete && !old[kv.Key].Complete)
                .ThenBy(kv => kv.Key[0] switch { 'p' => 0, 'c' => 1, _ => 2 })
                .Take(MaxCelebrated).Select(kv => kv.Key).ToList();
            if (won.Count > 0)
            {
                _celebrated = won.Concat(_celebrated.Where(k => !won.Contains(k, StringComparer.OrdinalIgnoreCase))).ToList();
                _confettiPending = true;
            }
        }
        _tierMarks = now;
    }

    /// <summary>The celebration cards under the profile card, built like the profile's own.</summary>
    void FillCelebration()
    {
        CelebrationHost.Children.Clear();
        var items = _celebrated.Select(BuildProfileCard).OfType<(UIElement Card, Color? Tier)>().ToList();
        CelebrationHost.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CelebrationHost.Width = items.Count == 1 ? 880 : 1753;
        _celebrationColors = items.Take(MaxCelebrated).Select(x => x.Tier).OfType<Color>().ToList();
        foreach (var (card, tint) in items)
        {
            var fe = (FrameworkElement)card;
            fe.Margin = new Thickness(0);
            fe.LayoutTransform = new ScaleTransform(1.5, 1.5);
            if (card is Border cb) { cb.BorderThickness = new Thickness(0); cb.CornerRadius = new CornerRadius(0); }
            Brush under = SolidFixed("#CC34343A"), edge = Brushes.Transparent;
            if (tint is { } c)
            {
                var baseC = Color.FromRgb(0x2A, 0x2A, 0x2F);
                under = new LinearGradientBrush(new GradientStopCollection { new GradientStop(WithAlpha(MixColor(baseC, c, 0.42), 0xCC), 0), new GradientStop(WithAlpha(MixColor(baseC, c, 0.12), 0xCC), 1) }, new Point(0, 0), new Point(1, 1));
                edge = new SolidColorBrush(WithAlpha(c, 0x99));
            }
            CelebrationHost.Children.Add(new Border { Background = under, BorderBrush = edge, BorderThickness = new Thickness(2), Margin = new Thickness(8), Child = card });
        }
    }

    /// <summary>Fires the confetti once the view is shown.</summary>
    void FireConfetti()
    {
        if (!_confettiPending) return;
        if (!IsVisible || ConfettiLayer.ActualWidth <= 0)
        {
            // Window hidden (tray): fire when it comes back.
            void Once(object? s, DependencyPropertyChangedEventArgs e)
            {
                if (!IsVisible) return;
                IsVisibleChanged -= Once;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, FireConfetti);
            }
            IsVisibleChanged -= Once;
            IsVisibleChanged += Once;
            return;
        }
        _confettiPending = false;
        ConfettiLayer.Fire(_celebrationColors);
    }
}
