using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace KovaaksCompanion.App;

/// <summary>
/// "Start with Windows": a logon scheduled task with highest privileges (the app needs admin, and an HKCU\...\Run entry
/// is skipped at logon for apps that require elevation). Removes the legacy Run value.
/// </summary>
public static class Autostart
{
    const string TaskName = "KovaaksCompanion", LegacyKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Command-line flag that starts minimized to the tray.</summary>
    public const string TrayArg = "--tray";

    static string ExePath => Environment.ProcessPath ?? "";

    /// <summary>The task's registered definition (XML) or null when there is none.</summary>
    static string? RegisteredXml()
    {
        var (code, output) = Run("/Query", "/TN", TaskName, "/XML");
        return code == 0 ? output : null;
    }

    public static bool IsOn() => RegisteredXml() != null;

    public static void Apply(bool on)
    {
        try
        {
            RemoveLegacy();
            if (!on) { Run("/Delete", "/TN", TaskName, "/F"); return; }
            var xml = Path.Combine(Path.GetTempPath(), "kc-autostart-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xml, TaskXml(), System.Text.Encoding.Unicode);
                Run("/Create", "/TN", TaskName, "/XML", xml, "/F");
            }
            finally { try { File.Delete(xml); } catch { } }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Rewrites the task when the setting is on but the exe moved or the task is missing.</summary>
    public static void Refresh(bool wanted)
    {
        if (!wanted) return;
        var xml = RegisteredXml();
        if (xml == null || !xml.Contains(SecurityElement.Escape(ExePath), StringComparison.OrdinalIgnoreCase)) Apply(true);
        else RemoveLegacy();
    }

    static string TaskXml()
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(ExePath)}</Command><Arguments>{TrayArg}</Arguments></Exec></Actions>
            </Task>
            """;
    }

    static void RemoveLegacy()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LegacyKeyPath, writable: true);
            k?.DeleteValue(TaskName, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException) { }
    }

    static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, o);
    }
}
