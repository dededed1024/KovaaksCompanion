using System.IO;
using System.Security;

namespace KovaaksCompanion.App;

/// <summary>
/// "Start with Windows": a shortcut in the user's Startup folder that starts the app minimized to the tray.
/// </summary>
public static class Autostart
{
    const string TaskName = "KovaaksCompanion";

    /// <summary>Command-line flag that starts minimized to the tray.</summary>
    public const string TrayArg = "--tray";

    static string ExePath => Environment.ProcessPath ?? "";

    static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), TaskName + ".lnk");

    public static bool IsOn() => File.Exists(LinkPath);

    public static void Apply(bool on)
    {
        try
        {
            if (!on) { File.Delete(LinkPath); return; }
            CreateLink();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Rewrites the shortcut when the setting is on but the exe moved or the shortcut is missing.</summary>
    public static void Refresh(bool wanted)
    {
        if (!wanted) return;
        if (!IsOn() || !LinkTarget().Equals(ExePath, StringComparison.OrdinalIgnoreCase)) Apply(true);
    }

    static dynamic Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;

    static string LinkTarget()
    {
        try { return (string)Shell().CreateShortcut(LinkPath).TargetPath; }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return ""; }
    }

    static void CreateLink()
    {
        var link = Shell().CreateShortcut(LinkPath);
        link.TargetPath = ExePath;
        link.Arguments = TrayArg;
        link.WorkingDirectory = Path.GetDirectoryName(ExePath) ?? "";
        link.Save();
    }
}
