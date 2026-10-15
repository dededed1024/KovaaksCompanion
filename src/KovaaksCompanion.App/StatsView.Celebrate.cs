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
    const int MaxCelebrated = 5;

    readonly record struct TierMark(int Tier, bool Complete);

    Dictionary<string, TierMark>? _tierMarks;
    bool _celebrateNext, _confettiPending;

    /// <summary>Topmost confetti layer owned by the main window, so it covers popups and the title bar.</summary>
    public Confetti ConfettiLayer { get; set; } = new();
    List<string> _celebrated = [];
    readonly HashSet<string> _celebratedComplete = new(StringComparer.OrdinalIgnoreCase);
    List<Color> _celebrationColors = [];
    DispatcherTimer? _expiry;

    /// <summary>Clears the celebration cards 10 minutes after the last win.</summary>
    DispatcherTimer _celebrationExpiry => _expiry ??= MakeExpiry();

    DispatcherTimer MakeExpiry()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        t.Tick += (_, _) => ClearCelebration();
        return t;
    }

    /// <summary>Removes the celebration cards and any running confetti.</summary>
    void ClearCelebration()
    {
        _expiry?.Stop();
        _celebrated = [];
        _celebratedComplete.Clear();
        _confettiPending = false;
        ConfettiLayer.Clear();
        FillCelebration();
    }

    /// <summary>The tier and COMPLETE state of every playlist and scenario, keyed like the profile items.</summary>
    Dictionary<string, TierMark> TierSnapshot()
    {
        var res = new Dictionary<string, TierMark>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, d) in AllDifficulties())
        {
            if (!_progress.TryGetValue(d.KovaaksBenchmarkId, out var p)) continue;
            var id = $"{d.KovaaksBenchmarkId}";
            var top = Math.Max(p.TierNames.Count - 1, 0);
            res[UiState.PlaylistKey(id)] = new(p.OverallTier, p.IsTierComplete(p.OverallTier));
            foreach (var sc in p.Scenarios)
            {
                if (sc.TierThresholds.Count == 0) continue;
                var rank = Math.Min(sc.Tier, top);
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
            var won = now.Where(kv => old.TryGetValue(kv.Key, out var o) && (kv.Value.Tier > o.Tier || (kv.Value.Complete && !o.Complete)))
                .OrderByDescending(kv => kv.Value.Complete && !old[kv.Key].Complete)
                .ThenBy(kv => kv.Key[0] switch { 'p' => 0, 'c' => 1, _ => 2 })
                .Take(MaxCelebrated).Select(kv => kv.Key).ToList();
            if (won.Count > 0)
            {
                foreach (var k in won) { if (now[k].Complete && !old[k].Complete) _celebratedComplete.Add(k); else _celebratedComplete.Remove(k); }
                _celebrated = _celebrated.Where(k => !won.Contains(k, StringComparer.OrdinalIgnoreCase)).Concat(won).TakeLast(MaxCelebrated).ToList();
                _confettiPending = true;
                _celebrationExpiry.Stop();
                _celebrationExpiry.Start();
            }
        }
        _tierMarks = now;
    }

    bool _celebrationScaleHooked;

    /// <summary>Scale the profile Viewbox applies to the card (design width 1753).</summary>
    double ProfileScale => ProfileBox.ActualWidth > 0 ? ProfileBox.ActualWidth / 1753 : 1;

    /// <summary>The card lives inside the profile Viewbox, so its radius, border and padding are divided by the Viewbox scale to look like the Home cards (18 / 1 / 8,16,8,8); the inner cards stay square.</summary>
    void ApplyCelebrationScale()
    {
        var k = 1 / ProfileScale;
        CelebrationCard.CornerRadius = new CornerRadius(18 * k);
        CelebrationCard.BorderThickness = new Thickness(k);
        CelebrationCard.Padding = new Thickness(8 * k, 16 * k, 8 * k, 8 * k);
    }

    /// <summary>The celebration cards under the profile card, built like the profile's own.</summary>
    void FillCelebration()
    {
        if (!_celebrationScaleHooked) { _celebrationScaleHooked = true; ProfileBox.SizeChanged += (_, _) => ApplyCelebrationScale(); }
        CelebrationHost.Children.Clear();
        // Same surface as the Home cards (theme-aware ink).
        CelebrationCard.Background = Solid("#0DFFFFFF");
        CelebrationCard.BorderBrush = Solid("#12FFFFFF");
        var keys = _celebrated.Where(k => BuildProfileCard(k) != null).TakeLast(MaxCelebrated).ToList();
        var items = keys.Select(BuildProfileCard).OfType<(UIElement Card, Color? Tier)>().ToList();
        CelebrationCard.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _celebrationColors = items.Take(MaxCelebrated).Select(x => x.Tier).OfType<Color>().ToList();
        if (items.Count == 0) return;

        var complete = keys.Count(_celebratedComplete.Contains);
        var tierUp = keys.Count - complete;
        var parts = new List<string>();
        if (complete > 0) parts.Add($"{complete} {L.T("Complete!")}");
        if (tierUp > 0) parts.Add($"{tierUp} {L.T("Tier Up!")}");
        // Same ink as the activity card's stats: one color, SemiBold.
        var label = Text(string.Join("  ", parts), 40, FgB, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        // Same size as the profile card's edit button (44, icon 24), with a party popper.
        var replay = new Button { Style = (Style)FindResource("PopupClose"), Width = 44, Height = 44, Margin = new Thickness(10, 0, 0, 0), ToolTip = L.T("Fanfare!"), HorizontalAlignment = HorizontalAlignment.Right, Content = new TextBlock { Text = "🎉", FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        replay.Click += (_, _) => { _confettiPending = true; FireConfetti(); };
        var dismiss = new Button { Style = (Style)FindResource("PopupClose"), Width = 44, Height = 44, Margin = new Thickness(0, 0, 10, 0), ToolTip = L.T("Close"), HorizontalAlignment = HorizontalAlignment.Left, Content = new TextBlock { Text = "✕", FontFamily = new FontFamily("Segoe UI Symbol"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        dismiss.Click += (_, _) => ClearCelebration();
        var header = new Grid { Margin = new Thickness(56, 0, 56, 12) };
        header.Children.Add(label);
        header.Children.Add(dismiss);
        header.Children.Add(replay);

        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
        stack.Children.Add(header);
        var wraps = new List<Border>();
        foreach (var (card, tint) in items)
        {
            var fe = (FrameworkElement)card;
            fe.Margin = new Thickness(0);
            fe.LayoutTransform = new ScaleTransform(1.25, 1.25);
            fe.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (card is Border cb) { cb.BorderThickness = new Thickness(0); cb.CornerRadius = new CornerRadius(0); }
            // Same underlay as the profile's favorite cards.
            Brush under = SolidFixed("#CC34343A");
            if (tint is { } c)
            {
                var baseC = Color.FromRgb(0x2A, 0x2A, 0x2F);
                under = new LinearGradientBrush(new GradientStopCollection { new GradientStop(WithAlpha(MixColor(baseC, c, 0.42), 0xCC), 0), new GradientStop(WithAlpha(MixColor(baseC, c, 0.12), 0xCC), 1) }, new Point(0, 0), new Point(1, 1));
            }
            var wrap = new Border { Background = under, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(56, 0, 56, 2), HorizontalAlignment = HorizontalAlignment.Center, Child = card };
            wraps.Add(wrap);
            stack.Children.Add(wrap);
        }
        // Playlist cards are 70% of the celebration card width, centered.
        stack.SizeChanged += (_, _) => { foreach (var w in wraps) w.Width = CelebrationCard.ActualWidth * 0.7; };
        CelebrationHost.Children.Add(stack);
        ApplyCelebrationScale();
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
