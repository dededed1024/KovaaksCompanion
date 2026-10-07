using Microsoft.Win32;

namespace KovaaksCompanion.App;

/// <summary>"Start with Windows": a value under HKCU\...\Run pointing at this exe.</summary>
public static class Autostart
{
    const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "KovaaksCompanion";

    /// <summary>Command-line flag that starts minimized to the tray.</summary>
    public const string TrayArg = "--tray";

    static string Command => $"\"{Environment.ProcessPath}\" {TrayArg}";

    public static string? Registered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
        return k?.GetValue(ValueName) as string;
    }

    public static bool IsOn() => Registered() != null;

    public static void Apply(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (on) k.SetValue(ValueName, Command);
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException) { }
    }

    /// <summary>Rewrites the registered path when the setting is on but the exe moved.</summary>
    public static void Refresh(bool wanted)
    {
        if (wanted && Registered() != Command) Apply(true);
    }
}
