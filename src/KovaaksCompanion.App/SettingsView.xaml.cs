using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Update;
using KovaaksCompanion.Core.Video;

namespace KovaaksCompanion.App;

/// <summary>Settings popup (scrim + card), same shell as the replay popup.</summary>
public partial class SettingsView : UserControl
{
    readonly AppHost _host;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    bool _open, _loading = true;
    int _tok;

    static Version Current => App.CurrentVersion;

    public SettingsView(AppHost host)
    {
        InitializeComponent();
        _host = host;
        Load();
        host.GameStateChanged += OnGameState;
    }

    public void Detach() => _host.GameStateChanged -= OnGameState;

    void OnGameState() => Dispatcher.InvokeAsync(UpdateLock);

    /// <summary>Recording is configured when it starts, so every setting is locked while a game session is live.</summary>
    void UpdateLock()
    {
        var free = !_host.GameActive;
        GeneralSection.IsEnabled = RecordingSection.IsEnabled = StorageSection.IsEnabled = CamSection.IsEnabled = free;
        LockNote.Visibility = free ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool IsOpen => _open;

    void Load()
    {
        _loading = true;
        var s = _host.Settings;
        LoadAppearance(s);
        Kovaaks.Text = s.KovaaksPath; Data.Text = s.DataRoot; Steam.Text = s.EffectiveSteamId;
        foreach (RadioButton r in QualityBar.Children) r.IsChecked = (string)r.Tag == s.VideoQuality.ToString();
        FpsBox.Text = s.VideoFps.ToString();
        StorageBox.Text = s.StorageLimitGb + GbSuffix;
        AgeBox.Text = s.MaxAgeDays + DaySuffix;
        DeleteBeaten.IsChecked = s.DeleteBeatenRuns;
        KeepPb.IsChecked = s.KeepPersonalBests;
        PbOnly.IsChecked = s.VideoPbOnly;
        AutoStart.IsChecked = Autostart.IsOn();
        ErrorReports.IsChecked = s.SendErrorReports;
        SizeCamFrame(s);
        CamOn.IsChecked = s.WebcamEnabled;
        ShowCamPanels(s.WebcamEnabled);
        foreach (RadioButton r in CamSaveBar.Children) r.IsChecked = (string)r.Tag == s.WebcamSave.ToString();
        CamCanvas.Children.Clear(); _cards.Clear(); _selected = null;
        foreach (var slot in s.Webcams) AddCamCard(slot);
        UpdateCamUi();
        UpdateLock();
        _loading = false;
        if (s.WebcamEnabled) _ = RefreshCamDevices();
        Validate(null, null);
        if (Kovaaks.Text.Length == 0 && PathDetector.FindKovaaks() is { } found) { Kovaaks.Text = found; Save(); }
    }

    void LoadAppearance(AppSettings s)
    {
        LanguageBar.Children.Clear();
        foreach (var (code, name) in L.Languages)
            LanguageBar.Children.Add(new RadioButton { Style = (Style)FindResource("Segment"), Content = name, Tag = code, IsChecked = code == L.Code });
        foreach (RadioButton r in LanguageBar.Children) r.Checked += OnAppearance;
        var scale = Math.Clamp(s.UiScale, AppSettings.MinUiScale, AppSettings.MaxUiScale).ToString();
        foreach (RadioButton r in ScaleBar.Children) r.IsChecked = (string)r.Tag == scale;
        ThemeBar.Children.Clear();
        var theme = Theme.Normalize(s.Theme);
        foreach (var name in Theme.Names)
        {
            var on = name == theme;
            var (bg, accent) = Theme.Swatch(name);
            var chip = new Border
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 10, 10), CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(bg), BorderThickness = new Thickness(2), ToolTip = L.T(name),
                BorderBrush = on ? (Brush)FindResource("Accent") : (Brush)FindResource("EdgeFaint"),
                Child = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            chip.MouseLeftButtonUp += (_, _) => { if (!on) SaveAppearance(name); };
            ThemeBar.Children.Add(chip);
        }
    }

    void OnAppearance(object sender, RoutedEventArgs e) { if (!_loading) SaveAppearance(); }

    /// <summary>Applies language, theme and size. Allowed while the game runs: it changes only how the app looks.</summary>
    void SaveAppearance(string? theme = null)
    {
        var cur = _host.Settings;
        string Tag(Panel bar, string fallback) => bar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? fallback;
        var next = cur with
        {
            Language = Tag(LanguageBar, cur.Language),
            Theme = theme ?? cur.Theme,
            UiScale = int.TryParse(Tag(ScaleBar, ""), out var sc) ? sc : cur.UiScale,
        };
        if (next == cur) return;
        _host.ApplySettings(next);
    }

    bool SteamValid()
    {
        var steam = Steam.Text.Trim();
        return steam.Length == 0 || steam.Length == 17 && steam.All(char.IsAsciiDigit);
    }

    bool FpsValid(out int fps) => int.TryParse(FpsBox.Text, out fps) && fps is >= 30 and <= 1000;

    const string GbSuffix = " GB";

    bool StorageValid(out int gb) => int.TryParse(StorageBox.Text.Replace(GbSuffix, "").Trim(), out gb) && gb >= 0;

    /// <summary>Shows " GB" after the number once the box loses focus; edits start from the bare number.</summary>
    void OnStorageFocus(object sender, RoutedEventArgs e)
    {
        if (StorageBox.Text.EndsWith(GbSuffix)) StorageBox.Text = StorageBox.Text[..^GbSuffix.Length];
        StorageBox.CaretIndex = StorageBox.Text.Length;
    }

    void OnStorageLostFocus(object sender, RoutedEventArgs e)
    {
        if (StorageValid(out var gb)) StorageBox.Text = gb + GbSuffix;
        OnCommit(sender, e);
    }

    const string DaySuffix = " d";

    bool AgeValid(out int days) => int.TryParse(AgeBox.Text.Replace(DaySuffix, "").Trim(), out days) && days >= 0;

    void OnAgeFocus(object sender, RoutedEventArgs e)
    {
        if (AgeBox.Text.EndsWith(DaySuffix)) AgeBox.Text = AgeBox.Text[..^DaySuffix.Length];
        AgeBox.CaretIndex = AgeBox.Text.Length;
    }

    void OnAgeLostFocus(object sender, RoutedEventArgs e)
    {
        if (AgeValid(out var days)) AgeBox.Text = days + DaySuffix;
        OnCommit(sender, e);
    }

    void Validate(object? sender, RoutedEventArgs? e)
    {
        Check(StorageBox, StorageError, StorageValid(out _), L.T("Enter 0 or more"));
        Check(AgeBox, AgeError, AgeValid(out _), L.T("Enter 0 or more"));
        Check(Steam, SteamError, SteamValid(), L.T("Must be 17 digits"));
        Check(FpsBox, FpsError, FpsValid(out _), L.T("Enter 30–1000"));
    }

    void OnDigits(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    bool Check(TextBox box, TextBlock error, bool valid, string message)
    {
        error.Text = message;
        error.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        box.BorderBrush = valid ? Brushes.Transparent : (Brush)FindResource("Red");
        return valid;
    }

    /// <summary>Persists the current control values; an invalid Steam ID keeps the saved one. No-op when nothing changed.</summary>
    void Save()
    {
        if (_loading || _host.GameActive) return;
        var cur = _host.Settings;
        var next = cur with
        {
            KovaaksPath = Kovaaks.Text.Trim(), DataFolder = Data.Text.Trim() == cur.DataRoot ? cur.DataFolder : Data.Text.Trim(), SteamId = !SteamValid() || Steam.Text.Trim() == cur.EffectiveSteamId ? cur.SteamId : Steam.Text.Trim(),
            StartWithWindows = AutoStart.IsChecked == true,
            SendErrorReports = ErrorReports.IsChecked == true,
            VideoQuality = QualityBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string t
                && Enum.TryParse<VideoQuality>(t, out var q) ? q : cur.VideoQuality,
            VideoFps = FpsValid(out var fps) ? fps : cur.VideoFps,
            StorageLimitGb = StorageValid(out var gb) ? gb : cur.StorageLimitGb,
            MaxAgeDays = AgeValid(out var days) ? days : cur.MaxAgeDays,
            DeleteBeatenRuns = DeleteBeaten.IsChecked == true,
            KeepPersonalBests = KeepPb.IsChecked == true,
            VideoPbOnly = PbOnly.IsChecked == true,
            WebcamEnabled = CamOn.IsChecked == true,
            WebcamSave = CamSaveBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string cs
                && Enum.TryParse<WebcamSave>(cs, out var hs) ? hs : cur.WebcamSave,
            Webcams = CamSlots(cur.Webcams),
        };
        if (next == cur) return;
        _host.ApplySettings(next);
    }

    void OnCommit(object sender, RoutedEventArgs e) => Save();

    void OnTextKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Save();
    }

    void OnQuality(object sender, RoutedEventArgs e) => Save();

    void OnCamLabel(object sender, MouseButtonEventArgs e)
    {
        if (CamSection.IsEnabled) CamOn.IsChecked = CamOn.IsChecked != true;
    }

    void OnCamToggle(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var on = CamOn.IsChecked == true;
        ShowCamPanels(on);
        if (on)
        {
            UpdateCamUi();
            _ = RefreshCamDevices();
        }
        Save();
    }

    void ShowCamPanels(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        CamLeft.Visibility = v;
    }

    void OnCamSave(object sender, RoutedEventArgs e) => Save();

    async void OnCamAdd(object sender, RoutedEventArgs e)
    {
        if (!CamAdd.IsEnabled) return;
        await RefreshCamDevices();
        var menu = new ContextMenu { Style = (Style)FindResource("ThemedMenu"), PlacementTarget = CamAdd, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, MinWidth = Math.Max(CamAdd.ActualWidth, 200) };
        foreach (var d in _devices.Where(d => !_cards.Any(c => Equals(c.Device, d))))
        {
            var item = new MenuItem { Style = (Style)FindResource("ThemedMenuItem"), Header = d.ToString() };
            item.Click += (_, _) =>
            {
                var card = AddCamCard(new WebcamSlot(d.Name, d.Number, Y: Math.Min(100, 35 * _cards.Count)), select: true);
                UpdateCamUi();
                Save();
                _ = ProbeCam(card);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Style = (Style)FindResource("ThemedMenuItem"), Header = L.T("No webcam found"), IsEnabled = false });
        menu.IsOpen = true;
    }

    void OnCamCanvasDown(object sender, MouseButtonEventArgs e) { _selected = null; UpdateCamUi(); }

    sealed class CamCard
    {
        public WebcamDevice? Device;
        public int Size;
        public double X, Y;
        public int Rotation;
        public WebcamCut Cut = new();
        public double Aspect;
        public bool Probing;
        public required Border Marker;
        public required TextBlock Number;
        public required Border[] Grips;
    }

    readonly List<CamCard> _cards = [];
    CamCard? _selected;
    Border? _bar;
    List<WebcamDevice> _devices = [];
    static readonly string[] CamColors = ["AccentFill", "Green", "Orange"];
    const double BarW = 56, BarH = 28;


    WebcamSlots CamSlots(WebcamSlots cur)
    {
        var next = _cards.Select(c => new WebcamSlot(c.Device?.Name ?? "", c.Device?.Number ?? 0, c.Size, (int)Math.Round(c.X), (int)Math.Round(c.Y), c.Rotation, c.Cut.IsNone ? null : c.Cut, c.Aspect));
        var list = new WebcamSlots(); list.AddRange(next);
        return list.Equals(cur) ? cur : list;
    }

    /// <summary>The floating rotate / delete buttons next to the selected preview box.</summary>
    Border Bar()
    {
        if (_bar != null) { if (!CamCanvas.Children.Contains(_bar)) CamCanvas.Children.Add(_bar); return _bar; }
        Button Tool(string glyph, string tip, string brush, RoutedEventHandler click)
        {
            var b = new Button { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = (Brush)FindResource(brush), ToolTip = L.T(tip), Style = (Style)FindResource("SheetClose"), Width = BarH, Height = BarH };
            b.Click += click;
            return b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Tool("\uE7AD", "Rotate", "Fg", (_, _) =>
        {
            if (_selected is not { } c) return;
            c.Rotation = (c.Rotation + 90) % 360; UpdateCamUi(); Save();
        }));
        row.Children.Add(Tool("\uE74D", "Remove", "Red", (_, _) =>
        {
            if (_selected is not { } c) return;
            _cards.Remove(c); CamCanvas.Children.Remove(c.Marker); _selected = null; UpdateCamUi(); Save();
        }));
        _bar = new Border { Child = row, Background = (Brush)FindResource("Surface"), CornerRadius = new CornerRadius(5), Visibility = Visibility.Collapsed };
        _bar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        Panel.SetZIndex(_bar, 10);
        CamCanvas.Children.Add(_bar);
        return _bar;
    }

    CamCard AddCamCard(WebcamSlot slot, bool select = false)
    {
        var number = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var inner = new Grid();
        inner.Children.Add(number);
        // corners resize the cam, edges cut it
        (bool L, bool R, bool T, bool B)[] sides = [(true, false, true, false), (false, true, true, false), (true, false, false, true), (false, true, false, true),
                                                    (true, false, false, false), (false, true, false, false), (false, false, true, false), (false, false, false, true)];
        var grips = new Border[sides.Length];
        for (int i = 0; i < sides.Length; i++)
        {
            var (l, r, t, b) = sides[i];
            bool corner = i < 4;
            var g = new Border { Visibility = Visibility.Collapsed };
            if (corner)
            {
                g.Width = g.Height = 9; g.CornerRadius = new CornerRadius(2); g.Background = Brushes.White; g.Opacity = 0.9;
                g.Cursor = l == t ? Cursors.SizeNWSE : Cursors.SizeNESW;
            }
            else if (l || r) { g.Width = 8; g.Margin = new Thickness(0, 9, 0, 9); g.Background = Brushes.Transparent; g.Cursor = Cursors.SizeWE; }
            else { g.Height = 8; g.Margin = new Thickness(9, 0, 9, 0); g.Background = Brushes.Transparent; g.Cursor = Cursors.SizeNS; }
            g.HorizontalAlignment = l ? HorizontalAlignment.Left : r ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
            g.VerticalAlignment = t ? VerticalAlignment.Top : b ? VerticalAlignment.Bottom : VerticalAlignment.Stretch;
            grips[i] = g;
        }
        for (int i = sides.Length - 1; i >= 0; i--) inner.Children.Add(grips[i]); // corners last, so they win over the edge strips
        var marker = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.85, Cursor = Cursors.SizeAll, Child = inner };
        var card = new CamCard
        {
            Device = slot.Device.Length > 0 ? new WebcamDevice(slot.Device, slot.Number) : null,
            Size = Math.Clamp(slot.Size, 10, 40), X = slot.X, Y = slot.Y, Rotation = slot.Rotation, Cut = slot.Cut ?? new WebcamCut(), Aspect = slot.Aspect, Marker = marker, Number = number, Grips = grips,
        };
        marker.ToolTip = card.Device?.ToString();
        _cards.Add(card);
        CamCanvas.Children.Add(marker);
        if (select) _selected = card;

        Point grab = default;
        marker.MouseLeftButtonDown += (_, e) => { _selected = card; marker.CaptureMouse(); grab = e.GetPosition(marker); Panel.SetZIndex(marker, 1); UpdateCamUi(); e.Handled = true; };
        marker.MouseMove += (_, e) =>
        {
            if (!marker.IsMouseCaptured) return;
            var p = e.GetPosition(CamCanvas);
            var (_, _, w, h) = CamBox(card);
            double left = p.X - grab.X, top = p.Y - grab.Y;
            ClearGuides();
            Snap(card, w, h, ref left, ref top);
            card.X = Math.Clamp((left + w / 2) / CamW * 100, 0, 100);
            card.Y = Math.Clamp((top + h / 2) / CamH * 100, 0, 100);
            UpdateCamUi();
        };
        marker.MouseLeftButtonUp += (_, _) => { if (!marker.IsMouseCaptured) return; marker.ReleaseMouseCapture(); Panel.SetZIndex(marker, 0); ClearGuides(); Save(); };
        for (int i = 0; i < sides.Length; i++)
            if (i < 4) Corner(grips[i], sides[i].R, sides[i].B);
            else Edge(grips[i], new[] { 0, 2, 1, 3 }[i - 4]);
        return card;

        // a corner resizes the cam (its shape stays) while the opposite corner stays put
        void Corner(Border g, bool right, bool bottom)
        {
            double sl = 0, st = 0, sw = 0, sh = 0;
            g.MouseLeftButtonDown += (_, e) =>
            {
                _selected = card; g.CaptureMouse(); e.Handled = true;
                (sl, st, sw, sh) = CamBox(card);
            };
            g.MouseMove += (_, e) =>
            {
                if (!g.IsMouseCaptured) return;
                var p = ClampToFrame(e.GetPosition(CamCanvas)); // outside the preview the box stops at its edge
                double fx = right ? sl : sl + sw, fy = bottom ? st : st + sh;
                var shown = card.Rotation is 90 or 270 ? card.Cut.Height / SourceAspect(card) : card.Cut.Width; // shown width as a part of the picture's own width
                card.Size = Math.Clamp((int)Math.Round(Math.Abs(p.X - fx) / (CamW * shown) * 100), 10, 40);
                var (_, _, w, h) = CamBox(card);
                card.X = Math.Clamp(((right ? fx : fx - w) + w / 2) / CamW * 100, 0, 100);
                card.Y = Math.Clamp(((bottom ? fy : fy - h) + h / 2) / CamH * 100, 0, 100);
                UpdateCamUi();
            };
            g.MouseLeftButtonUp += (_, _) => { if (!g.IsMouseCaptured) return; g.ReleaseMouseCapture(); Save(); };
        }

        // an edge cuts the cam on that side (0 left, 1 top, 2 right, 3 bottom as shown); the picture keeps its scale and the opposite edge stays put
        void Edge(Border g, int edge)
        {
            bool horizontal = edge % 2 == 0;
            double sl = 0, st = 0, sw = 0, sh = 0;
            g.MouseLeftButtonDown += (_, e) =>
            {
                _selected = card; g.CaptureMouse(); e.Handled = true;
                (sl, st, sw, sh) = CamBox(card);
            };
            g.MouseMove += (_, e) =>
            {
                if (!g.IsMouseCaptured) return;
                var p = ClampToFrame(e.GetPosition(CamCanvas));
                var turned = card.Rotation is 90 or 270;
                var own = CamW * card.Size / 100; // the picture's own width
                var fullW = turned ? own / SourceAspect(card) : own;
                var fullH = turned ? own : own / SourceAspect(card);
                var fixedAt = edge switch { 0 => sl + sw, 1 => st + sh, 2 => sl, _ => st };
                var kept = Math.Abs((horizontal ? p.X : p.Y) - fixedAt) / (horizontal ? fullW : fullH); // part of the whole picture that stays visible
                // the edge as shown is another side of the cam when it is turned
                var side = (edge - card.Rotation / 90 + 4) % 4;
                var opposite = (side + 2) % 4;
                var cut = card.Cut with { };
                var other = CutOf(cut, opposite);
                cut = WithCut(cut, side, Math.Clamp(1 - other - Math.Max(kept, 0.1), 0, 0.9 - other));
                card.Cut = cut;
                var (_, _, w, h) = CamBox(card);
                if (horizontal) card.X = Math.Clamp(((edge == 2 ? sl : sl + sw - w) + w / 2) / CamW * 100, 0, 100);
                else card.Y = Math.Clamp(((edge == 3 ? st : st + sh - h) + h / 2) / CamH * 100, 0, 100);
                UpdateCamUi();
            };
            g.MouseLeftButtonUp += (_, _) => { if (!g.IsMouseCaptured) return; g.ReleaseMouseCapture(); Save(); };
        }
    }

    static double SourceAspect(CamCard c) => c.Aspect > 0 ? c.Aspect : WebcamGeometry.DefaultAspect;
    static double CutOf(WebcamCut c, int side) => side switch { 0 => c.Left, 1 => c.Top, 2 => c.Right, _ => c.Bottom };
    static WebcamCut WithCut(WebcamCut c, int side, double v) => side switch { 0 => c with { Left = v }, 1 => c with { Top = v }, 2 => c with { Right = v }, _ => c with { Bottom = v } };
    Point ClampToFrame(Point p) => new(Math.Clamp(p.X, 0, CamW), Math.Clamp(p.Y, 0, CamH));

    const double CamW = 480, SnapDistance = 6;
    readonly List<UIElement> _guides = [];

    /// <summary>Preview box of a cam (the cam keeps 16:9), centered on its position and pulled inside the frame.</summary>
    (double L, double T, double W, double H) CamBox(CamCard c)
    {
        var (w, h) = WebcamGeometry.BoxSize(CamW, CamH, new WebcamLayout(c.Size, 0, 0, c.Rotation, c.Cut), c.Aspect);
        return (Math.Clamp(CamW * c.X / 100 - w / 2, 0, CamW - w), Math.Clamp(CamH * c.Y / 100 - h / 2, 0, CamH - h), w, h);
    }

    double _camH = 270;
    double CamH => _camH;
    /// <summary>Shapes the preview like the game window (last recorded size, else the main screen).</summary>
    void SizeCamFrame(AppSettings s)
    {
        double aspect = s.GameWidth > 0 && s.GameHeight > 0 ? (double)s.GameWidth / s.GameHeight : SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight;
        _camH = Math.Round(CamW / Math.Clamp(aspect, 1.0, 2.6));
        CamFrame.Height = _camH;
    }

    /// <summary>Reads the cam's own shape so the preview box matches it.</summary>
    async Task ProbeCam(CamCard c)
    {
        if (c.Probing || c.Aspect > 0 || c.Device == null) return;
        var id = _devices.FirstOrDefault(d => Equals(d, c.Device))?.Id;
        if (string.IsNullOrEmpty(id)) return;
        c.Probing = true;
        try
        {
            var aspect = await WebcamProbe.AspectAsync(id, TimeSpan.FromSeconds(4));
            if (aspect > 0 && _cards.Contains(c)) { c.Aspect = aspect; UpdateCamUi(); Save(); }
        }
        finally { c.Probing = false; }
    }

    void ClearGuides() { foreach (var g in _guides) CamCanvas.Children.Remove(g); _guides.Clear(); }

    /// <summary>Pulls a dragged box onto the frame's edges and center and onto the edges and centers of the other cams.</summary>
    void Snap(CamCard moving, double w, double h, ref double left, ref double top)
    {
        List<double> xs = [0, CamW / 2, CamW], ys = [0, CamH / 2, CamH];
        foreach (var o in _cards.Where(o => o != moving))
        {
            var (ol, ot, ow, oh) = CamBox(o);
            xs.AddRange([ol, ol + ow / 2, ol + ow]); ys.AddRange([ot, ot + oh / 2, ot + oh]);
        }
        double? gx = null, gy = null;
        left = SnapAxis(left, w, xs, out gx); top = SnapAxis(top, h, ys, out gy);
        left = Math.Clamp(left, 0, CamW - w); top = Math.Clamp(top, 0, CamH - h);
        if (gx is double x) AddGuide(x, 0, x, CamH);
        if (gy is double y) AddGuide(0, y, CamW, y);
    }

    static double SnapAxis(double start, double size, List<double> targets, out double? guide)
    {
        guide = null;
        double best = SnapDistance, shift = 0;
        foreach (var edge in new[] { 0, size / 2, size })
            foreach (var t in targets)
            {
                var d = t - (start + edge);
                if (Math.Abs(d) < best) { best = Math.Abs(d); shift = d; guide = t; }
            }
        return start + shift;
    }

    void AddGuide(double x1, double y1, double x2, double y2)
    {
        var line = new System.Windows.Shapes.Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = (Brush)FindResource("Fg"), StrokeThickness = 1, Opacity = 0.5, StrokeDashArray = [3, 3], IsHitTestVisible = false };
        _guides.Add(line); CamCanvas.Children.Add(line);
    }

    /// <summary>Preview boxes, the selection tools and the Add button after any change.</summary>
    void UpdateCamUi()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            var (l, t, w, h) = CamBox(c);
            c.Marker.Width = w; c.Marker.Height = h;
            c.Marker.Background = (Brush)FindResource(CamColors[i % CamColors.Length]);
            c.Marker.BorderBrush = c == _selected ? Brushes.White : null;
            c.Marker.BorderThickness = new Thickness(c == _selected ? 1.5 : 0);
            foreach (var g in c.Grips) g.Visibility = c == _selected ? Visibility.Visible : Visibility.Collapsed;
            c.Number.Text = (i + 1).ToString();
            c.Number.RenderTransformOrigin = new Point(0.5, 0.5);
            c.Number.RenderTransform = new RotateTransform(c.Rotation);
            Canvas.SetLeft(c.Marker, l); Canvas.SetTop(c.Marker, t);
        }
        var bar = Bar();
        if (_selected is { } s)
        {
            var (l, t, w, h) = CamBox(s);
            bar.Visibility = Visibility.Visible;
            Canvas.SetLeft(bar, Math.Clamp(l + w - BarW, 0, CamW - BarW));
            Canvas.SetTop(bar, t >= BarH + 2 ? t - BarH - 2 : Math.Min(t + h + 2, CamH - BarH));
        }
        else bar.Visibility = Visibility.Collapsed;
        CamAdd.IsEnabled = _cards.Count < WebcamSlot.Max;
    }

    async Task RefreshCamDevices()
    {
        _devices = await Task.Run(() => AppHost.EnsureObs() ? WebcamDeviceList.List() : []);
        foreach (var c in _cards.ToList()) _ = ProbeCam(c);
    }

    void OnAutoStart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Autostart.Apply(AutoStart.IsChecked == true);
        Save();
    }

    void OnBrowseKovaaks(object sender, RoutedEventArgs e) => BrowseFolder(Kovaaks);
    void OnBrowseData(object sender, RoutedEventArgs e) => BrowseFolder(Data);

    void BrowseFolder(TextBox box)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
        if (d.ShowDialog(Window.GetWindow(this)) != true) return;
        box.Text = box == Data ? (_host.Settings with { DataFolder = d.FolderName }).DataRoot : d.FolderName;
        Save();
    }

    static string CheckText => L.T("Check for updates");

    void OnAutoStartLabel(object sender, MouseButtonEventArgs e) => AutoStart.IsChecked = AutoStart.IsChecked != true;
    void OnBeatenLabel(object sender, MouseButtonEventArgs e) => DeleteBeaten.IsChecked = DeleteBeaten.IsChecked != true;

    void OnPbOnlyLabel(object sender, MouseButtonEventArgs e) => PbOnly.IsChecked = PbOnly.IsChecked != true;
    void OnKeepPbLabel(object sender, MouseButtonEventArgs e) => KeepPb.IsChecked = KeepPb.IsChecked != true;
    void OnErrorReportsLabel(object sender, MouseButtonEventArgs e) => ErrorReports.IsChecked = ErrorReports.IsChecked != true;

    async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        var tok = _tok;
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = L.T("Checking…");
        var latest = await UpdateCheck.Fetch(Http);
        if (tok != _tok) return;
        if (latest != null && UpdateCheck.IsNewer(latest, Current))
        {
            _host.ShowUpdate(latest);
            ResetCheck();
            return;
        }
        CheckBtn.Content = latest == null ? L.T("Could not check") : L.T("Up to date");
        await Task.Delay(3000);
        if (tok == _tok) ResetCheck();
    }

    void ResetCheck()
    {
        CheckBtn.IsEnabled = true;
        if (_host.UpdateAvailable)
        {
            CheckBtn.Content = L.T("Update");
            CheckBtn.SetResourceReference(BackgroundProperty, "Red");
            CheckBtn.Foreground = Brushes.White;
        }
        else { CheckBtn.Content = CheckText; CheckBtn.ClearValue(BackgroundProperty); CheckBtn.ClearValue(ForegroundProperty); }
    }

    static DateTime _lastReport = DateTime.MinValue;
    bool _reporting;

    void OnReportText(object sender, TextChangedEventArgs e) => ReportBtn.IsEnabled = !_reporting && !string.IsNullOrWhiteSpace(ReportText.Text);

    async void OnReport(object sender, RoutedEventArgs e)
    {
        var tok = _tok;
        _reporting = true;
        ReportBtn.IsEnabled = false;
        if (DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(10))
            ReportBtn.Content = L.T("Please try again in a moment");
        else
        {
            ReportBtn.Content = L.T("Sending…");
            var ok = await BugReport.Send(Http, ReportText.Text.Trim(), Current.ToString(3), DescribeEnvironment());
            if (tok != _tok) { _reporting = false; return; }
            if (ok) { _lastReport = DateTime.UtcNow; ReportText.Clear(); }
            ReportBtn.Content = ok ? L.T("Sent") : L.T("Could not send");
        }
        await Task.Delay(ReportBtn.Content as string == L.T("Sent") ? 1200 : 3000);
        if (tok != _tok) { _reporting = false; return; }
        _reporting = false;
        if (string.IsNullOrWhiteSpace(ReportText.Text)) ReportOverlay.Visibility = Visibility.Collapsed;
        ResetReport();
    }

    void OnReportOpen(object sender, RoutedEventArgs e)
    {
        ResetReport();
        // The information exactly as the report carries it: the header line, the environment and the scrubbed log tail.
        ReportInfo.Text = BugReport.Scrub(L.F("Version {0}", Current.ToString(3)) + "\n" + DescribeEnvironment());
        var log = BugReport.LogPreview();
        ReportLog.Text = log ?? "";
        ReportLogPanel.Visibility = string.IsNullOrWhiteSpace(log) ? Visibility.Collapsed : Visibility.Visible;
        ReportOverlay.Visibility = Visibility.Visible;
        ReportText.Focus();
    }

    void OnReportCancel(object sender, RoutedEventArgs e)
    {
        if (!_reporting) ReportOverlay.Visibility = Visibility.Collapsed;
    }

    string DescribeEnvironment()
    {
        var s = _host.Settings;
        var dpi = VisualTreeHelper.GetDpi(this);
        var res = $"{SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:0}x{SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:0}";
        return EnvironmentInfo.Describe(new(res, s.VideoQuality.ToString(), s.VideoFps, s.WebcamEnabled, s.Webcams.Count));
    }

    void ResetReport()
    {
        ReportBtn.Content = L.T("Send");
        ReportBtn.IsEnabled = !_reporting && !string.IsNullOrWhiteSpace(ReportText.Text);
    }

    // ---- popup shell -------------------------------------------------------------------------------------------

    void OnSized(object sender, SizeChangedEventArgs e) => Card.MaxHeight = Math.Max(0, e.NewSize.Height - 48 - 24);

    void OnCardSized(object sender, SizeChangedEventArgs e) =>
        Clipper.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    void OnLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        e.Handled = true;
    }

    /// <summary>Opens the popup with the saved values (values are saved as they change).</summary>
    public void Open()
    {
        if (_open) return;
        Load();
        _open = true;
        VersionCaption.Text = L.F("Version {0}", Current.ToString(3));
        var tok = ++_tok;
        ResetCheck();
        _reporting = false;
        ResetReport();
        ReportOverlay.Visibility = Visibility.Collapsed;
        ReportPanel.Visibility = BugReport.Enabled ? Visibility.Visible : Visibility.Collapsed;
        Scrim.BeginAnimation(OpacityProperty, null); Scrim.Opacity = 0;
        Card.BeginAnimation(OpacityProperty, null); Card.Opacity = 0;
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale.ScaleX = Scale.ScaleY = 0.94;
        Visibility = Visibility.Visible;
        Popups.Opened(this, this, Close, () => { });
        UpdateLayout();
        PageScroll.ScrollToTop();
        if (_host.UpdateAvailable) PageScroll.ScrollToBottom();
        Animate(true, tok);
    }

    /// <summary>Closes the popup; false when it was not open.</summary>
    public bool Close()
    {
        if (!_open) return false;
        if (ReportOverlay.Visibility == Visibility.Visible && !_reporting) { ReportOverlay.Visibility = Visibility.Collapsed; return true; }
        _open = false;
        Popups.Closed(this);
        Animate(false, ++_tok);
        return true;
    }

    void Animate(bool show, int tok)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var ms = TimeSpan.FromMilliseconds(show ? 260 : 180);
        var fade = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 180));
        Scrim.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, ms) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        var sx = new DoubleAnimation(show ? 1 : 0.94, ms) { EasingFunction = ease };
        var sy = sx.Clone();
        if (!show) sx.Completed += (_, _) => { if (tok == _tok && !_open) Visibility = Visibility.Collapsed; };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx, HandoffBehavior.SnapshotAndReplace);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy, HandoffBehavior.SnapshotAndReplace);
    }
}
