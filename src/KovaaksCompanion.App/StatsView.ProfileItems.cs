using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace KovaaksCompanion.App;

/// <summary>What the profile card shows (playlists, subcategories, scenarios) and the editor panel above it where the user picks and orders them.</summary>
public partial class StatsView
{
    const string DragFormat = "kc-profile-item";
    bool _profileEditing;

    static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    (Benchmark B, Difficulty D) FindDifficulty(string playlist) =>
        AllDifficulties().FirstOrDefault(x => $"{x.D.KovaaksBenchmarkId}" == playlist);

    /// <summary>The subcategory in the loaded progress; a category without subdivision has no name of its own and matches by its category.</summary>
    static SubcategoryProgress? FindSubcategory(BenchmarkProgress? p, string parent, string sub) =>
        p?.Subcategories.FirstOrDefault(v => CatOf(v) == parent && (v.Name.Trim() == sub || (sub.Length == 0 && v.Name.Trim() == parent)));

    /// <summary>The card for a profile item and its tier color (null = unranked); null when the item no longer exists.</summary>
    (UIElement Card, Color? Tier)? BuildProfileCard(string key)
    {
        Color? ColorOf(Brush b) => b is SolidColorBrush sb ? sb.Color : null;
        if (key.StartsWith("p:", StringComparison.Ordinal))
        {
            var (b, d) = FindDifficulty(key[2..]);
            if (d == null) return null;
            Color? tint = _progress.TryGetValue(d.KovaaksBenchmarkId, out var pr) && pr.OverallRankName.Length > 0 ? ColorOf(RankBrush(d, pr.OverallRankName)) : null;
            return (FavoriteCard(b, d), tint);
        }
        if (UiState.ParseSubcategoryKey(key) is { } k)
        {
            var (b, d) = FindDifficulty(k.Playlist);
            if (d == null) return null;
            _progress.TryGetValue(d.KovaaksBenchmarkId, out var p);
            var s = FindSubcategory(p, k.Parent, k.Sub);
            var cat = d.Categories.FirstOrDefault(c => c.CategoryName == k.Parent);
            var def = cat?.Subcategories.FirstOrDefault(v => v.SubcategoryName.Trim() == k.Sub);
            var dot = ParseBrush(def?.Color is { Length: > 0 } dc ? dc : cat?.Color);
            var ranked = p != null && s != null && s.Rank > 0 && p.RankName(s.Rank).Length > 0;
            var tier = ranked ? RankBrush(d, p!.RankName(s!.Rank)) : (Brush)FindResource("Dim");
            var history = s == null ? [] : Tier.History(s.Scenarios.Select(sc => (_book.For(sc.Scenario), (IReadOnlyList<double>)sc.RankMaxes)));
            var watermark = ranked ? TierText.Label(p!.RankName(s!.Rank)) : "";
            var complete = ranked && s!.Rank >= p!.RankNames.Count - 1;
            var scoped = p != null && s != null ? p with { Subcategories = [s] } : null;
            return (MiniCard(k.Sub.Length > 0 ? k.Sub : k.Parent, $"{b.BenchmarkName} · {d.DifficultyName}", dot, tier, ranked, history, watermark, complete, scoped, () => OpenPlaylist(b, d)), ranked ? ColorOf(tier) : null);
        }
        if (key.StartsWith("s:", StringComparison.Ordinal))
        {
            var name = key[2..];
            BenchmarkProgress? p = null;
            ScenarioProgress? sc = null;
            foreach (var pp in _progress.Values)
            {
                sc = pp.Scenarios.FirstOrDefault(v => v.RankMaxes.Count > 0 && SameKey(v.Scenario, name));
                if (sc != null) { p = pp; break; }
            }
            var stats = _book.For(name);
            var best = Math.Max(stats.Best, sc?.Score ?? 0);
            var rank = sc == null ? 0 : Math.Min(Tier.TierOf(best, sc.RankMaxes).Rank, Math.Max(p!.RankNames.Count - 1, 0));
            var d = p == null ? null : AllDifficulties().FirstOrDefault(x => _progress.TryGetValue(x.D.KovaaksBenchmarkId, out var q) && ReferenceEquals(q, p)).D;
            var ranked = d != null && rank > 0 && p!.RankName(rank).Length > 0;
            var tier = ranked ? RankBrush(d!, p!.RankName(rank)) : (Brush)FindResource("Dim");
            var history = sc == null ? [] : Tier.History([(stats, (IReadOnlyList<double>)sc.RankMaxes)]);
            var watermark = ranked ? TierText.Label(p!.RankName(rank)) : "";
            var complete = ranked && rank >= p!.RankNames.Count - 1;
            var scoped = p != null && sc != null ? p with { Subcategories = [new SubcategoryProgress("", "", 0, 0, [], [sc])] } : null;
            var sub = best > 0 ? L.F("best {0}", best.ToString("0.#")) : L.T("Not played");
            return (MiniCard(name, sub, ranked ? tier : ParseBrush(null), tier, ranked, history, watermark, complete, scoped, () => _host.ShowScenario(name, null)), ranked ? ColorOf(tier) : null);
        }
        return null;
    }

    /// <summary>Editor chip text and dot color for an item key.</summary>
    (string Text, Brush Dot) ProfileLabel(string key)
    {
        if (key.StartsWith("p:", StringComparison.Ordinal))
        {
            var (b, d) = FindDifficulty(key[2..]);
            return d == null ? (key[2..], ParseBrush(null)) : ($"{b.BenchmarkName} {d.DifficultyName}", ParseBrush(b.Color));
        }
        if (UiState.ParseSubcategoryKey(key) is { } k)
        {
            var (b, d) = FindDifficulty(k.Playlist);
            var cat = d?.Categories.FirstOrDefault(c => c.CategoryName == k.Parent);
            var def = cat?.Subcategories.FirstOrDefault(v => v.SubcategoryName.Trim() == k.Sub);
            var name = k.Sub.Length > 0 ? k.Sub : k.Parent;
            return (d == null ? name : $"{name} · {b.BenchmarkName} {d.DifficultyName}", ParseBrush(def?.Color is { Length: > 0 } dc ? dc : cat?.Color));
        }
        return (key.Length > 2 ? key[2..] : key, ParseBrush(null));
    }

    /// <summary>Every favorite the profile card can show: favorite playlists and favorite scenarios.</summary>
    IEnumerable<string> ProfilePool()
    {
        var ui = _host.Ui;
        var favs = AllDifficulties().Where(x => ui.IsFavorite($"{x.D.KovaaksBenchmarkId}")).OrderByDescending(x => ui.FavoriteIndex($"{x.D.KovaaksBenchmarkId}")).ToList();
        foreach (var (_, d) in favs) yield return UiState.PlaylistKey($"{d.KovaaksBenchmarkId}");
        foreach (var n in ui.FavoriteScenarios.Reverse()) yield return UiState.ScenarioKey(n);
    }

    /// <summary>The pinned items the card shows. Subcategories can no longer be pinned; ones saved earlier are skipped, and dropped on the next edit.</summary>
    List<string> ProfileKeys() => _host.Ui.ProfileItems.Where(k => UiState.ParseSubcategoryKey(k) == null).ToList();

    void CommitProfile(IEnumerable<string> keys)
    {
        _host.Ui.SetProfileItems(keys);
        _host.SaveUi();
        // Not inside the drop callback: the dragged chip is rebuilt.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshProfile);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool GetCursorPos(out CursorPoint p);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    /// <summary>Runs a drag with a translucent copy of <paramref name="chip"/> that follows the cursor, held at the point where it was grabbed (OLE drag shows no picture of its own).</summary>
    void DragWithGhost(Border chip, Point grab, DataObject data)
    {
        var ghost = new Border
        {
            Width = chip.ActualWidth, Height = chip.ActualHeight, CornerRadius = chip.CornerRadius, IsHitTestVisible = false, Opacity = 0.9,
            Background = new VisualBrush(chip) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = 0.5 },
            Margin = new Thickness(12),
        };
        var popup = new System.Windows.Controls.Primitives.Popup { AllowsTransparency = true, IsHitTestVisible = false, Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint, Child = ghost };
        var src = PresentationSource.FromVisual(this);
        void Move()
        {
            if (!GetCursorPos(out var c)) return;
            var p = src?.CompositionTarget is { } ct ? ct.TransformFromDevice.Transform(new Point(c.X, c.Y)) : new Point(c.X, c.Y);
            // The popup's own 12px margin (room for the shadow) is taken off again.
            popup.HorizontalOffset = p.X - grab.X - 12;
            popup.VerticalOffset = p.Y - grab.Y - 12;
        }
        GiveFeedbackEventHandler follow = (_, _) => Move();
        chip.GiveFeedback += follow;
        chip.Opacity = 0.35;
        Move();
        popup.IsOpen = true;
        try { DragDrop.DoDragDrop(chip, data, DragDropEffects.Move); }
        finally
        {
            chip.GiveFeedback -= follow;
            popup.IsOpen = false;
            chip.Opacity = 1;
        }
    }

    /// <summary>The panel above the profile card: left = what the card shows (drag to reorder, X to remove), right = favorites not on the card yet (drag to the left to add). The card follows every change.</summary>
    void BuildProfileEditor()
    {
        ProfileEditor.Visibility = _profileEditing ? Visibility.Visible : Visibility.Collapsed;
        ProfileEditor.Child = null;
        if (!_profileEditing) return;

        var ui = _host.Ui;
        var shown = ProfileKeys();

        // A playlist's category names follow it in the playlist's color; they are not draggable.
        FrameworkElement Row(string key, bool onCard)
        {
            var chip = Chip(key, onCard);
            if (!key.StartsWith("p:", StringComparison.Ordinal)) return chip;
            var (b, d) = FindDifficulty(key[2..]);
            if (d == null) return chip;
            var names = d.Categories.Select(c => c.CategoryName.Trim()).Where(n => n.Length > 0).Distinct().ToList();
            if (names.Count == 0) return chip;
            var color = ParseBrush(b.Color);
            var cats = new WrapPanel { Margin = new Thickness(14, 0, 0, 8) };
            for (var i = 0; i < names.Count; i++)
                cats.Children.Add(Text(i < names.Count - 1 ? names[i] + "  ·  " : names[i], 14, color, null));
            chip.Margin = new Thickness(0, 0, 8, 2);
            var row = new StackPanel();
            row.Children.Add(chip);
            row.Children.Add(cats);
            return row;
        }

        Border Chip(string key, bool onCard)
        {
            var (text, dot) = ProfileLabel(key);
            var row = new DockPanel { LastChildFill = true };
            if (onCard)
            {
                var x = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 14, Foreground = DimB, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
                x.MouseEnter += (_, _) => x.Foreground = FgB;
                x.MouseLeave += (_, _) => x.Foreground = DimB;
                x.MouseLeftButtonUp += (_, e) => { e.Handled = true; CommitProfile(shown.Where(v => !SameKey(v, key))); };
                DockPanel.SetDock(x, Dock.Right);
                row.Children.Add(x);
            }
            row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = dot, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var label = Text(text, 20, FgB, null);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            row.Children.Add(label);
            var chip = new Border { Background = SolidFixed("#14FFFFFF"), CornerRadius = new CornerRadius(4), Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.SizeAll, Child = row, Tag = key, ToolTip = text };
            chip.MouseEnter += (_, _) => chip.Background = SolidFixed("#22FFFFFF");
            chip.MouseLeave += (_, _) => chip.Background = SolidFixed("#14FFFFFF");
            Point? down = null;
            chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(null);
            chip.PreviewMouseLeftButtonUp += (_, _) => down = null;
            chip.MouseMove += (_, e) =>
            {
                if (down is not { } d0 || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(null);
                if (Math.Abs(now.X - d0.X) < 6 && Math.Abs(now.Y - d0.Y) < 6) return;
                down = null;
                DragWithGhost(chip, e.GetPosition(chip), new DataObject(DragFormat, key));
            };
            return chip;
        }

        var list = new StackPanel();
        foreach (var k in shown) list.Children.Add(Row(k, true));
        var left = new Border { Background = SolidFixed("#0DFFFFFF"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 12, 4, 4), MinHeight = 80, AllowDrop = true, Child = list };
        left.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        left.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DragFormat) is not string key) return;
            var items = shown.ToList();
            var pos = e.GetPosition(list);
            var at = list.Children.OfType<FrameworkElement>().Count(c => c.TranslatePoint(new Point(0, c.ActualHeight / 2), list).Y < pos.Y);
            var from = items.FindIndex(v => SameKey(v, key));
            if (from >= 0) { items.RemoveAt(from); if (from < at) at--; }
            else if (items.Count >= UiState.MaxProfileItems) return;
            items.Insert(Math.Clamp(at, 0, items.Count), key);
            CommitProfile(items);
        };

        var pool = new StackPanel();
        foreach (var k in ProfilePool().Where(k => !shown.Any(v => SameKey(v, k)))) pool.Children.Add(Row(k, false));
        var right = new Border { Background = SolidFixed("#0DFFFFFF"), CornerRadius = new CornerRadius(4), Padding = new Thickness(12, 12, 4, 4), MinHeight = 80, AllowDrop = true, Child = new ScrollViewer { MaxHeight = 330, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = pool } };
        right.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        right.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DragFormat) is string key && shown.Any(v => SameKey(v, key))) CommitProfile(shown.Where(v => !SameKey(v, key)));
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);
        ProfileEditor.Child = grid;
    }
}
