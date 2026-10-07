using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core.Update;

namespace KovaaksCompanion.App;

/// <summary>"Update available" popup (scrim + card): Update opens the release page in the browser.</summary>
public partial class UpdateView : UserControl
{
    ReleaseInfo? _release;
    bool _open;
    int _tok;

    public UpdateView() => InitializeComponent();

    public bool IsOpen => _open;

    /// <summary>Shows the popup for <paramref name="release"/>.</summary>
    public void Open(ReleaseInfo release)
    {
        if (_open) return;
        _release = release;
        Versions.Text = $"v{App.CurrentVersion.ToString(3)} → v{release.Version.ToString(3)}";
        _open = true;
        var tok = ++_tok;
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

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnLater(object sender, RoutedEventArgs e) => Close();

    void OnUpdate(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(_release?.Url ?? UpdateCheck.LatestPage) { UseShellExecute = true })?.Dispose();
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
