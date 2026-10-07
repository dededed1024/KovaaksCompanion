using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using KovaaksCompanion.Core.Update;

namespace KovaaksCompanion.App;

public partial class App : Application
{
    /// <summary>True in the filming build (<c>-p:DemoBuild=true</c>): fixed fake data, own single-instance names, no user data written.</summary>
    public static bool Demo { get; } =
#if DEMO
        true;
#else
        false;
#endif

    static readonly string Suffix = Demo ? ".Demo" : "";
    static readonly string ShowEventName = @"Local\KovaaksCompanion.ShowMain" + Suffix;
    static readonly string QuitEventName = @"Local\KovaaksCompanion.Quit" + Suffix;
    static readonly string VersionMapName = @"Local\KovaaksCompanion.Version" + Suffix;

    /// <summary>This build's version (assembly informational version).</summary>
    public static Version CurrentVersion { get; } = UpdateCheck.ParseVersion(typeof(App).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new Version(0, 0, 0);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int dwProcessId);

    Mutex? _single;
    EventWaitHandle? _showEvent;
    RegisteredWaitHandle? _showWait;
    EventWaitHandle? _quitEvent;
    RegisteredWaitHandle? _quitWait;
    MemoryMappedFile? _versionMap;
    AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MiddleScroll.Register();
        _single =new Mutex(true, @"Local\KovaaksCompanion.SingleInstance" + Suffix, out var first);
        if (!first)
        {
            var replace = UpdateCheck.ShouldReplace(CurrentVersion, RunningVersion());
            if (replace) SignalQuit();
            if (!replace || !TakeOver(_single))
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
        }
        PublishVersion();
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Dispatcher.BeginInvoke(new Action(Shutdown)), null, Timeout.Infinite, false);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => _host?.ShowMain("Stats"), null, Timeout.Infinite, false);
        _host = new AppHost(this);
        // Autostart launches with --tray; a manual launch opens the Stats window.
        _host.Start(showMain: !e.Args.Contains(Autostart.TrayArg));
    }

    /// <summary>Waits for the previous instance to release the mutex; abandoned counts as acquired.</summary>
    static bool TakeOver(Mutex m)
    {
        try { return m.WaitOne(TimeSpan.FromSeconds(15)); }
        catch (AbandonedMutexException) { return true; }
    }

    static void SignalQuit()
    {
        try { using var q = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName); q.Set(); } catch { }
    }

    /// <summary>The running instance's published version; null when unreadable.</summary>
    static Version? RunningVersion()
    {
        try
        {
            using var map = MemoryMappedFile.OpenExisting(VersionMapName, MemoryMappedFileRights.Read);
            using var s = map.CreateViewStream(0, 0, MemoryMappedFileAccess.Read);
            var buf = new byte[64];
            var n = s.Read(buf, 0, buf.Length);
            return UpdateCheck.ParseVersion(System.Text.Encoding.UTF8.GetString(buf, 0, n).TrimEnd('\0'));
        }
        catch { return null; }
    }

    /// <summary>Publishes this version for later launches; the map lives until exit.</summary>
    void PublishVersion()
    {
        try
        {
            _versionMap = MemoryMappedFile.CreateOrOpen(VersionMapName, 64);
            using var s = _versionMap.CreateViewStream(0, 64);
            var bytes = System.Text.Encoding.UTF8.GetBytes(CurrentVersion.ToString(3));
            s.Write(new byte[64], 0, 64); s.Position = 0;
            s.Write(bytes, 0, bytes.Length);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _quitWait?.Unregister(null);
        _quitEvent?.Dispose();
        _versionMap?.Dispose();
        _host?.Dispose();
        if (_single != null) { _single.ReleaseMutex(); _single.Dispose(); }
        base.OnExit(e);
    }
}
