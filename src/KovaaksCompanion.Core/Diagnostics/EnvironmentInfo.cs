using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KovaaksCompanion.Core.Diagnostics;

/// <summary>Machine and settings summary attached to manual bug reports.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class EnvironmentInfo
{
    /// <summary>App-supplied values (UI and settings) so Core stays UI-free.</summary>
    public sealed record AppValues(string Resolution, string VideoQuality, int MaxFps, bool WebcamEnabled, int WebcamCount);

    /// <summary>Reads the OS, Windows and GPU details and formats them with the app values.</summary>
    public static string Describe(AppValues app)
    {
        return Format(RuntimeInformation.OSDescription, WindowsEdition(), CpuName(),
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, GpuNames(), app);
    }

    /// <summary>Pure formatting of the collected values, one line per item.</summary>
    public static string Format(string os, string windows, string cpu, long ramBytes, IReadOnlyList<string> gpus, AppValues app)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {os}");
        if (windows.Length > 0) sb.AppendLine($"Windows: {windows}");
        sb.AppendLine($"CPU: {(cpu.Length > 0 ? cpu : "unknown")}");
        sb.AppendLine($"RAM: {ramBytes / (1024.0 * 1024 * 1024):0.#} GB");
        sb.AppendLine($"GPU: {(gpus.Count > 0 ? string.Join("; ", gpus) : "unknown")}");
        sb.AppendLine($"Screen: {app.Resolution}");
        sb.AppendLine($"Video: {app.VideoQuality}, {app.MaxFps} FPS");
        sb.Append($"Webcam: {(app.WebcamEnabled ? "on" : "off")}, {app.WebcamCount} configured");
        return sb.ToString();
    }

    static string WindowsEdition()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (k == null) return "";
            string V(string n) => k.GetValue(n)?.ToString() ?? "";
            return string.Join(" ", new[] { V("ProductName"), V("EditionID"), V("DisplayVersion"), "build " + V("CurrentBuild") }.Where(s => s.Length > 0 && s != "build "));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    static string CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return ""; }
    }

    static List<string> GpuNames()
    {
        var names = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return names;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsAsciiDigit)))
            {
                using var k = cls.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc")?.ToString() is { Length: > 0 } d && !names.Contains(d)) names.Add(d);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("environment info", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); }
        return names;
    }
}
