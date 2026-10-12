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
        DeleteBeaten.IsChecked = s.DeleteBeatenRuns;
        KeepPb.IsChecked = s.KeepPersonalBests;
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

    void Validate(object? sender, RoutedEventArgs? e)
    {
        Check(StorageBox, StorageError, StorageValid(out _), L.T("Enter 0 or more"));
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
            DeleteBeatenRuns = DeleteBeaten.IsChecked == true,
            KeepPersonalBests = KeepPb.IsChecked == true,
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
        AddCamCard(new HandCamSlot(Top: Math.Min(80, 20 + 25 * _cards.Count)));
        UpdateCamUi();
        Save();
    }

    sealed class CamCard
    {
        public required StackPanel Root;
        public required TextBlock Title;
        public required ComboBox Devices;
        public required Slider Size, Height, Right, Top;
        public required TextBlock SizeText, HeightText, RightText, TopText;
        public required Border Marker;
    }

    readonly List<CamCard> _cards = [];
    List<HandCamDevice> _devices = [];
    static readonly string[] CamColors = ["AccentFill", "Green", "Orange"];

    HandCamSlots CamSlots(HandCamSlots cur)
    {
        var next = _cards.Select(c => c.Devices.SelectedItem is HandCamDevice d
            ? new HandCamSlot(d.Name, d.Number, (int)c.Size.Value, (int)c.Right.Value, (int)c.Top.Value, (int)c.Height.Value)
            : new HandCamSlot("", 0, (int)c.Size.Value, (int)c.Right.Value, (int)c.Top.Value, (int)c.Height.Value));
        var list = new HandCamSlots(); list.AddRange(next);
        return list.Equals(cur) ? cur : list;
    }

    void AddCamCard(HandCamSlot slot)
    {
        var root = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock { Foreground = (Brush)FindResource("Fg"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var remove = new Button { Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16, Foreground = (Brush)FindResource("Red"), ToolTip = "Remove", Style = (Style)FindResource("SheetClose"), Width = 34, Height = 30 };
        Grid.SetColumn(remove, 1);
        head.Children.Add(title); head.Children.Add(remove);
        var devices = new ComboBox { Margin = new Thickness(0, 6, 0, 0) };
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        Slider Row(int r, string label, double min, double max, double val, out TextBlock text)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var margin = new Thickness(0, r == 0 ? 0 : 8, 0, 0);
            var l = new TextBlock { Text = label, Foreground = (Brush)FindResource("Fg"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, margin.Top, 0, 0) };
            var sl = new Slider { Minimum = min, Maximum = max, Value = val, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true, TickFrequency = 1, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
            text = new TextBlock { Style = (Style)FindResource("Caption"), Margin = margin, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(l, r); Grid.SetRow(sl, r); Grid.SetRow(text, r);
            Grid.SetColumn(sl, 1); Grid.SetColumn(text, 2);
            grid.Children.Add(l); grid.Children.Add(sl); grid.Children.Add(text);
            return sl;
        }
        var size = Row(0, L.T("Width"), 10, 40, slot.Size, out var st);
        var height = Row(1, L.T("Height"), 10, 40, slot.Height, out var ht);
        var right = Row(2, L.T("From right"), 0, 100, slot.Right, out var rt);
        var top = Row(3, L.T("From top"), 0, 100, slot.Top, out var tt);
        root.Children.Add(head); root.Children.Add(devices); root.Children.Add(grid);

        var marker = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.85 };
        var card = new CamCard { Root = root, Title = title, Devices = devices, Size = size, Height = height, Right = right, Top = top, SizeText = st, HeightText = ht, RightText = rt, TopText = tt, Marker = marker };
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
        size.ValueChanged += Changed; height.ValueChanged += Changed; right.ValueChanged += Changed; top.ValueChanged += Changed;
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

    /// <summary>Titles, slider readouts, preview boxes and the Add button after any card or slider change.</summary>
    void UpdateCamUi()
    {
        const double W = 240, H = 135;
        for (int i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            c.Title.Text = L.F("Webcam {0}", i + 1);
            c.SizeText.Text = $"{(int)c.Size.Value}%"; c.HeightText.Text = $"{(int)c.Height.Value}%"; c.RightText.Text = $"{(int)c.Right.Value}%"; c.TopText.Text = $"{(int)c.Top.Value}%";
            var w = W * c.Size.Value / 100; var h = H * c.Height.Value / 100;
            c.Marker.Width = w; c.Marker.Height = h;
            c.Marker.Background = (Brush)FindResource(CamColors[i % CamColors.Length]);
            Canvas.SetLeft(c.Marker, Math.Max(0, Math.Min(W - w, W - w - W * c.Right.Value / 100)));
            Canvas.SetTop(c.Marker, Math.Max(0, Math.Min(H - h, H * c.Top.Value / 100)));
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
            ReportBtn.Content = L.T("Wait a minute");
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
