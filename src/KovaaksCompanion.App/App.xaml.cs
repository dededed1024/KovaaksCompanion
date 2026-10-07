using System.Runtime.InteropServices;
using System.Windows;

namespace KovaaksCompanion.App;

public partial class App : Application
{
    const string ShowEventName = @"Local\KovaaksCompanion.ShowMain";

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    Mutex? _single;
    EventWaitHandle? _showEvent;
    RegisteredWaitHandle? _showWait;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\KovaaksCompanion.SingleInstance", out var first);
        if (!first)
        {
            _single.Dispose(); _single = null;
            try
            {
                using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
                AllowSetForegroundWindow(-1);
                ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _host?.ShowMain("Stats"), null, Timeout.Infinite, false);
        _host = new AppHost(this);
        // Autostart launches with --tray; a manual launch opens the Stats window.
        _host.Start(showMain: !e.Args.Contains(Autostart.TrayArg));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _host?.Dispose();
        if (_single != null) { _single.ReleaseMutex(); _single.Dispose(); }
        base.OnExit(e);
    }
}
