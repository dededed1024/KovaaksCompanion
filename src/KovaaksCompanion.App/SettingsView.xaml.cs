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

    void OnGameState() => Dispatcher.InvokeAsync(UpdateCamLock);

    /// <summary>Webcam settings are read when recording starts, so they are locked while a game session is live.</summary>
    void UpdateCamLock() => CamSection.IsEnabled = !_host.GameActive;

    public bool IsOpen => _open;

    void Load()
    {
        _loading = true;
        var s = _host.Settings;
        Kovaaks.Text = s.KovaaksPath; Data.Text = s.DataRoot; Steam.Text = s.EffectiveSteamId;
        foreach (RadioButton r in QualityBar.Children) r.IsChecked = (string)r.Tag == s.VideoQuality.ToString();
        FpsBox.Text = s.VideoFps.ToString();
        AutoStart.IsChecked = Autostart.IsOn();
        CamOn.IsChecked = s.HandCamEnabled;
        CamPanel.Visibility = s.HandCamEnabled ? Visibility.Visible : Visibility.Collapsed;
        foreach (RadioButton r in CamSaveBar.Children) r.IsChecked = (string)r.Tag == s.HandCamSave.ToString();
        CamCards.Children.Clear(); CamCanvas.Children.Clear(); _cards.Clear();
        foreach (var slot in s.HandCams) AddCamCard(slot);
        UpdateCamUi();
        UpdateCamLock();
        _loading = false;
        if (s.HandCamEnabled) _ = RefreshCamDevices();
        Validate(null, null);
        if (Kovaaks.Text.Length == 0 && PathDetector.FindKovaaks() is { } found) { Kovaaks.Text = found; Save(); }
    }

    bool SteamValid()
    {
        var steam = Steam.Text.Trim();
        return steam.Length == 0 || steam.Length == 17 && steam.All(char.IsAsciiDigit);
    }

    bool FpsValid(out int fps) => int.TryParse(FpsBox.Text, out fps) && fps is >= 30 and <= 1000;

    void Validate(object? sender, RoutedEventArgs? e)
    {
        Check(Steam, SteamError, SteamValid(), "Must be 17 digits");
        Check(FpsBox, FpsError, FpsValid(out _), "Enter 30–1000");
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
        if (_loading) return;
        var cur = _host.Settings;
        var next = cur with
        {
            KovaaksPath = Kovaaks.Text.Trim(), DataFolder = Data.Text.Trim() == cur.DataRoot ? cur.DataFolder : Data.Text.Trim(), SteamId = !SteamValid() || Steam.Text.Trim() == cur.EffectiveSteamId ? cur.SteamId : Steam.Text.Trim(),
            StartWithWindows = AutoStart.IsChecked == true,
            VideoQuality = QualityBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string t
                && Enum.TryParse<VideoQuality>(t, out var q) ? q : cur.VideoQuality,
            VideoFps = FpsValid(out var fps) ? fps : cur.VideoFps,
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
        CamPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            if (_cards.Count == 0) AddCamCard(new HandCamSlot());
            UpdateCamUi();
            _ = RefreshCamDevices();
        }
        Save();
    }

    void OnCamSave(object sender, RoutedEventArgs e) => Save();

    void OnCamAdd(object sender, RoutedEventArgs e)
    {
        if (_cards.Count >= HandCamSlot.Max) return;
        AddCamCard(new HandCamSlot(Top: Math.Min(80, 20 + 25 * _cards.Count)));
        UpdateCamUi();
        Save();
    }

    sealed class CamCard
    {
        public required StackPanel Root;
        public required TextBlock Title;
        public required ComboBox Devices;
        public required Slider Size, Right, Top;
        public required TextBlock SizeText, RightText, TopText;
        public required Border Marker;
    }

    readonly List<CamCard> _cards = [];
    List<HandCamDevice> _devices = [];
    static readonly string[] CamColors = ["AccentFill", "Green", "Orange"];

    HandCamSlots CamSlots(HandCamSlots cur)
    {
        var next = _cards.Select(c => c.Devices.SelectedItem is HandCamDevice d
            ? new HandCamSlot(d.Name, d.Number, (int)c.Size.Value, (int)c.Right.Value, (int)c.Top.Value)
            : new HandCamSlot("", 0, (int)c.Size.Value, (int)c.Right.Value, (int)c.Top.Value));
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
        var size = Row(0, "Size", 10, 50, slot.Size, out var st);
        var right = Row(1, "From right", 0, 100, slot.Right, out var rt);
        var top = Row(2, "From top", 0, 100, slot.Top, out var tt);
        root.Children.Add(head); root.Children.Add(devices); root.Children.Add(grid);

        var marker = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.85 };
        var card = new CamCard { Root = root, Title = title, Devices = devices, Size = size, Right = right, Top = top, SizeText = st, RightText = rt, TopText = tt, Marker = marker };
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
        size.ValueChanged += Changed; right.ValueChanged += Changed; top.ValueChanged += Changed;
        devices.SelectionChanged += (_, _) => Save();
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
            c.Title.Text = $"Webcam {i + 1}";
            c.SizeText.Text = $"{(int)c.Size.Value}%"; c.RightText.Text = $"{(int)c.Right.Value}%"; c.TopText.Text = $"{(int)c.Top.Value}%";
            var w = W * c.Size.Value / 100; var h = w * 9 / 16;
            c.Marker.Width = w; c.Marker.Height = h;
            c.Marker.Background = (Brush)FindResource(CamColors[i % CamColors.Length]);
            Canvas.SetLeft(c.Marker, Math.Max(0, Math.Min(W - w, W - w - W * c.Right.Value / 100)));
            Canvas.SetTop(c.Marker, Math.Max(0, Math.Min(H - h, H * c.Top.Value / 100)));
        }
        CamAdd.Visibility = _cards.Count >= HandCamSlot.Max ? Visibility.Collapsed : Visibility.Visible;
    }

    async Task RefreshCamDevices()
    {
        var ffmpeg = AppHost.ResolveFfmpeg();
        if (ffmpeg is null) return;
        _devices = await HandCamDeviceList.ListAsync(ffmpeg);
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

    const string CheckText = "Check for updates";

    void OnAutoStartLabel(object sender, MouseButtonEventArgs e) => AutoStart.IsChecked = AutoStart.IsChecked != true;

    async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        var tok = _tok;
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = "Checking…";
        var latest = await UpdateCheck.Fetch(Http);
        if (tok != _tok) return;
        if (latest != null && UpdateCheck.IsNewer(latest, Current))
        {
            ResetCheck();
            _host.ShowUpdate(latest);
            return;
        }
        CheckBtn.Content = latest == null ? "Could not check" : "Up to date";
        await Task.Delay(3000);
        if (tok == _tok) ResetCheck();
    }

    void ResetCheck() { CheckBtn.Content = CheckText; CheckBtn.IsEnabled = true; }

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
        VersionCaption.Text = $"Version {Current.ToString(3)}";
        var tok = ++_tok;
        ResetCheck();
        Scrim.BeginAnimation(OpacityProperty, null); Scrim.Opacity = 0;
        Card.BeginAnimation(OpacityProperty, null); Card.Opacity = 0;
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale.ScaleX = Scale.ScaleY = 0.94;
        Visibility = Visibility.Visible;
        UpdateLayout();
        PageScroll.ScrollToTop();
        if (_host.UpdateAvailable)
        {
            CheckBtn.SetResourceReference(BackgroundProperty, "Red");
            CheckBtn.Foreground = Brushes.White;
            CheckBtn.BringIntoView();
        }
        else { CheckBtn.ClearValue(BackgroundProperty); CheckBtn.ClearValue(ForegroundProperty); }
        Animate(true, tok);
    }

    /// <summary>Closes the popup; false when it was not open.</summary>
    public bool Close()
    {
        if (!_open) return false;
        _open = false;
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
