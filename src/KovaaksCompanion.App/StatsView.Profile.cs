using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.IO;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using KovaaksCompanion.Core.Benchmarks;

namespace KovaaksCompanion.App;

/// <summary>The profile popup: Steam avatar and name, the Home stats and calendar, and the favorite playlist cards.</summary>
public partial class StatsView
{
    bool _profileOpen;
    int _profileTok;

    public void OpenProfile()
    {
        if (_profileOpen) return;
        _profileOpen = true;
        _profileTok++;
        ProfileScrim.BeginAnimation(OpacityProperty, null); ProfileScrim.Opacity = 0;
        ProfileCard.BeginAnimation(OpacityProperty, null); ProfileCard.Opacity = 0;
        ProfileScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); ProfileScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProfileScale.ScaleX = ProfileScale.ScaleY = 0.94;
        ApplyProfileAccent();
        BuildProfileTools();
        ProfileTools.BeginAnimation(OpacityProperty, null); ProfileTools.Opacity = 0;
        ProfileTools.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        FillProfile();
        ProfilePopup.Visibility = Visibility.Visible;
        Animate(true, null, ProfileScrim, ProfileCard, ProfileScale);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_profileOpen) { ProfilePopup.Focus(); RefitProfile(); } });
    }

    public void CloseProfile(bool instant = false)
    {
        if (!_profileOpen) return;
        _profileOpen = false;
        var tok = ++_profileTok;
        ProfileTools.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)), HandoffBehavior.SnapshotAndReplace);
        void Done()
        {
            if (tok != _profileTok) return;
            ProfilePopup.Visibility = Visibility.Collapsed;
            ProfileHost.Children.Clear();
        }
        if (instant) Done();
        else Animate(false, Done, ProfileScrim, ProfileCard, ProfileScale);
    }

    static readonly string[] ProfileColors = ["#FF375F", "#FF9F0A", "#FFD60A", "#30D158", "#0A84FF", "#BF5AF2", "#000000", "#FFFFFF"];

    /// <summary>A near-white accent makes the card background light, so text and the calendar switch to dark ink.</summary>
    bool ProfileLight => _host.Ui.ProfileAccent.Length > 0 && ProfileAccent is SolidColorBrush sb && sb.Color.R + sb.Color.G + sb.Color.B > 720;
    Brush ProfileInk => ProfileLight ? Solid("#FF1C1C1E") : FgB;
    Brush ProfileDim => ProfileLight ? Solid("#FF5A5A60") : DimB;
    Brush ProfileLine => Solid(ProfileLight ? "#22000000" : "#14FFFFFF");

    Brush ProfileAccent => _host.Ui.ProfileAccent.Length > 0 ? Solid(_host.Ui.ProfileAccent) : (Brush)FindResource("Accent");

    void ApplyProfileAccent()
    {
        ProfileCard.BorderBrush = _host.Ui.ProfileAccent.Length > 0 ? Alpha(ProfileAccent, 0.5) : Solid("#1FFFFFFF");
        ProfileCard.Background = ProfileBg(ProfileLight ? (byte)0xFF : (byte)0xF5);
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color MixColor(Color x, Color y, double t) =>
        Color.FromRgb((byte)Math.Round(x.R + (y.R - x.R) * t), (byte)Math.Round(x.G + (y.G - x.G) * t), (byte)Math.Round(x.B + (y.B - x.B) * t));

    /// <summary>Start (top left) and end (bottom right) colors of the card background.</summary>
    (Color C1, Color C2) ProfileBgColors()
    {
        var b = Color.FromRgb(0x23, 0x23, 0x26);
        Color c1 = Color.FromRgb(0x38, 0x38, 0x3E), c2 = Color.FromRgb(0x18, 0x18, 0x1B);
        if (_host.Ui.ProfileAccent.Length > 0 && ProfileAccent is SolidColorBrush sb)
        {
            var light = ProfileLight;
            var black = sb.Color.R + sb.Color.G + sb.Color.B < 60;
            c1 = light ? sb.Color : MixColor(b, sb.Color, black ? 1 : 0.6);
            c2 = MixColor(Color.FromRgb(0x10, 0x10, 0x12), sb.Color, light ? 0.4 : black ? 1 : 0.0);
            if (!light && !black) { c1 = Saturate(c1, 1.6); c2 = Saturate(c2, 1.6); }
        }
        return (c1, c2);
    }

    /// <summary>Pushes the color away from its own gray by <paramref name="k"/>; brightness stays about the same.</summary>
    static Color Saturate(Color c, double k)
    {
        var y = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        byte Ch(byte v) => (byte)Math.Clamp(Math.Round(y + (v - y) * k), 0, 255);
        return Color.FromRgb(Ch(c.R), Ch(c.G), Ch(c.B));
    }

    /// <summary>The card background: a diagonal gradient from the accent-tinted dark base (top left) to near the plain base (bottom right). Dark enough everywhere for white text.</summary>
    LinearGradientBrush ProfileBg(byte alpha)
    {
        var (c1, c2) = ProfileBgColors();
        var g = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(alpha, c1.R, c1.G, c1.B), 0), new GradientStop(Color.FromArgb(alpha, c2.R, c2.G, c2.B), 1) }, new Point(0, 0), new Point(1, 1));
        g.Freeze();
        return g;
    }

    Action<double, double>? _profileFit;
    readonly List<Action> _profileFits = [];
    Border? _profileAddBox;
    StackPanel? _profileBody;
    double _profilePicWidth;
    /// <summary>The left column is this fraction wider than the picture; the stats and calendar are BodyFrac of the picture width, so the line never touches them.</summary>
    const double BodyExtra = 0, BodyFrac = 0.84, Lean = 0.08;
    double _profilePicH;
    System.Windows.Shapes.Path? _profilePanel;

    /// <summary>Picture box height: 50% of the card height, and at most 40% of its width at 16:9. Independent of the column width, so the layout is the same with or without a picture.</summary>
    double PicHeight(double sc)
    {
        if (ProfileHost.ActualHeight <= 0 || ProfileHost.ActualWidth <= 0) return 0;
        return _profilePicH = Math.Max(30, Math.Min(ProfileHost.ActualHeight * 0.5, ProfileHost.ActualWidth * 0.4 * 9 / 16) / sc);
    }

    void RefitProfile() { foreach (var f in _profileFits) f(); }

    /// <summary>Lays <paramref name="content"/> out at the column width, and when it is taller than the column shrinks it uniformly to fit, so the column never scrolls.</summary>
    Grid FitHeight(FrameworkElement content, bool sizePic = false, Action<double>? pre = null)
    {
        var host = new Grid { ClipToBounds = true };
        var st = new ScaleTransform(1, 1);
        content.HorizontalAlignment = HorizontalAlignment.Left;
        content.VerticalAlignment = VerticalAlignment.Top;
        content.RenderTransformOrigin = new Point(0, 0);
        content.RenderTransform = st;
        // A Canvas arranges the content at its full desired size. In a Grid cell the content is wider and taller than the slot before scaling, so WPF adds a layout clip of the slot size in the content's own space, and the scale then shrinks that clip too.
        var canvas = new Canvas();
        canvas.Children.Add(content);
        host.Children.Add(canvas);
        void Fit()
        {
            double w = host.ActualWidth, h = host.ActualHeight;
            if (w <= 0 || h <= 0) return;
            pre?.Invoke(h);
            double sc = 1;
            for (var i = 0; i < 6; i++)
            {
                content.Width = w / sc;
                if (sizePic) content.Height = double.NaN;
                if (sizePic) _profileFit?.Invoke(w / sc, sc);
                content.Measure(new Size(w / sc, double.PositiveInfinity));
                var next = Math.Min(1, h / Math.Max(1, content.DesiredSize.Height));
                if (Math.Abs(next - sc) < 0.002) break;
                sc = next;
            }
            content.Width = w / sc;
            if (sizePic) _profileFit?.Invoke(w / sc, sc);
            st.ScaleX = st.ScaleY = sc;
            if (sizePic)
            {
                // Stretch to the column so the bottom-aligned body sits at the bottom; hug the column to the picture's width.
                content.Height = h / sc;
                var wl = _profilePicWidth * (1 + BodyExtra) * sc;
                if (_profilePicWidth > 0 && (double.IsNaN(host.Width) || Math.Abs(host.Width - wl) > 0.5)) { host.HorizontalAlignment = HorizontalAlignment.Left; host.Width = wl; }
            }
        }
        host.SizeChanged += (_, _) => Fit();
        _profileFits.Add(Fit);
        return host;
    }

    /// <summary>Accent circles (first = app default) and the copy-as-image button, top right of the card.</summary>
    void BuildProfileTools()
    {
        ProfileTools.Children.Clear();
        void Dot(string hex)
        {
            var on = _host.Ui.ProfileAccent.Equals(hex, StringComparison.OrdinalIgnoreCase);
            var fill = hex.Length > 0 ? Solid(hex) : (Brush)FindResource("Accent");
            var inner = new Ellipse { Width = 16, Height = 16, Fill = fill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            if (hex.Length == 0) inner.Fill = Solid("#8E8E93");
            var ring = new Ellipse { Width = 22, Height = 22, Stroke = on ? FgB : Brushes.Transparent, StrokeThickness = 2 };
            var g = new Grid { Width = 28, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.Children.Add(ring);
            g.Children.Add(inner);
            g.MouseEnter += (_, _) => { if (!on) ring.Stroke = Solid("#66FFFFFF"); };
            g.MouseLeave += (_, _) => { if (!on) ring.Stroke = Brushes.Transparent; };
            g.MouseLeftButtonUp += (_, _) =>
            {
                _host.Ui.ProfileAccent = hex;
                _host.SaveUi();
                ApplyProfileAccent();
                BuildProfileTools();
                FillProfile();
            };
            ProfileTools.Children.Add(g);
        }
        foreach (var c in ProfileColors)
        {
            if (c == "#FFFFFF") Dot("");
            Dot(c);
        }

        var copy = new Button { Style = (Style)FindResource("PopupClose"), Width = 34, Height = 34, Margin = new Thickness(10, 0, 0, 0), Content = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        copy.Click += async (_, _) =>
        {
            if (ProfileCard.ActualWidth <= 0) return;
            try
            {
                ProfileTools.Visibility = Visibility.Hidden;
                if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Collapsed; RefitProfile(); }
                ProfileCard.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(ProfileCard);
                var w = ProfileCard.ActualWidth; var h = ProfileCard.ActualHeight;
                // Render(visual) paints the card at its offset inside its parent (here below the tools row), so render with that offset and crop it away again.
                var off = VisualTreeHelper.GetOffset(ProfileCard);
                int ox = (int)Math.Round(off.X * dpi.DpiScaleX), oy = (int)Math.Round(off.Y * dpi.DpiScaleY);
                int cw = (int)Math.Ceiling(w * dpi.DpiScaleX), ch = (int)Math.Ceiling(h * dpi.DpiScaleY);
                var rtb = new RenderTargetBitmap(ox + cw, oy + ch, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                // The open animation's scale and the drop shadow (a costly blur over the whole card) are switched off while painting.
                var effect = ProfileCard.Effect;
                var xform = ProfileCard.RenderTransform;
                ProfileCard.Effect = null;
                ProfileCard.RenderTransform = Transform.Identity;
                try { rtb.Render(ProfileCard); }
                finally { ProfileCard.Effect = effect; ProfileCard.RenderTransform = xform; }
                var shot = new CroppedBitmap(rtb, new Int32Rect(ox, oy, cw, ch));
                shot.Freeze();
                Clipboard.SetImage(shot);
                // Encoding and writing the PNG happen off the UI thread.
                var file = Path.Combine(_host.Settings.CapturesFolder, $"profile_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _ = Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(shot));
                        using var fs = File.Create(file);
                        enc.Save(fs);
                    }
                    catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); }
                });
            }
            catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); return; }
            finally { ProfileTools.Visibility = Visibility.Visible; if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Visible; RefitProfile(); } }
            var tb = (TextBlock)copy.Content;
            tb.Text = "";
            await Task.Delay(1200);
            tb.Text = "";
        };
        ProfileTools.Children.Add(copy);
    }

    void OnProfileScrim(object sender, MouseButtonEventArgs e) => CloseProfile();

    void OnProfileKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseProfile();
    }

    void FillProfile()
    {
        ProfileHost.Children.Clear();
        ProfileHost.ColumnDefinitions.Clear();
        _profileFits.Clear();
        ProfileHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.7, GridUnitType.Star) });
        ProfileHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        ProfileHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void Col(UIElement content, int col)
        {
            Grid.SetColumn(content, col);
            ProfileHost.Children.Add(content);
        }

        // Left: picture, name, stats and calendar
        _profileBody = null;
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(ProfileHeader());
        var runs = _lib.AllRuns.OrderByDescending(r => r.End).ToList();
        if (runs.Count > 0)
        {
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            // Stats and calendar share one width, set to the picture's width.
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            _profileBody = body;
            var stats = BuildPlayStats(runs, plays, 22, 13, FontWeights.SemiBold, ProfileInk);
            ((FrameworkElement)stats).Margin = new Thickness(4, 12, 4, 12);
            body.Children.Add(stats);
            var cal = new ActivityCalendar { MaxCell = 42, MaxWeeks = 22, Margin = new Thickness(4, 0, 4, 4) };
            cal.Light = ProfileLight;
            cal.Set(plays, ProfileLight ? Solid("#FF2A2A2E") : ChartPaths.TextTone(ProfileAccent));
            cal.PbDays = PbSet().OfType<KovaaksCompanion.Core.Library.RunRecord>().Select(r => r.End.Date).ToHashSet();
            body.Children.Add(cal);
            body.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetRow(body, 1);
            left.Children.Add(body);
        }
        var leftHost = FitHeight(left, true);
        var wide = _profileFit != null;
        if (wide) Grid.SetColumnSpan(leftHost, 3);
        Col(leftHost, 0);
        // The divider is a straight diagonal line from the top left to the bottom right; the favorite cards are indented to follow it.
        var curve = new System.Windows.Shapes.Path { StrokeThickness = 1.5, IsHitTestVisible = false, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var inkC = ProfileLight ? Colors.Black : _host.Ui.ProfileAccent.Length > 0 && ChartPaths.TextTone(ProfileAccent) is SolidColorBrush ab ? ab.Color : Colors.White;
        curve.Stroke = new LinearGradientBrush(new GradientStopCollection
        {
            new GradientStop(Color.FromArgb(0, inkC.R, inkC.G, inkC.B), 0),
            new GradientStop(Color.FromArgb(150, inkC.R, inkC.G, inkC.B), 0.25),
            new GradientStop(Color.FromArgb(150, inkC.R, inkC.G, inkC.B), 0.75),
            new GradientStop(Color.FromArgb(0, inkC.R, inkC.G, inkC.B), 1),
        }, new Point(0, 0), new Point(0, 1));
        var curveHost = new Canvas { IsHitTestVisible = false };
        var panel = new System.Windows.Shapes.Path { IsHitTestVisible = false };
        curveHost.Children.Add(panel);
        curveHost.Children.Add(curve);
        Grid.SetColumnSpan(curveHost, 3);
        curveHost.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        ProfileHost.Children.Add(curveHost);

        // 3: favorite playlists
        var favs = AllDifficulties()
            .Where(x => _host.Ui.IsFavorite($"{x.D.KovaaksBenchmarkId}"))
            .OrderByDescending(x => _host.Ui.FavoriteIndex($"{x.D.KovaaksBenchmarkId}")).ToList();
        var right = new StackPanel();
        if (favs.Count == 0) right.Children.Add(Text("No favorites", 13, ProfileDim, null, new Thickness(6, 0, 0, 0)));
        var favCards = new List<FrameworkElement>();
        foreach (var (b, d) in favs)
        {
            // Opaque underlay with its own tier tint: the card's own wash is faint and translucent, and would otherwise pick up the popup tint.
            var card = FavoriteCard(b, d);
            ((FrameworkElement)card).Margin = new Thickness(0);
            ((FrameworkElement)card).LayoutTransform = new ScaleTransform(1.875, 1.875);
            favCards.Add((FrameworkElement)card);
            if (card is Border cb) { cb.BorderThickness = new Thickness(0); cb.CornerRadius = new CornerRadius(0); }
            Brush under = Solid("#CC34343A"), edge = Brushes.Transparent;
            if (_progress.TryGetValue(d.KovaaksBenchmarkId, out var pr) && pr.OverallRankName.Length > 0 && RankBrush(d, pr.OverallRankName) is SolidColorBrush tb)
            {
                var c = tb.Color;
                var baseC = Color.FromRgb(0x2A, 0x2A, 0x2F);
                under = new LinearGradientBrush(new GradientStopCollection { new GradientStop(WithAlpha(MixColor(baseC, c, 0.42), 0xCC), 0), new GradientStop(WithAlpha(MixColor(baseC, c, 0.12), 0xCC), 1) }, new Point(0, 0), new Point(1, 1));
            }
            right.Children.Add(new Border { Background = under, BorderBrush = edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, 4), IsHitTestVisible = false, Child = card });
        }
        // No gap to the line and none to the card edge: the column reaches out over the host's right margin.
        // The cards share the column height equally, whatever their number. Only the text scale is capped (1.875); the card height stretches to fill.
        var rightHost = FitHeight(right, false, h =>
        {
            if (favCards.Count == 0) return;
            var slot = Math.Max(20, (h - 4 * favCards.Count) / favCards.Count);
            var s = Math.Min(1.875, slot / 100);
            foreach (var c in favCards)
            {
                c.LayoutTransform = new ScaleTransform(s, s);
                c.Height = slot / s;
                // Tier name: sized from at most 1/3.7 of the popup height; with 3 cards or fewer the name repeats upward.
                if (c is Border { Child: Grid g } && g.Children.OfType<ScoreChart>().FirstOrDefault() is { } sch)
                {
                    sch.MiniTextCap = ProfileHost.ActualHeight / 3.7 / s;
                    sch.MiniStackAbove = favCards.Count <= 3 ? 0 : double.PositiveInfinity;
                }
            }
        });
        rightHost.Margin = new Thickness(0, 0, -ProfileHost.Margin.Right, 0);
        Col(rightHost, 2);

        static double Ease(double f) => Math.Clamp(f, 0, 1);
        void LayoutCurve()
        {
            var hh = ProfileHost.ActualHeight;
            var hw = ProfileHost.ActualWidth;
            if (!wide || hh <= 0 || hw <= 0 || _profilePicWidth <= 0 || double.IsNaN(leftHost.Width)) return;
            // A nearly vertical line just right of the stats and calendar, leaning slightly from the top left to the bottom right; it cuts off the picture's right edge.
            var sc0 = leftHost.Width / (_profilePicWidth * (1 + BodyExtra));
            var x1 = _profilePicWidth * BodyFrac * sc0 + 8;
            var x0 = x1 - _profilePicWidth * Lean * sc0;
            var dx = x1 - x0;
            var cols = ProfileHost.ColumnDefinitions;
            if (Math.Abs(cols[0].Width.Value - x0) > 0.5 || cols[0].Width.GridUnitType != GridUnitType.Pixel) cols[0].Width = new GridLength(x0);
            const int n = 48;
            var pts = new Point[n + 1];
            for (var i = 0; i <= n; i++) pts[i] = new Point(x0 + dx * Ease((double)i / n), hh * i / n);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                for (var i = 0; i <= n; i++) { if (i == 0) ctx.BeginFigure(pts[i], false, false); else ctx.LineTo(pts[i], true, true); }
            }
            g.Freeze();
            curve.Data = g;
            var pg = new StreamGeometry();
            using (var ctx = pg.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, -14), true, true);
                foreach (var pt in pts) ctx.LineTo(pt, true, false);
                ctx.LineTo(new Point(pts[n].X, hh + 12), true, false);
                ctx.LineTo(new Point(hw + 14, hh + 12), true, false);
                ctx.LineTo(new Point(hw + 14, -14), true, false);
            }
            pg.Freeze();
            panel.Data = pg;
            // The same diagonal gradient as the card, laid over the card's own area, a touch lighter (darker on a light card): next to the line both sides are nearly the same color. Opaque, so the hidden edge never shows through.
            var (p1, p2) = ProfileBgColors();
            var tint = ProfileLight ? Colors.Black : Colors.White;
            var pb = new LinearGradientBrush(new GradientStopCollection { new GradientStop(MixColor(p1, tint, 0.05), 0), new GradientStop(MixColor(p2, tint, 0.05), 1) }, new Point(0, 0), new Point(1, 1))
            {
                MappingMode = BrushMappingMode.Absolute,
                Transform = new MatrixTransform(hw + 28, 0, 0, hh + 26, -14, -14),
            };
            panel.Fill = pb;
            curveHost.Clip = new RectangleGeometry(new Rect(-14, -14, hw + 28, hh + 26), 5, 5);
            var sc = right.RenderTransform is ScaleTransform st && st.ScaleX > 0 ? st.ScaleX : 1;
            foreach (var child in right.Children.OfType<Border>())
            {
                if (child.ActualHeight <= 0) continue;
                // The card's box starts at the line's position at its top edge; the clip slants its left edge parallel to the line down to the bottom edge.
                var yTop = child.TranslatePoint(new Point(0, 0), right).Y;
                var m = dx * Ease(yTop * sc / hh) / sc;
                if (Math.Abs(child.Margin.Left - m) > 0.5) child.Margin = new Thickness(m, 0, 0, child.Margin.Bottom);
                var cw = child.ActualWidth; var ch = child.ActualHeight - child.Margin.Bottom;
                var slant = Math.Min(cw / 2, dx / hh * ch);
                var cg = new StreamGeometry();
                using (var ctx = cg.Open())
                {
                    ctx.BeginFigure(new Point(0, 0), true, true);
                    ctx.LineTo(new Point(cw, 0), true, false);
                    ctx.LineTo(new Point(cw, ch), true, false);
                    ctx.LineTo(new Point(slant, ch), true, false);
                }
                cg.Freeze();
                child.Clip = cg;
            }
        }
        _profileFits.Add(LayoutCurve);
        leftHost.SizeChanged += (_, _) => LayoutCurve();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { if (_profileOpen) RefitProfile(); });
    }

    static BitmapImage? LoadAvatar(string? path, double size)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.UriSource = new Uri(path);
            img.DecodePixelWidth = (int)(size * 2);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception) { return null; }
    }

    /// <summary>With a chosen picture: a full-width banner (click to change, X to remove) and the name under it. Without: the round Steam avatar (click to choose a picture) beside the name.</summary>
    UIElement ProfileHeader()
    {
        var steam = _host.Settings.EffectiveSteamId;
        var ui = _host.Ui;
        var (persona, _) = SteamAccount.Profile(_host.Settings.KovaaksPath, steam);
        string DefaultName() => persona ?? (steam.Length == 0 ? null : Cache.LoadUsername(steam)) ?? (steam.Length == 0 ? "Profile" : steam);

        void Pick()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var root = _host.Settings.DataRoot;
                Directory.CreateDirectory(root);
                var dest = Path.Combine(root, $"profile-{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName)}");
                File.Copy(dlg.FileName, dest, true);
                if (LoadAvatar(dest, 1200) == null) { File.Delete(dest); return; }
                DeleteProfileImage();
                ui.ProfileImage = dest;
                _host.SaveUi();
                FillProfile();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("profile", ex.Message); }
        }

        var banner = LoadAvatar(ui.ProfileImage.Length > 0 ? ui.ProfileImage : null, 1200);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 6) };
        _profileFit = null;
        _profileAddBox = null;
        UIElement lead;
        if (banner != null)
        {
            var pic = new Border { CornerRadius = new CornerRadius(4), Background = new ImageBrush(banner) { Stretch = Stretch.UniformToFill }, Cursor = Cursors.Hand };
            var holder = new Grid { Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
            // Box is always 16:9 (cropped): full column width unless the height cap (50% of the card height in what is shown, so the stats and calendar keep room below it) is lower, then narrower.
            void FitPic(double avail, double sc)
            {
                var h = PicHeight(sc);
                if (h <= 0) return;
                pic.Height = h;
                holder.Width = h * 16 / 9;
                _profilePicWidth = holder.Width;
                if (_profileBody != null) _profileBody.Width = holder.Width * BodyFrac;
            }
            _profileFit = FitPic;
            var wash = new Border { CornerRadius = new CornerRadius(4), Background = Solid("#66000000"), Opacity = 0, IsHitTestVisible = false };
            var cam = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 28, Foreground = Brushes.White, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var xbg = new Border { Margin = new Thickness(0, 10, 10, 0), Width = 32, Height = 32, CornerRadius = new CornerRadius(0), Background = Solid("#99000000"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0 };
            var x = new Button
            {
                Style = (Style)FindResource("PopupClose"), Width = 32, Height = 32, Margin = new Thickness(0, 10, 10, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
                Content = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 13, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            x.Click += (_, _) =>
            {
                DeleteProfileImage();
                ui.ProfileImage = "";
                _host.SaveUi();
                FillProfile();
            };
            holder.Children.Add(pic);
            holder.Children.Add(wash);
            holder.Children.Add(cam);
            holder.Children.Add(xbg);
            holder.Children.Add(x);
            holder.MouseEnter += (_, _) => { wash.Opacity = cam.Opacity = x.Opacity = xbg.Opacity = 1; };
            holder.MouseLeave += (_, _) => { wash.Opacity = cam.Opacity = x.Opacity = xbg.Opacity = 0; };
            pic.MouseLeftButtonUp += (_, _) => Pick();
            lead = holder;
        }
        else
        {
            // No picture: a quiet placeholder to click, no avatar.
            var add = new Border
            {
                CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Background = Solid("#0DFFFFFF"), BorderBrush = Solid("#14FFFFFF"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock { Text = "\uE722", FontFamily = new FontFamily(Icons), FontSize = 26, Foreground = DimB, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            add.MouseEnter += (_, _) => add.Background = Solid("#14FFFFFF");
            add.MouseLeave += (_, _) => add.Background = Solid("#0DFFFFFF");
            add.MouseLeftButtonUp += (_, _) => Pick();
            _profileAddBox = add;
            _profileFit = (_, sc) =>
            {
                var h = PicHeight(sc);
                if (h <= 0) return;
                add.Height = h;
                add.Width = h * 16 / 9;
                _profilePicWidth = add.Width;
                if (_profileBody != null) _profileBody.Width = add.Width * BodyFrac;
            };
            lead = add;
        }

        var ui2 = (FontFamily)FindResource("UiFont");
        var nameText = Text(ui.ProfileName.Length > 0 ? ui.ProfileName : DefaultName(), 38, ProfileInk, FontWeights.SemiBold);
        nameText.FontFamily = ui2;
        nameText.TextTrimming = TextTrimming.CharacterEllipsis;
        var nameBox = new TextBox { FontSize = 38, FontWeight = FontWeights.SemiBold, FontFamily = ui2, Foreground = ProfileInk, CaretBrush = ProfileInk, MaxLength = 32, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
        var namePill = new Border { CornerRadius = new CornerRadius(0), Padding = new Thickness(8, 2, 8, 2), Background = Brushes.Transparent, Cursor = Cursors.IBeam, Child = nameText, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        namePill.MouseEnter += (_, _) => namePill.Background = Solid("#14FFFFFF");
        namePill.MouseLeave += (_, _) => namePill.Background = Brushes.Transparent;
        var editing = false;
        void Commit(bool save)
        {
            if (!editing) return;
            editing = false;
            if (save)
            {
                var v = nameBox.Text.Trim();
                ui.ProfileName = v == DefaultName() ? "" : v;
                _host.SaveUi();
                nameText.Text = ui.ProfileName.Length > 0 ? ui.ProfileName : DefaultName();
            }
            nameBox.Visibility = Visibility.Collapsed;
            namePill.Visibility = Visibility.Visible;
        }
        namePill.MouseLeftButtonUp += (_, _) =>
        {
            editing = true;
            nameBox.Text = nameText.Text;
            namePill.Visibility = Visibility.Collapsed;
            nameBox.Visibility = Visibility.Visible;
            nameBox.Focus();
            nameBox.SelectAll();
        };
        nameBox.LostKeyboardFocus += (_, _) => Commit(true);
        nameBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(true); }
            else if (e.Key == Key.Escape) { e.Handled = true; Commit(false); }
        };
        var nameHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        nameHost.Children.Add(namePill);
        nameHost.Children.Add(nameBox);

        nameHost.Margin = new Thickness(banner != null ? 8 : 0, 0, 0, 0);
        stack.Children.Add(lead);
        stack.Children.Add(nameHost);
        return stack;
    }

    void DeleteProfileImage()
    {
        var old = _host.Ui.ProfileImage;
        if (old.Length == 0) return;
        try { if (Path.GetFileName(old).StartsWith("profile-", StringComparison.Ordinal)) File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
