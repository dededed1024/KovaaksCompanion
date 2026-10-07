using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Update;

namespace KovaaksCompanion.App;

/// <summary>"Update available" popup (scrim + card): downloads the new exe, swaps it in and restarts into it.</summary>
public partial class UpdateView : UserControl
{
    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    ReleaseInfo? _release;
    bool _open, _busy, _openPage;
    int _tok;

    public UpdateView() => InitializeComponent();

    public bool IsOpen => _open;

    /// <summary>Shows the popup for <paramref name="release"/>.</summary>
    public void Open(ReleaseInfo release)
    {
        if (_open) return;
        _release = release; _busy = false; _openPage = release.DownloadUrl == null;
        Versions.Text = $"v{App.CurrentVersion.ToString(3)} → v{release.Version.ToString(3)}";
        Track.Visibility = Status.Visibility = Visibility.Collapsed;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = true;
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

    /// <summary>Closes the popup; false when it was not open. Stays open while downloading.</summary>
    public bool Close()
    {
        if (!_open) return false;
        if (_busy) return true;
        _open = false;
        Animate(false, ++_tok);
        return true;
    }

    void OnScrim(object sender, MouseButtonEventArgs e) => Close();
    void OnLater(object sender, RoutedEventArgs e) => Close();

    async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_release == null) return;
        if (_openPage) { OpenRelease(); Close(); return; }
        _busy = true;
        UpdateBtn.IsEnabled = LaterBtn.IsEnabled = false;
        Track.Visibility = Status.Visibility = Visibility.Visible;
        FillScale.ScaleX = 0;
        Status.Text = "Downloading… 0%";
        try
        {
            await Install(_release.DownloadUrl!);
            Status.Text = "Restarting…";
        }
        catch (Exception ex)
        {
            AppLog.Write("update", "failed: " + ex.Message);
            _busy = false; _openPage = true;
            Track.Visibility = Visibility.Collapsed;
            Status.Text = "Update failed";
            LaterBtn.IsEnabled = UpdateBtn.IsEnabled = true;
        }
    }

    void OpenRelease() => Process.Start(new ProcessStartInfo(_release?.Url ?? UpdateCheck.LatestPage) { UseShellExecute = true });

    /// <summary>Downloads next to the running exe, swaps the files and starts the new one with --replace; restores the old exe on failure.</summary>
    async Task Install(string url)
    {
        var exe = Environment.ProcessPath;
        if (exe == null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("not a standalone exe");
        var dir = Path.GetDirectoryName(exe)!;
        var fresh = Path.Combine(dir, "KovaaksCompanion.new.exe");
        var old = Path.Combine(dir, App.OldExeName);
        var renamed = false;
        try
        {
            using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                res.EnsureSuccessStatusCode();
                var total = res.Content.Headers.ContentLength ?? 0;
                await using var src = await res.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { var f = (double)done / total; FillScale.ScaleX = f; Status.Text = $"Downloading… {(int)(f * 100)}%"; }
                }
            }
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old); renamed = true;
            File.Move(fresh, exe);
            Process.Start(new ProcessStartInfo(exe, App.ReplaceArg) { UseShellExecute = false, WorkingDirectory = dir })?.Dispose();
        }
        catch
        {
            try
            {
                if (renamed) { if (File.Exists(exe)) File.Delete(exe); File.Move(old, exe); }
                if (File.Exists(fresh)) File.Delete(fresh);
            }
            catch { }
            throw;
        }
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
