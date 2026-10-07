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
    }

    public void Detach() { }

    public bool IsOpen => _open;

    void Load()
    {
        _loading = true;
        var s = _host.Settings;
        Kovaaks.Text = s.KovaaksPath; Data.Text = s.DataRoot; Steam.Text = s.SteamId;
        foreach (RadioButton r in QualityBar.Children) r.IsChecked = (string)r.Tag == s.VideoQuality.ToString();
        AutoStart.IsChecked = Autostart.IsOn();
        KovaaksNote.Visibility = Visibility.Collapsed;
        _loading = false;
        Validate(null, null);
    }

    bool SteamValid()
    {
        var steam = Steam.Text.Trim();
        return steam.Length == 0 || steam.Length == 17 && steam.All(char.IsAsciiDigit);
    }

    void Validate(object? sender, RoutedEventArgs? e) => Check(Steam, SteamError, SteamValid(), "Must be 17 digits");

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
            KovaaksPath = Kovaaks.Text.Trim(), DataFolder = Data.Text.Trim() == cur.DataRoot ? cur.DataFolder : Data.Text.Trim(), SteamId = SteamValid() ? Steam.Text.Trim() : cur.SteamId,
            StartWithWindows = AutoStart.IsChecked == true,
            VideoQuality = QualityBar.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string t
                && Enum.TryParse<VideoQuality>(t, out var q) ? q : cur.VideoQuality,
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

    void OnAutoStart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Autostart.Apply(AutoStart.IsChecked == true);
        Save();
    }

    void OnDetectKovaaks(object sender, RoutedEventArgs e)
    {
        var found = PathDetector.FindKovaaks();
        KovaaksNote.Text = "Not found";
        KovaaksNote.Visibility = found == null ? Visibility.Visible : Visibility.Collapsed;
        if (found == null) return;
        Kovaaks.Text = found;
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
