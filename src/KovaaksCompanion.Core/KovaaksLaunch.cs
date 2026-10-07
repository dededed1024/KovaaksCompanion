using System.Diagnostics;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core;

/// <summary>Steam deep links that start a scenario or playlist in KovaaK's (app 824270).</summary>
public static class KovaaksLaunch
{
    const string Base = "steam://run/824270/?action=";

    public static string Scenario(string name) => $"{Base}jump-to-scenario;name={Uri.EscapeDataString(name)};mode=challenge";

    public static string Playlist(string code) => $"{Base}jump-to-playlist;sharecode={Uri.EscapeDataString(code)}";

    /// <summary>Opens <paramref name="url"/> through the shell; failures are logged, never thrown.</summary>
    public static bool Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception e)
        {
            AppLog.Write("launch", $"{url} failed: {e.Message}");
            return false;
        }
    }
}
