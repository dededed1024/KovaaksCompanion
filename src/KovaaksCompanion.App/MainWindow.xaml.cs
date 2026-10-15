using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using KovaaksCompanion.Core;

namespace KovaaksCompanion.App;

/// <summary>The single app window: the Stats page, with the replay and settings popups floating above it.</summary>
public partial class MainWindow : Window
{
    readonly AppHost _host;
    StatsView _stats;
    ReplayView _replay;
    readonly SettingsView _settings;
    readonly UpdateView _update = new();

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Popups.Reset();
        Backdrop.Apply(this);
        ApplyScale(host.Settings.UiScale);
        ApplyGrain(host.Settings.Grain);
        _stats = new StatsView(host);
        _replay = new ReplayView(host);
        _settings = new SettingsView(host);
        _stats.ConfettiLayer = ConfettiLayer;
        Pages.Children.Add(_stats);
        Root.Children.Add(_replay); // above the pages, below the title bar (ZIndex 1), so the caption buttons keep working
        Root.Children.Add(_settings);
        Root.Children.Add(_update);
        Popups.AddBase(SearchBtn, GearBtn);
        PreviewKeyDown += OnNavKey;
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); } };
        Closed += (_, _) => { _stats.Detach(); _replay.Detach(); _settings.Detach(); };
        StateChanged += (_, _) => SyncCaption();
        if (host.UpdateAvailable) MarkUpdate();
    }

    /// <summary>Scales the whole window content to <paramref name="percent"/> of the design size.</summary>
    public void ApplyScale(int percent)
    {
        var s = Math.Clamp(percent, AppSettings.MinUiScale, AppSettings.MaxUiScale) / 100.0;
        Root.LayoutTransform = new ScaleTransform(s, s);
        MinWidth = 1280 * s; MinHeight = 720 * s;
        WindowChrome.GetWindowChrome(this).CaptionHeight = 48 * s;
    }

    /// <summary>
    /// Redraws the window after a theme change without recreating it: the shared brushes are already rewritten, so the Stats page and the replay popup
    /// (which draw with brushes read at build time) are built again in place; the open settings popup stays.
    /// </summary>
    public void RefreshTheme()
    {
        Backdrop.SetDark(this);
        var index = Root.Children.IndexOf(_replay);
        _stats.Detach(); _replay.Detach();
        Pages.Children.Remove(_stats);
        Root.Children.Remove(_replay);
        _stats = new StatsView(_host) { ConfettiLayer = ConfettiLayer };
        _replay = new ReplayView(_host);
        Pages.Children.Add(_stats);
        Root.Children.Insert(index, _replay);
        _settings.RefreshAppearance();
    }

    /// <summary>Shows the film grain behind the pages at <paramref name="strength"/> (0-100); 0 removes it.</summary>
    public void ApplyGrain(int strength)
    {
        Grain.Set(strength);
        GrainLayer.Visibility = strength > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (strength <= 0) return;
        GrainLayer.Background = Grain.Brush;
        GrainLayer.Opacity = Grain.Opacity(strength);
    }

    // A chrome window overshoots the screen by the resize border when maximized; pad it back in.
    void SyncCaption()
    {
        var max = WindowState == WindowState.Maximized;
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? L.T("Restore") : L.T("Maximize");
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnNavKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; if (!Popups.Any) OnSearch(null!, null!); return; }
        if (!_settings.IsOpen && !_update.IsOpen && _replay.HandleKey(e)) return;
        var back = (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) || (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (back) e.Handled = GoBack();
    }

    /// <summary>Closes the topmost popup or overlay; false when nothing was open.</summary>
    bool GoBack() => Popups.CloseTop();

    void OnSearch(object sender, RoutedEventArgs e)
    {
        _settings.Close();
        _replay.Close();
        _stats.OpenSearch();
    }

    void OnGear(object sender, RoutedEventArgs e)
    {
        _stats.CloseSearch();
        _replay.Close();
        _settings.Open();
    }

    /// <summary>Opens the update popup above everything else.</summary>
    public void ShowUpdate(KovaaksCompanion.Core.Update.ReleaseInfo release) { MarkUpdate(); _update.Open(release); }

    /// <summary>Tints the settings button red: a newer release exists.</summary>
    public void MarkUpdate() => GearBtn.SetResourceReference(Control.ForegroundProperty, "Red");

    public void ShowPage(string page)
    {
        switch (page)
        {
            case "Stats": _settings.Close(); _replay.Close(); break;
            case "Settings": OnGear(null!, null!); break;
        }
    }

    /// <summary>Opens the replay popup on the scenario overview; <paramref name="origin"/> is the clicked point in window coordinates.</summary>
    public void ShowScenario(string scenario, Point? origin)
    {
        _settings.Close();
        var o = origin is { } p ? this.TranslatePoint(p, _replay) : (Point?)null;
        _replay.Show(_stats.SheetData(), scenario, o);
    }

    /// <summary>Opens the session popup on a play session overview.</summary>
    public void ShowSession(KovaaksCompanion.Core.Library.PlaySession session, Point? origin)
    {
        _settings.Close();
        _replay.Close();
        _stats.OpenSession(session, origin);
    }

    /// <summary>Opens the replay popup with the run ending at <paramref name="end"/> selected; false when it was not recorded.</summary>
    public bool ShowRun(string scenario, DateTime end, bool withoutRecording = false)
    {
        _settings.Close();
        return _replay.ShowRun(_stats.SheetData(), scenario, end, withoutRecording);
    }

    /// <summary>Opens the most recent recorded run; false (window only) when nothing was recorded.</summary>
    public bool ShowLatestReplay()
    {
        _settings.Close();
        return _replay.ShowLatest(_stats.SheetData());
    }
}
