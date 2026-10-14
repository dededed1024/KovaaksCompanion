using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace KovaaksCompanion.App;

/// <summary>
/// "Start with Windows": a shortcut in the user's Startup folder that starts the app minimized to the tray.
/// Removes the legacy scheduled task and Run value.
/// </summary>
public static class Autostart
{
    const string TaskName = "KovaaksCompanion", LegacyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Command-line flag that starts minimized to the tray.</summary>
    public const string TrayArg = "--tray";

    static string ExePath => Environment.ProcessPath ?? "";

    static string LinkPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), TaskName + ".lnk");

    public static bool IsOn() => File.Exists(LinkPath);

    public static void Apply(bool on)
    {
        try
        {
            RemoveLegacy();
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
        else RemoveLegacy();
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

    static void RemoveLegacy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LegacyKeyPath, writable: true);
            k?.DeleteValue(TaskName, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException) { }
        try { Run("/Delete", "/TN", TaskName, "/F"); }
        catch (System.ComponentModel.Win32Exception) { }
    }

    static void Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }
}
