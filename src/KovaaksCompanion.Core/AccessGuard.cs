using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core;

/// <summary>
/// The one way to recover from a permission error: run the file operation, and on <see cref="UnauthorizedAccessException"/> ask the
/// host for an elevated grant on that folder (one UAC prompt) and retry once. A folder is asked about once per process; when the grant
/// is declined or fails, the original exception is rethrown for the caller to report.
/// </summary>
public static class AccessGuard
{
    /// <summary>Set by the host: for every folder creates it if missing and grants the current user read (or modify when write) on it only, never on its parents, in one elevated call (one UAC prompt). Returns false when declined or failed.</summary>
    public static Func<IReadOnlyList<(string Folder, bool Write)>, bool> GrantMany { get; set; } = _ => false;

    public static bool Grant(string folder, bool write) => GrantMany([(folder, write)]);

    /// <summary>
    /// Probes each folder up front and fixes every blocked one with a single UAC prompt, so later file operations
    /// do not prompt one by one. Returns the folders that are still blocked afterwards (empty = all fine).
    /// </summary>
    public static List<string> Preflight(IEnumerable<(string Folder, bool Write)> folders)
    {
        var blocked = folders.Where(f => f.Folder.Length > 0 && !Accessible(f.Folder, f.Write)).ToList();
        if (blocked.Count == 0) return [];
        AppLog.Write("access", "preflight blocked: " + string.Join("; ", blocked.Select(b => b.Folder)));
        var ok = GrantMany(blocked);
        AppLog.Write("access", ok ? "preflight granted" : "preflight grant failed or declined");
        lock (Gate) foreach (var b in blocked) Asked.Add((b.Write ? "w:" : "r:") + b.Folder);
        return blocked.Where(b => !Accessible(b.Folder, b.Write)).Select(b => b.Folder).ToList();
    }

    static bool Accessible(string folder, bool write)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                if (!write) return true; // nothing to read yet
                Directory.CreateDirectory(folder);
            }
            if (write)
            {
                var probe = Path.Combine(folder, ".access-probe-" + Environment.ProcessId);
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
            }
            else using (var e = Directory.EnumerateFileSystemEntries(folder).GetEnumerator()) e.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return true; } // not a permission problem
    }

    static readonly object Gate = new();
    static readonly HashSet<string> Asked = new(StringComparer.OrdinalIgnoreCase);

    public static void Run(string folder, bool write, Action op)
    {
        try { op(); }
        catch (UnauthorizedAccessException e) when (TryGrant(folder, write, e)) { op(); }
    }

    static bool TryGrant(string folder, bool write, UnauthorizedAccessException e)
    {
        var key = (write ? "w:" : "r:") + folder;
        lock (Gate)
        {
            if (Asked.Contains(key)) return false; // already asked: a second denial is a real failure
            AppLog.Write("access", $"denied: {folder}: {e.Message}; requesting {(write ? "modify" : "read")} grant");
            var ok = GrantMany([(folder, write)]);
            Asked.Add(key);
            AppLog.Write("access", ok ? $"granted: {folder}" : $"grant failed or declined: {folder}");
            return ok;
        }
    }
}
