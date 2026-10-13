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
        CamOn.IsChecked = s.HandCamEnabled;
        ShowCamPanels(s.HandCamEnabled);
        foreach (RadioButton r in CamSaveBar.Children) r.IsChecked = (string)r.Tag == s.HandCamSave.ToString();
        CamCards.Children.Clear(); CamCanvas.Children.Clear(); _cards.Clear();
        foreach (var slot in s.HandCams) AddCamCard(slot);
        UpdateCamUi();
        UpdateLock();
        _loading = false;
        if (s.HandCamEnabled) _ = RefreshCamDevices();
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
            HandCamEnabled = CamOn.IsChecked == true,
            HandCamSave = CamSaveBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string cs
                && Enum.TryParse<HandCamSave>(cs, out var hs) ? hs : cur.HandCamSave,
            HandCams = CamSlots(cur.HandCams),
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
        CamPanel.Visibility = CamLeft.Visibility = CamDivider.Visibility = v;
    }

    void OnCamSave(object sender, RoutedEventArgs e) => Save();

    void OnCamAdd(object sender, RoutedEventArgs e)
    {
        if (!CamAdd.IsEnabled) return;
        AddCamCard(new HandCamSlot(Y: Math.Min(100, 35 * _cards.Count)));
        UpdateCamUi();
        Save();
    }

    sealed class CamCard
    {
        public required StackPanel Root;
        public required TextBlock Title;
        public required ComboBox Devices;
        public required Slider Size;
        public double X, Y;
        public required Border Marker;
        public required TextBlock Number;
    }

    readonly List<CamCard> _cards = [];
    List<HandCamDevice> _devices = [];
    static readonly string[] CamColors = ["AccentFill", "Green", "Orange"];

    HandCamSlots CamSlots(HandCamSlots cur)
    {
        var next = _cards.Select(c => c.Devices.SelectedItem is HandCamDevice d
            ? new HandCamSlot(d.Name, d.Number, (int)c.Size.Value, (int)Math.Round(c.X), (int)Math.Round(c.Y))
            : new HandCamSlot("", 0, (int)c.Size.Value, (int)Math.Round(c.X), (int)Math.Round(c.Y)));
        var list = new HandCamSlots(); list.AddRange(next);
        return list.Equals(cur) ? cur : list;
    }

    void AddCamCard(HandCamSlot slot)
    {
        var root = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 64 });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Foreground = (Brush)FindResource("Fg"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = (Brush)FindResource("Red"), ToolTip = "Remove", Style = (Style)FindResource("SheetClose"), Width = 34, Height = 30 };
        var devices = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        var size = new Slider { Minimum = 10, Maximum = 40, Value = slot.Size, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = new Thickness(12, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Size") };
        Grid.SetColumn(devices, 1); Grid.SetColumn(size, 2); Grid.SetColumn(remove, 3);
        head.Children.Add(title); head.Children.Add(devices); head.Children.Add(size); head.Children.Add(remove);
        root.Children.Add(head);

        var number = new TextBlock { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        var marker = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.85, Cursor = Cursors.SizeAll, Child = number };
        var card = new CamCard { Root = root, Title = title, Devices = devices, Size = size, X = slot.X, Y = slot.Y, Number = number, Marker = marker };
        _cards.Add(card);
        CamCanvas.Children.Add(marker);
        CamCards.Children.Add(root);

        var items = new List<HandCamDevice>(_devices);
        var saved = slot.Device.Length > 0 ? new HandCamDevice(slot.Device, slot.Number) : null;
        if (saved != null && !items.Contains(saved)) items.Insert(0, saved);
        devices.ItemsSource = items;
        devices.SelectedItem = saved;
        if (saved == null) devices.SelectedItem = items.FirstOrDefault(d => !_cards.Any(c => c != card && Equals(c.Devices.SelectedItem, d)));

        void Changed(object? s, RoutedPropertyChangedEventArgs<double> e) { UpdateCamUi(); Save(); }
        size.ValueChanged += Changed;
        Point grab = default;
        marker.MouseLeftButtonDown += (_, e) => { marker.CaptureMouse(); grab = e.GetPosition(marker); Panel.SetZIndex(marker, 1); e.Handled = true; };
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
        devices.SelectionChanged += (_, _) => { UpdateCamUi(); Save(); };
        remove.Click += (_, _) =>
        {
            _cards.Remove(card);
            CamCards.Children.Remove(root);
            CamCanvas.Children.Remove(marker);
            UpdateCamUi();
            Save();
        };
    }

    const double CamW = 240, CamH = 135, SnapDistance = 6;
    readonly List<UIElement> _guides = [];

    /// <summary>Preview box of a cam (the cam keeps 16:9), centered on its position and pulled inside the frame.</summary>
    static (double L, double T, double W, double H) CamBox(CamCard c)
    {
        var w = CamW * c.Size.Value / 100; var h = w * 9 / 16;
        return (Math.Clamp(CamW * c.X / 100 - w / 2, 0, CamW - w), Math.Clamp(CamH * c.Y / 100 - h / 2, 0, CamH - h), w, h);
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

    /// <summary>Titles, slider readouts, preview boxes and the Add button after any card or slider change.</summary>
    void UpdateCamUi()
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            c.Title.Text = (i + 1).ToString();
            var (l, t, w, h) = CamBox(c);
            c.Marker.Width = w; c.Marker.Height = h;
            c.Marker.Background = (Brush)FindResource(CamColors[i % CamColors.Length]);
            c.Number.Text = (i + 1).ToString();
            Canvas.SetLeft(c.Marker, l); Canvas.SetTop(c.Marker, t);
        }
        CamAdd.IsEnabled = _cards.Count < HandCamSlot.Max && _devices.Any(d => !_cards.Any(c => Equals(c.Devices.SelectedItem, d)));
    }

    async Task RefreshCamDevices()
    {
        _devices = await Task.Run(() => AppHost.EnsureObs() ? HandCamDeviceList.List() : []);
        var was = _loading; _loading = true;
        foreach (var c in _cards)
        {
            var sel = c.Devices.SelectedItem as HandCamDevice;
            var items = new List<HandCamDevice>(_devices);
            if (sel != null && !items.Contains(sel)) items.Insert(0, sel);
            c.Devices.ItemsSource = items;
            c.Devices.SelectedItem = sel;
        }
        foreach (var c in _cards.Where(c => c.Devices.SelectedItem == null))
            c.Devices.SelectedItem = _devices.FirstOrDefault(d => !_cards.Any(o => Equals(o.Devices.SelectedItem, d)));
        _loading = was;
        UpdateCamUi();
        Save();
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
        if (DateTime.UtcNow - _lastReport < TimeSpan.FromSeconds(60))
            ReportBtn.Content = L.T("Please try again in a minute");
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
        return EnvironmentInfo.Describe(new(res, s.VideoQuality.ToString(), s.VideoFps, s.HandCamEnabled, s.HandCams.Count));
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
