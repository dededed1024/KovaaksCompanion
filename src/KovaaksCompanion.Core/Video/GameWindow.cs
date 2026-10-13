using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video;

/// <summary>Game window: <see cref="Hwnd"/> for the libobs window capture, client area size in physical pixels (even, as encoders need).</summary>
public sealed record CaptureTarget(int ProcessId, int Width, int Height, long Hwnd);

/// <summary>Locates the game's window.</summary>
public static class GameWindow
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point p);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    private const int MinSide = 200;

    /// <summary>Largest visible top-level window of the process; null when the process or its window is not up yet.</summary>
    public static CaptureTarget? Find(string processName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName)) { pids.Add((uint)p.Id); p.Dispose(); }
        if (pids.Count == 0) return null;

        // physical pixels regardless of this process's DPI mode
        var prevCtx = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            (uint Pid, IntPtr Hwnd, Rect Client, long Area)? best = null;
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (!pids.Contains(pid) || !IsWindowVisible(h) || IsIconic(h) || !GetClientRect(h, out var c)) return true;
                var origin = new Point();
                if (!ClientToScreen(h, ref origin)) return true;
                int w = c.Right - c.Left, ht = c.Bottom - c.Top;
                if (w < MinSide || ht < MinSide) return true;
                long area = (long)w * ht;
                if (best is null || area > best.Value.Area)
                    best = (pid, h, new Rect { Left = origin.X, Top = origin.Y, Right = origin.X + w, Bottom = origin.Y + ht }, area);
                return true;
            }, IntPtr.Zero);
            if (best is null) return null;
            var r = best.Value.Client;
            return new CaptureTarget((int)best.Value.Pid, (r.Right - r.Left) & ~1, (r.Bottom - r.Top) & ~1, best.Value.Hwnd.ToInt64());
        }
        finally { SetThreadDpiAwarenessContext(prevCtx); }
    }

    /// <summary>
    /// Polls <paramref name="find"/> until it yields a target. Gives up only when <paramref name="processAlive"/> turns false or
    /// <paramref name="ct"/> fires: a fullscreen game alt-tabbed away has no usable window for as long as it likes.
    /// </summary>
    public static async Task<CaptureTarget?> WaitAsync(Func<CaptureTarget?> find, Func<bool> processAlive, TimeSpan poll, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var t = find();
            if (t != null) return t;
            if (!processAlive()) return null;
            try { await Task.Delay(poll, ct); } catch (OperationCanceledException) { break; }
        }
        return null;
    }

    public static bool IsProcessAlive(string processName)
    {
        var ps = Process.GetProcessesByName(processName);
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }
}
