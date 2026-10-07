using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KovaaksCompanion.App;

/// <summary>The single app window: the Stats page, with the replay and settings popups floating above it.</summary>
public partial class MainWindow : Window
{
    readonly StatsView _stats;
    readonly ReplayView _replay;
    readonly SettingsView _settings;

    public MainWindow(AppHost host)
    {
        InitializeComponent();
        Backdrop.Apply(this);
        _stats = new StatsView(host);
        _replay = new ReplayView(host);
        _settings = new SettingsView(host);
        Pages.Children.Add(_stats);
        Root.Children.Add(_replay); // above the pages, below the title bar (ZIndex 1), so the caption buttons keep working
        Root.Children.Add(_settings);
        PreviewKeyDown += OnNavKey;
        PreviewMouseDown += (_, e) => { if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); } };
        Closed += (_, _) => { _stats.Detach(); _replay.Detach(); _settings.Detach(); };
        StateChanged += (_, _) => SyncCaption();
    }

    // A chrome window overshoots the screen by the resize border when maximized; pad it back in.
    void SyncCaption()
    {
        var max = WindowState == WindowState.Maximized;
        Root.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        MaxButton.Content = max ? "" : "";
        MaxButton.ToolTip = max ? "Restore" : "Maximize";
    }

    void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnNavKey(object sender, KeyEventArgs e)
    {
        if (!_settings.IsOpen && _replay.HandleKey(e)) return;
        var back = (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) || (e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        if (back) e.Handled = GoBack();
    }

    /// <summary>Closes the topmost popup or overlay; false when nothing was open.</summary>
    bool GoBack() => _settings.Close() || _replay.Close() || _stats.CloseOverlay();

    void OnGear(object sender, RoutedEventArgs e)
    {
        _replay.Close();
        _settings.Open();
    }

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

    /// <summary>Opens the replay popup on a play session overview.</summary>
    public void ShowSession(KovaaksCompanion.Core.Library.PlaySession session, Point? origin)
    {
        _settings.Close();
        var o = origin is { } p ? this.TranslatePoint(p, _replay) : (Point?)null;
        _replay.ShowSession(_stats.SheetData(), session, o);
    }

    /// <summary>Opens the replay popup with the run ending at <paramref name="end"/> selected; false when it was not recorded.</summary>
    public bool ShowRun(string scenario, DateTime end)
    {
        _settings.Close();
        return _replay.ShowRun(_stats.SheetData(), scenario, end);
    }

    /// <summary>Opens the most recent recorded session's run; false (window only) when nothing was recorded.</summary>
    public bool ShowLatestReplay()
    {
        _settings.Close();
        return _replay.ShowLatest(_stats.SheetData());
    }
}
