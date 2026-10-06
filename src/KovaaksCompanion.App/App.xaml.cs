using System.Windows;

namespace KovaaksCompanion.App;

public partial class App : Application
{
    Mutex? _single;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\KovaaksCompanion.SingleInstance", out var first);
        if (!first) { _single.Dispose(); _single = null; Shutdown(); return; }
        _host = new AppHost(this);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        if (_single != null) { _single.ReleaseMutex(); _single.Dispose(); }
        base.OnExit(e);
    }
}
