using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Benchmarks;
using KovaaksCompanion.Core.Library;

namespace KovaaksCompanion.App;

/// <summary>What the profile card shows (playlists, playlist categories, scenarios) and the popup where the user pins and orders them.</summary>
public partial class StatsView
{
    const string DragFormat = "kc-profile-item";
    bool _editOpen;
    int _editTok;

    static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    (Benchmark B, Difficulty D) FindDifficulty(string playlist) =>
        AllDifficulties().FirstOrDefault(x => $"{x.D.KovaaksBenchmarkId}" == playlist);

    /// <summary>The categories of a playlist as the loaded progress names them (a click on a category card scrolls to the card of that name), else as the definition does.</summary>
    List<string> CategoryNames(Difficulty d) =>
        _progress.TryGetValue(d.KovaaksBenchmarkId, out var p) && p.Subcategories.Count > 0
            ? p.Subcategories.Select(CatOf).Where(n => n.Length > 0).Distinct().ToList()
            : d.Categories.Select(c => c.CategoryName).Where(n => n.Length > 0).Distinct().ToList();

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
        if (UiState.ParseSubcategoryKey(key) is { Sub.Length: 0 } k)
        {
            // A whole category: its rank is the mean tier progress of its scenarios, like the category cards of the playlist page.
            var (b, d) = FindDifficulty(k.Playlist);
            if (d == null || !_progress.TryGetValue(d.KovaaksBenchmarkId, out var p)) return null;
            var subs = p.Subcategories.Where(v => CatOf(v) == k.Parent).ToList();
            var scenarios = subs.SelectMany(v => v.Scenarios).Where(sc => sc.RankMaxes.Count > 0).ToList();
            if (scenarios.Count == 0) return null;
            var tiers = Math.Min(scenarios.Max(sc => sc.RankMaxes.Count), Math.Max(p.RankNames.Count - 1, 0));
            var value = scenarios.Average(sc => Tier.ValueOf(Math.Max(_book.For(sc.Scenario).Best, sc.Score), sc.RankMaxes));
            var rank = Math.Min((int)Math.Floor(value + 1e-9), tiers);
            var complete = tiers > 0 && rank >= tiers;
            var ranked = rank > 0 && p.RankName(rank).Length > 0;
            var tier = ranked ? RankBrush(d, p.RankName(rank)) : (Brush)FindResource("Dim");
            var dot = ParseBrush(b.Color);
            var history = Tier.History(scenarios.Select(sc => (_book.For(sc.Scenario), (IReadOnlyList<double>)sc.RankMaxes)));
            var watermark = ranked ? TierText.Label(p.RankName(rank)) : L.T("UNRANKED");
            var category = k.Parent;
            return (MiniCard(b.BenchmarkName, category, dot, tier, ranked, history, watermark, complete, complete ? 1 : value - rank, p with { Subcategories = subs }, () => OpenPlaylist(b, d, category)), ranked ? ColorOf(tier) : null);
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
            var pos = sc == null ? default : Tier.TierOf(best, sc.RankMaxes);
            var rank = sc == null ? 0 : Math.Min(pos.Rank, Math.Max(p!.RankNames.Count - 1, 0));
            var d = p == null ? null : AllDifficulties().FirstOrDefault(x => _progress.TryGetValue(x.D.KovaaksBenchmarkId, out var q) && ReferenceEquals(q, p)).D;
            var ranked = d != null && rank > 0 && p!.RankName(rank).Length > 0;
            var tier = ranked ? RankBrush(d!, p!.RankName(rank)) : (Brush)FindResource("Dim");
            var history = sc == null ? [] : Tier.History([(stats, (IReadOnlyList<double>)sc.RankMaxes)]);
            var watermark = ranked ? TierText.Label(p!.RankName(rank)) : L.T("UNRANKED");
            var complete = ranked && rank >= p!.RankNames.Count - 1;
            var scoped = p != null && sc != null ? p with { Subcategories = [new SubcategoryProgress("", "", 0, 0, [], [sc])] } : null;
            var sub = best > 0 ? L.F("best {0}", best.ToString("0.#")) : L.T("Not played");
            return (MiniCard(name, sub, ranked ? tier : ParseBrush(null), tier, ranked, history, watermark, complete, sc == null ? null : complete ? 1 : pos.Fraction, scoped, () => _host.ShowScenario(name, null)), ranked ? ColorOf(tier) : null);
        }
        return null;
    }

    /// <summary>Every item the profile card can show: the favorite playlists, each followed by its categories, then the favorite scenarios.</summary>
    IEnumerable<string> ProfilePool()
    {
        var ui = _host.Ui;
        var favs = AllDifficulties().Where(x => ui.IsFavorite($"{x.D.KovaaksBenchmarkId}")).OrderByDescending(x => ui.FavoriteIndex($"{x.D.KovaaksBenchmarkId}")).ToList();
        foreach (var (_, d) in favs)
        {
            yield return UiState.PlaylistKey($"{d.KovaaksBenchmarkId}");
            foreach (var cat in CategoryNames(d)) yield return UiState.CategoryKey($"{d.KovaaksBenchmarkId}", cat);
        }
        foreach (var n in ui.FavoriteScenarios.Reverse()) yield return UiState.ScenarioKey(n);
    }

    /// <summary>The pinned items the card shows, in pin order. Subcategories can no longer be pinned; ones saved earlier are skipped, and dropped on the next edit.</summary>
    List<string> ProfileKeys() => _host.Ui.ProfileItems.Where(k => UiState.ParseSubcategoryKey(k) is not { Sub.Length: > 0 }).ToList();

    void CommitProfile(IEnumerable<string> keys)
    {
        _host.Ui.SetProfileItems(keys);
        _host.SaveUi();
        // Not inside the click or drop callback: the card in it is rebuilt.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            RefreshProfile();
            if (_editOpen) FillProfileEditor();
        });
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

    // The editor popup

    /// <summary>Opens the popup that pins items to the profile card and orders them.</summary>
    public void OpenProfileEdit()
    {
        if (_editOpen) return;
        _editOpen = true;
        Register("edit", EditPopup, CloseProfileEdit, () => EditPopup.Focus());
        _editTok++;
        EditScrim.BeginAnimation(OpacityProperty, null); EditScrim.Opacity = 0;
        EditCard.BeginAnimation(OpacityProperty, null); EditCard.Opacity = 0;
        EditScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); EditScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        EditScale.ScaleX = EditScale.ScaleY = 0.94;
        FillProfileEditor();
        EditPopup.Visibility = Visibility.Visible;
        Animate(true, null, EditScrim, EditCard, EditScale);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_editOpen) EditPopup.Focus(); });
    }

    public void CloseProfileEdit()
    {
        if (!_editOpen) return;
        _editOpen = false;
        Popups.Closed((this, "edit"));
        var tok = ++_editTok;
        Animate(false, () =>
        {
            if (tok != _editTok) return;
            EditPopup.Visibility = Visibility.Collapsed;
            EditScroll.Content = null;
        }, EditScrim, EditCard, EditScale);
    }

    void OnEditScrim(object sender, MouseButtonEventArgs e) => CloseProfileEdit();

    void OnEditKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseProfileEdit();
    }

    /// <summary>Index of the child of <paramref name="panel"/> under <paramref name="pos"/>, else of the one whose center is nearest.</summary>
    static int IndexAt(Panel panel, Point pos)
    {
        var best = -1;
        var bestDist = double.MaxValue;
        for (var i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement c) continue;
            var r = new Rect(c.TranslatePoint(new Point(0, 0), panel), new Size(c.ActualWidth, c.ActualHeight));
            if (r.Contains(pos)) return i;
            var dist = (new Point(r.X + r.Width / 2, r.Y + r.Height / 2) - pos).LengthSquared;
            if (dist < bestDist) { best = i; bestDist = dist; }
        }
        return best;
    }

    /// <summary>The popup's content, two columns: the pinned items in card order (drag to reorder), then the others in their natural order (never remembered). The pin button on a card pins or unpins it; the card follows every change.</summary>
    void FillProfileEditor()
    {
        var offset = EditScroll.VerticalOffset;
        var shown = ProfileKeys();
        var full = shown.Count >= UiState.MaxProfileItems;

        Border? Item(string key, bool pinned)
        {
            if (BuildProfileCard(key) is not { } built) return null;
            var pin = new Button
            {
                Style = (Style)FindResource("PopupClose"), Width = 32, Height = 32, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, TileGap / 2 + 8, TileGap / 2 + 8, 0), Opacity = !pinned && full ? 0.35 : 1,
                ToolTip = pinned ? L.T("Unpin") : full ? L.T("Up to 6 pinned") : L.T("Pin"),
                Content = new TextBlock { Text = pinned ? "" : "", FontFamily = new FontFamily(Icons), FontSize = 16, Foreground = pinned ? (Brush)FindResource("Accent") : FgB, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            pin.Click += (_, e) =>
            {
                e.Handled = true;
                if (pinned) CommitProfile(shown.Where(v => !SameKey(v, key)));
                else if (!full) CommitProfile([.. shown, key]);
            };
            var cell = new Grid();
            cell.Children.Add(built.Card);
            cell.Children.Add(pin);
            var host = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(4), Tag = key, Child = cell };
            if (!pinned) return host;
            Point? down = null;
            host.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(null);
            host.PreviewMouseLeftButtonUp += (_, _) => down = null;
            host.MouseMove += (_, e) =>
            {
                if (down is not { } d0 || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(null);
                if (Math.Abs(now.X - d0.X) < 6 && Math.Abs(now.Y - d0.Y) < 6) return;
                down = null;
                Mouse.Capture(null); // the card holds the mouse for its click; a drag is no click
                DragWithGhost(host, e.GetPosition(host), new DataObject(DragFormat, key));
            };
            return host;
        }

        var pinnedGrid = new UniformGrid { Columns = 2 };
        foreach (var k in shown) if (Item(k, true) is { } c) pinnedGrid.Children.Add(c);
        var pinnedHost = new Border { Background = Brushes.Transparent, AllowDrop = true, Child = pinnedGrid };
        pinnedHost.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        pinnedHost.Drop += (_, e) =>
        {
            e.Handled = true;
            if (e.Data.GetData(DragFormat) is not string key) return;
            var from = shown.FindIndex(v => SameKey(v, key));
            var at = IndexAt(pinnedGrid, e.GetPosition(pinnedGrid));
            if (from < 0 || at < 0 || at == from) return;
            var items = shown.ToList();
            items.RemoveAt(from);
            items.Insert(Math.Clamp(at, 0, items.Count), key);
            CommitProfile(items);
        };

        var restGrid = new UniformGrid { Columns = 2 };
        foreach (var k in ProfilePool().Where(k => !shown.Any(v => SameKey(v, k)))) if (Item(k, false) is { } c) restGrid.Children.Add(c);

        var root = new StackPanel();
        if (pinnedGrid.Children.Count > 0) root.Children.Add(pinnedHost);
        if (pinnedGrid.Children.Count > 0 && restGrid.Children.Count > 0) root.Children.Add(new Border { Height = 1, Background = Solid("#14FFFFFF"), Margin = new Thickness(8, 10, 8, 10) });
        root.Children.Add(restGrid);
        EditScroll.Content = root;
        EditScroll.UpdateLayout();
        EditScroll.ScrollToVerticalOffset(offset);
    }
}
