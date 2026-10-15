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

/// <summary>The profile card at the top of Home: Steam avatar and name, the Home stats and calendar, and the favorite playlist cards.</summary>
public partial class StatsView
{
    /// <summary>Rebuilds the profile card at the top of Home.</summary>
    void RefreshProfile()
    {
        ApplyProfileAccent();
        BuildProfileTools();
        FillProfile();
        FillCelebration();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RefitProfile);
    }

    /// <summary>The app's colored themes, then black and white; the app theme colors and the profile colors are one palette.</summary>
    static readonly string[] ProfileColors = [.. Theme.ColoredAccents, "#000000", "#FFFFFF"];

    /// <summary>The gold profile color, like the gold app theme, is a light card.</summary>
    bool ProfileGold => string.Equals(_host.Ui.ProfileAccent, Theme.ColoredAccents[2], StringComparison.OrdinalIgnoreCase);

    /// <summary>A near-white accent (or gold) makes the card background light, so text and the calendar switch to dark ink.</summary>
    bool ProfileLight => _host.Ui.ProfileAccent.Length > 0 && (ProfileGold || ProfileAccent is SolidColorBrush sb && sb.Color.R + sb.Color.G + sb.Color.B > 720);
    Brush ProfileInk => ProfileLight ? SolidFixed("#FF1C1C1E") : SolidFixed("#FFFFFFFF");
    Brush ProfileDim => ProfileLight ? SolidFixed("#FF5A5A60") : SolidFixed("#FFA1A1A8");
    /// <summary>Text tone of the profile theme: the calendar's tone on a dark card, dark ink on a light one.</summary>
    Brush ProfileTone => ProfileLight ? SolidFixed("#FF2A2A2E") : ChartPaths.TextToneOnDark(ProfileAccent);
    Brush ProfileLine => SolidFixed(ProfileLight ? "#22000000" : "#14FFFFFF");

    Brush ProfileAccent => _host.Ui.ProfileAccent.Length > 0 ? SolidFixed(_host.Ui.ProfileAccent) : (Brush)FindResource("Accent");

    void ApplyProfileAccent()
    {
        ProfileCard.BorderBrush = _host.Ui.ProfileAccent.Length > 0 ? Alpha(ProfileAccent, 0.5) : SolidFixed("#1FFFFFFF");
        // Opaque like the diagonal panel beside the cards, so the gray card is one even surface (a translucent card lets the window backdrop tint only its left part).
        ProfileCard.Background = ProfileBg(0xFF);
    }

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    static Color MixColor(Color x, Color y, double t) =>
        Color.FromRgb((byte)Math.Round(x.R + (y.R - x.R) * t), (byte)Math.Round(x.G + (y.G - x.G) * t), (byte)Math.Round(x.B + (y.B - x.B) * t));

    /// <summary>Start (top left) and end (bottom right) colors of the card background.</summary>
    (Color C1, Color C2) ProfileBgColors()
    {
        var b = Color.FromRgb(0x23, 0x23, 0x26);
        // With no profile color the card is the neutral gray of its swatch, whatever the app theme's tint.
        Color c1 = Color.FromRgb(0x3A, 0x3A, 0x3C), c2 = Color.FromRgb(0x1C, 0x1C, 0x1E);
        if (_host.Ui.ProfileAccent.Length > 0 && ProfileAccent is SolidColorBrush sb)
        {
            var light = ProfileLight;
            var black = sb.Color.R + sb.Color.G + sb.Color.B < 60;
            c1 = light ? sb.Color : MixColor(b, sb.Color, black ? 1 : 0.6);
            c2 = MixColor(Color.FromRgb(0x10, 0x10, 0x12), sb.Color, light ? 0.4 : black ? 1 : 0.0);
            var gray = Math.Max(sb.Color.R, Math.Max(sb.Color.G, sb.Color.B)) - Math.Min(sb.Color.R, Math.Min(sb.Color.G, sb.Color.B)) < 12;
            // A gray stays that gray (a slight light-to-dark slope) instead of fading into the near-black base.
            if (!light && !black && gray) { c1 = MixColor(sb.Color, Colors.White, 0.08); c2 = MixColor(sb.Color, Colors.Black, 0.3); }
            else if (!light && !black) { c1 = Saturate(c1, 1.6); c2 = Saturate(c2, 1.6); }
            if (ProfileGold) { c1 = Color.FromRgb(0xFF, 0xE8, 0x73); c2 = Color.FromRgb(0xFF, 0xF3, 0xB0); }
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
    readonly HashSet<ScoreChart> _profileCharts = [];
    bool _profileCapturing;
    StackPanel? _profileBody;
    double _profilePicWidth;
    /// <summary>The left column is this fraction wider than the picture; the stats and calendar are BodyFrac of the picture width, so the line never touches them.</summary>
    const double BodyExtra = 0, BodyFrac = 0.84, Lean = 0.08;
    double _profilePicH;

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

    /// <summary>The accent picker button and the copy-as-image button, top right of the card.</summary>
    void BuildProfileTools()
    {
        ProfileTools.Children.Clear();
        AddCustomAccent();

        // Motion toggle: on = drifting watermark, off = the static look a capture has.
        var anim = _host.Ui.ProfileAnimated;
        var track = new Border { Width = 34, Height = 20, CornerRadius = new CornerRadius(10), Background = anim ? (Brush)FindResource("AccentFill") : SolidFixed("#33FFFFFF"), VerticalAlignment = VerticalAlignment.Center };
        var knob = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, HorizontalAlignment = anim ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2, 0, 2, 0) };
        track.Child = knob;
        var toggle = new Border { Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = track, ToolTip = anim ? L.T("Motion on") : L.T("Motion off") };
        toggle.MouseLeftButtonUp += (_, _) =>
        {
            _host.Ui.ProfileAnimated = !_host.Ui.ProfileAnimated;
            _host.SaveUi();
            foreach (var ch0 in _profileCharts) ch0.Marquee = _host.Ui.ProfileAnimated;
            BuildProfileTools();
        };
        ProfileTools.Children.Add(toggle);

        var edit = new Button { Style = (Style)FindResource("PopupClose"), Width = 44, Height = 44, Margin = new Thickness(10, 0, 0, 0), ToolTip = L.T("Edit"), Content = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 24, Foreground = FgB, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        edit.Click += (_, _) => OpenProfileEdit();
        ProfileTools.Children.Add(edit);

        var copy = new Button { Style = (Style)FindResource("PopupClose"), Width = 34, Height = 34, Margin = new Thickness(10, 0, 0, 0), Content = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        copy.Click += async (_, _) =>
        {
            if (ProfileCard.ActualWidth <= 0 || _gifBusy) return;
            // Motion on: the card is saved as a looping GIF (and copied as a file); motion off: a still image.
            if (_host.Ui.ProfileAnimated)
            {
                var label = (TextBlock)copy.Content;
                await SaveProfileGifAsync(label, label.Text);
                return;
            }
            try
            {
                ProfileTools.Visibility = Visibility.Hidden;
                // The screen-only watermark drift is switched off so the image keeps the static look.
                _profileCapturing = true;
                foreach (var ch0 in _profileCharts) ch0.Marquee = false;
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
                var file = Path.Combine(_host.Settings.ScreenshotsFolder, $"profile_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                _ = Task.Run(() =>
                {
                    try
                    {
                        _host.Settings.EnsureScreenshotsFolder();
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(shot));
                        using var fs = File.Create(file);
                        enc.Save(fs);
                    }
                    catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); }
                });
            }
            catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); return; }
            finally { _profileCapturing = false; foreach (var ch0 in _profileCharts) ch0.Marquee = _host.Ui.ProfileAnimated; ProfileTools.Visibility = Visibility.Visible; if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Visible; RefitProfile(); } }
            var tb = (TextBlock)copy.Content;
            tb.Text = "";
            await Task.Delay(1200);
            tb.Text = "";
        };
        ProfileTools.Children.Add(copy);
    }

    /// <summary>A rainbow dot: opens the color wheel, the brightness slider and the preset colors below them.</summary>
    void AddCustomAccent()
    {
        var cur = _host.Ui.ProfileAccent;
        var custom = cur.Length > 0;
        var picked = cur.Length > 0 && ProfileAccent is SolidColorBrush;
        var (h0, s0, v0) = picked ? Theme.ToHsv(((SolidColorBrush)ProfileAccent).Color) : (0.0, 0.0, 1.0);
        var rainbow = new ColorWheel { Width = 16, Height = 16, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var ring = new Ellipse { Width = 22, Height = 22, Stroke = custom ? FgB : Brushes.Transparent, StrokeThickness = 2 };
        var dot = new Grid { Width = 28, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        dot.Children.Add(ring);
        dot.Children.Add(rainbow);
        dot.MouseEnter += (_, _) => { if (!custom) ring.Stroke = SolidFixed("#66FFFFFF"); };
        dot.MouseLeave += (_, _) => { if (!custom) ring.Stroke = Brushes.Transparent; };

        var wheel = new ColorWheel { Width = 140, Height = 140, Pick = (h0, s0), Brightness = v0, Active = picked };
        var slider = new Slider { Style = (Style)FindResource("ColorSlider"), Minimum = 20, Maximum = 100, Value = v0 * 100, Width = 140, Margin = new Thickness(0, 12, 0, 0) };
        void Track() => slider.Background = ColorWheel.BrightnessTrack(wheel.Pick.Hue, wheel.Pick.Saturation, 0.2, 1);
        Track();
        var refillQueued = false;
        void Apply(bool final)
        {
            var (h, sat) = wheel.Pick;
            wheel.Brightness = slider.Value / 100;
            Track();
            var c = Theme.FromHsv(h, sat, slider.Value / 100);
            _host.Ui.ProfileAccent = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            ApplyProfileAccent();
            if (final) { _host.SaveUi(); FillProfile(); return; }
            // The right side (charts, ink) follows the drag too; refills are coalesced so a fast drag does not queue one per mouse move.
            if (refillQueued) return;
            refillQueued = true;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => { refillQueued = false; FillProfile(); });
        }
        wheel.Picking += () => Apply(false);
        wheel.Picked += () => Apply(true);
        var syncing = false;
        slider.ValueChanged += (_, _) => { if (wheel.Active && !syncing) Apply(false); };
        slider.PreviewMouseLeftButtonUp += (_, _) => { if (wheel.Active) Apply(true); };
        // Preset colors under the wheel (first = app default); a click moves the wheel and slider to that color.
        var palette = new WrapPanel { Width = 140, Margin = new Thickness(0, 12, 0, 0) };
        void BuildPalette()
        {
            palette.Children.Clear();
            foreach (var hex in ProfileColors.Select(c => c == "#FFFFFF" ? new[] { "", c } : [c]).SelectMany(x => x))
            {
                var on = _host.Ui.ProfileAccent.Equals(hex, StringComparison.OrdinalIgnoreCase);
                var inner = new Ellipse { Width = 16, Height = 16, Fill = hex.Length > 0 ? SolidFixed(hex) : SolidFixed("#8E8E93"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var pr = new Ellipse { Width = 22, Height = 22, Stroke = on ? FgB : Brushes.Transparent, StrokeThickness = 2 };
                var g = new Grid { Width = 28, Height = 28, Background = Brushes.Transparent, Cursor = Cursors.Hand };
                g.Children.Add(pr);
                g.Children.Add(inner);
                g.MouseEnter += (_, _) => { if (!on) pr.Stroke = SolidFixed("#66FFFFFF"); };
                g.MouseLeave += (_, _) => { if (!on) pr.Stroke = Brushes.Transparent; };
                g.MouseLeftButtonUp += (_, _) =>
                {
                    _host.Ui.ProfileAccent = hex;
                    _host.SaveUi();
                    ApplyProfileAccent();
                    if (ProfileAccent is SolidColorBrush b)
                    {
                        var (ph, ps, pv) = Theme.ToHsv(b.Color);
                        syncing = true;
                        wheel.Pick = (ph, ps); wheel.Brightness = pv; wheel.Active = hex.Length > 0;
                        slider.Value = Math.Clamp(pv, 0.2, 1) * 100;
                        syncing = false;
                        Track();
                    }
                    BuildPalette();
                    FillProfile();
                };
                palette.Children.Add(g);
            }
        }
        BuildPalette();
        var card = new Border
            {
                Background = (Brush)FindResource("Surface"), BorderBrush = (Brush)FindResource("Edge"), BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("RadiusL"),
                Padding = new Thickness(16), Margin = new Thickness(24, 6, 24, 36), Child = new StackPanel { Children = { wheel, slider, palette } },
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.5, Color = Colors.Black },
            };
        Grain.SetCard(card, true);
        var pop = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = dot, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = card,
        };
        // Rebuilding the tools swaps the dot (and its ring) for the final color once the popup is closed.
        pop.Closed += (_, _) => BuildProfileTools();
        dot.MouseLeftButtonUp += (_, _) => pop.IsOpen = true;
        ProfileTools.Children.Add(dot);
    }

    void FillProfile()
    {
        using (L.English()) FillProfileCore();
    }

    void FillProfileCore()
    {
        ProfileHost.Children.Clear();
        ProfileHost.ColumnDefinitions.Clear();
        _profileFits.Clear();
        _profileCharts.Clear();
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
        var runs = AllPlays().OrderByDescending(r => r.End).ToList();
        if (runs.Count > 0)
        {
            var plays = runs.GroupBy(r => r.End.Date).ToDictionary(g => g.Key, g => g.Count());
            // Stats and calendar share one width, set to the picture's width.
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            _profileBody = body;
            var stats = BuildPlayStats(runs, plays, 29, 17, FontWeights.Medium, ProfileInk, FontWeights.Light);
            ((FrameworkElement)stats).Margin = new Thickness(0, 12, 4, 12);
            body.Children.Add(stats);
            var cal = new ActivityCalendar { EnglishOnly = true, MaxCell = 42, MaxWeeks = 22, ShowLabels = false, Margin = new Thickness(4, 0, 4, 28) };
            cal.Light = ProfileLight;
            cal.Set(_book.PlaysPerDay, ProfileTone);
            cal.PbDays = _book.PersonalBestDays;
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
        var inkC = ProfileLight ? Colors.Black : _host.Ui.ProfileAccent.Length > 0 && ChartPaths.TextToneOnDark(ProfileAccent) is SolidColorBrush ab ? ab.Color : Colors.White;
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

        // 3: the chosen playlists, subcategories and scenarios, top first
        var favs = ProfileKeys().Select(BuildProfileCard).OfType<(UIElement Card, Color? Tier)>().ToList();
        var right = new StackPanel();
        var favCards = new List<FrameworkElement>();
        // The vertical gap between cards shrinks as cards are added: 4 up to 3 cards, then 3, 2, 1.
        var gap = Math.Max(1, 7 - favs.Count);
        foreach (var (card, tint) in favs)
        {
            // A stronger tier wash than the card's own faint one.
            if (tint is { } tc && card is Border wash)
                wash.Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(Color.FromArgb(0x82, tc.R, tc.G, tc.B), 0), new GradientStop(Color.FromArgb(0xF2, tc.R, tc.G, tc.B), 1) }, new Point(0, 0), new Point(1, 1));
            ((FrameworkElement)card).Margin = new Thickness(0);
            ((FrameworkElement)card).LayoutTransform = new ScaleTransform(1.875, 1.875);
            favCards.Add((FrameworkElement)card);
            if (card is Border cb) { cb.BorderThickness = new Thickness(0); cb.CornerRadius = new CornerRadius(0); }
            right.Children.Add(new Border { Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(0), Margin = new Thickness(0, 0, 0, Math.Min(4, gap)), Child = card });
        }
        // No gap to the line and none to the card edge: the column reaches out over the host's right margin.
        // The cards share the column height equally, whatever their number. Only the text scale is capped (1.875); the card height stretches to fill.
        var rightHost = FitHeight(right, false, h =>
        {
            if (favCards.Count == 0) return;
            var slot = Math.Max(20, (h - Math.Min(4, gap) * favCards.Count) / favCards.Count);
            var s = Math.Min(1.875, slot / 100);
            foreach (var c in favCards)
            {
                c.LayoutTransform = new ScaleTransform(s, s);
                c.Height = slot / s;
                // Tier name: sized from at most 1/3.7 of the popup height; with 3 cards or fewer the name repeats upward.
                if (c is Border { Child: Grid g } && g.Children.OfType<ScoreChart>().FirstOrDefault() is { } sch)
                {
                    sch.MiniTextCap = ProfileHost.ActualHeight / 3.7 / s;
                    sch.DarkMark = ProfileLight;
                    sch.MiniStackAbove =favCards.Count <= 3 ? 0 : double.PositiveInfinity;
                    if (!_profileCapturing) sch.Marquee = _host.Ui.ProfileAnimated;
                    _profileCharts.Add(sch);
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

    /// <summary>A 16:9 picture (click to change; X to remove a chosen one) and the name under it. Without a chosen picture it shows the Steam avatar, or a placeholder when Steam has none cached.</summary>
    UIElement ProfileHeader()
    {
        var steam = _host.Settings.EffectiveSteamId;
        var ui = _host.Ui;
        var (persona, steamAvatar) = SteamAccount.Profile(_host.Settings.KovaaksPath, steam);
        string DefaultName() => persona ?? (steam.Length == 0 ? null : _host.Settings.UsernameFor(steam)) ?? (steam.Length == 0 ? L.T("Profile") : steam);

        void Pick()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|" + L.T("All files") + "|*.*" };
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

        var custom = LoadAvatar(ui.ProfileImage.Length > 0 ? ui.ProfileImage : null, 1200);
        var banner = custom ?? LoadAvatar(steamAvatar, 1200);
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
            var wash = new Border { CornerRadius = new CornerRadius(4), Background = SolidFixed("#66000000"), Opacity = 0, IsHitTestVisible = false };
            var cam = new TextBlock { Text = "", FontFamily = new FontFamily(Icons), FontSize = 28, Foreground = Brushes.White, Opacity = 0, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            var xbg = new Border { Margin = new Thickness(10, 10, 0, 0), Width = 32, Height = 32, CornerRadius = new CornerRadius(0), Background = SolidFixed("#99000000"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Opacity = 0 };
            var x = new Button
            {
                Style = (Style)FindResource("PopupClose"), Width = 32, Height = 32, Margin = new Thickness(10, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Opacity = 0,
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
            if (custom != null)
            {
                holder.Children.Add(xbg);
                holder.Children.Add(x);
            }
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
                CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Background = SolidFixed("#0DFFFFFF"), BorderBrush = SolidFixed("#14FFFFFF"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 10),
                Child = new TextBlock { Text = "\uE722", FontFamily = new FontFamily(Icons), FontSize = 26, Foreground = ProfileDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            add.MouseEnter += (_, _) => add.Background = SolidFixed("#14FFFFFF");
            add.MouseLeave += (_, _) => add.Background = SolidFixed("#0DFFFFFF");
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
        namePill.MouseEnter += (_, _) => namePill.Background = SolidFixed("#14FFFFFF");
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
        try { if (Path.GetFileName(old).StartsWith("profile-", StringComparison.Ordinal)) File.Delete(old); } catch (IOException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("profile image delete", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); } catch (UnauthorizedAccessException e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("profile image delete", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); }
    }
}
