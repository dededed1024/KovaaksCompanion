using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Update;

namespace KovaaksCompanion.App;

/// <summary>"Update available" popup (scrim + card): Update downloads, verifies and installs the new exe in place; without a verifiable asset it opens the download in the browser.</summary>
public partial class UpdateView : UserControl
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    ReleaseInfo? _release;
    bool _open, _busy;
    int _tok;

    public UpdateView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Fit();
    }

    /// <summary>Scales the card and its notes area with the window, within sensible bounds.</summary>
    void Fit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        Card.Width = Math.Clamp(ActualWidth * 0.4, 400, 640);
        NotesScroll.MaxHeight = Math.Max(260, ActualHeight * 0.4);
    }

    public bool IsOpen => _open;

    /// <summary>Shows the popup for <paramref name="release"/>; with <paramref name="whatsNew"/> it only shows the notes of the version that just got installed.</summary>
    public void Open(ReleaseInfo release, bool whatsNew = false)
    {
        if (_open) return;
        _release = release;
        _busy = false;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
        UpdateBtn.Visibility = whatsNew ? Visibility.Collapsed : Visibility.Visible;
        LaterBtn.Content = L.T(whatsNew ? "Close" : "Later");
        TitleText.Text = L.T(whatsNew ? "What's new" : "Update available");
        Status.Visibility = Visibility.Collapsed;
        Versions.Text = whatsNew ? $"v{release.Version.ToString(3)}" : $"v{App.CurrentVersion.ToString(3)} → v{release.Version.ToString(3)}";
        Notes.Text = release.Notes;
        NotesScroll.Visibility = release.Notes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _open = true;
        var tok = ++_tok;
        Scrim.BeginAnimation(OpacityProperty, null); Scrim.Opacity = 0;
        Card.BeginAnimation(OpacityProperty, null); Card.Opacity = 0;
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Scale.ScaleX = Scale.ScaleY = 0.94;
        Visibility = Visibility.Visible;
        UpdateLayout();
        Fit();
        Popups.Opened(this, this, Close, () => { });
        UpdateLayout();
        Animate(true, tok);
    }

    /// <summary>Closes the popup; false when it was not open.</summary>
    public bool Close()
    {
        if (!_open || _busy) return false;
        _open = false;
        Popups.Closed(this);
        Animate(false, ++_tok);
        return true;
    }

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnLater(object sender, RoutedEventArgs e) => Close();

    async void OnUpdate(object sender, RoutedEventArgs e)
    {
        var release = _release;
        var exe = Environment.ProcessPath;
        if (release == null || exe == null || !SelfUpdate.CanInstall(release)) { OpenInBrowser(); return; }
        _busy = true;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = false;
        ShowStatus(L.T("Downloading…"), false);
        try
        {
            var path = await SelfUpdate.Download(Http, release, exe, new Progress<double>(p => ShowStatus(L.F("Downloading… {0}%", (int)(p * 100)), false)));
            ShowStatus(L.T("Restarting…"), false);
            SelfUpdate.Install(exe, path);
        }
        catch (Exception ex)
        {
            AppLog.Write("update", "install failed: " + ex.Message);
            _busy = false;
            UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
            ShowStatus(L.T("Update failed. Download it from the release page instead."), true);
            OpenInBrowser();
        }
    }

    void ShowStatus(string text, bool error)
    {
        Status.Text = text;
        Status.Foreground = (Brush)FindResource(error ? "Red" : "Dim");
        Status.Visibility = Visibility.Visible;
    }

    void OpenInBrowser()
    {
        Process.Start(new ProcessStartInfo(UpdateCheck.LatestDownload) { UseShellExecute = true })?.Dispose();
        Close();
    }

    void Animate(bool show, int tok)
    {
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
        var ms = TimeSpan.FromMilliseconds(show ? 260 : 180);
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 200 : 180)), HandoffBehavior.SnapshotAndReplace);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, ms) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        var sx = new DoubleAnimation(show ? 1 : 0.94, ms) { EasingFunction = ease };
        var sy = sx.Clone();
        if (!show) sx.Completed += (_, _) => { if (tok == _tok && !_open) Visibility = Visibility.Collapsed; };
        Scale.BeginAnimation(ScaleTransform.ScaleXProperty, sx, HandoffBehavior.SnapshotAndReplace);
        Scale.BeginAnimation(ScaleTransform.ScaleYProperty, sy, HandoffBehavior.SnapshotAndReplace);
    }
}
