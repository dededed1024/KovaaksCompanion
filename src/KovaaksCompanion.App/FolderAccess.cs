using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using KovaaksCompanion.Core;

namespace KovaaksCompanion.App;

/// <summary>Grants the current user access to folders through one elevated call (one UAC prompt), so the app itself can run without administrator rights. Plugged into <see cref="AccessGuard"/>.</summary>
public static class FolderAccess
{
    public static void Register() => AccessGuard.GrantMany = GrantMany;

    /// <summary>Read (RX) or modify (M) on each folder and below only. A missing folder is created elevated first, so no ancestor (e.g. a drive root) ever gets a grant.</summary>
    public static bool GrantMany(IReadOnlyList<(string Folder, bool Write)> folders)
    {
        if (folders.Count == 0) return true;
        if (folders.Any(f => f.Folder.IndexOfAny(['"', '&', '%', '^', '|', '<', '>']) >= 0)) return false; // cmd.exe metacharacters
        try
        {
            var user = WindowsIdentity.GetCurrent().Name;
            var steps = folders.Select(f =>
            {
                var mk = Directory.Exists(f.Folder) ? "" : $"mkdir \"{f.Folder}\" & ";
                return $"{mk}icacls \"{f.Folder}\" /grant \"{user}:(OI)(CI){(f.Write ? "M" : "RX")}\" /T /C";
            });
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, Verb = "runas", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/c \"{string.Join(" & ", steps)}\"" };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit();
            return true; // each folder is re-probed by the caller
        }
        catch (System.ComponentModel.Win32Exception) { return false; } // UAC prompt declined
    }
}
