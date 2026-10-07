using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video;

/// <summary>
/// Game window: <see cref="Hwnd"/> for gfxcapture; client area in desktop pixels (X/Y relative to <see cref="Output"/>, the DXGI output
/// showing it, null when off-screen) for the ddagrab crop.
/// </summary>
public sealed record CaptureTarget(DxgiOutput? Output, int ProcessId, int X, int Y, int Width, int Height, long Hwnd = 0);

/// <summary>Locates the game's window and (handle for gfxcapture, client rect for ddagrab).</summary>
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
    public static CaptureTarget? Find(string processName, IReadOnlyList<DxgiOutput>? outputs = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(processName)) { pids.Add((uint)p.Id); p.Dispose(); }
        if (pids.Count == 0) return null;

        // physical pixels regardless of this process's DPI mode (same space as DXGI output rects)
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
            var t = Resolve(r.Left, r.Top, r.Right, r.Bottom, (int)best.Value.Pid, outputs ?? MonitorResolver.Enumerate());
            return (t ?? new CaptureTarget(null, (int)best.Value.Pid, 0, 0, (r.Right - r.Left) & ~1, (r.Bottom - r.Top) & ~1)) with { Hwnd = best.Value.Hwnd.ToInt64() };
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

    /// <summary>
    /// Maps a desktop rectangle to the output holding most of it: clipped to that output, made even-sized
    /// (encoders need it), offset relative to the output origin as ddagrab expects.
    /// </summary>
    public static CaptureTarget? Resolve(int left, int top, int right, int bottom, int pid, IEnumerable<DxgiOutput> outputs)
    {
        DxgiOutput? best = null; long bestArea = 0;
        foreach (var o in outputs.Where(o => o.AttachedToDesktop && !o.IsRotated))
        {
            long w = Math.Min(right, o.Right) - Math.Max(left, o.Left), h = Math.Min(bottom, o.Bottom) - Math.Max(top, o.Top);
            if (w > 0 && h > 0 && w * h > bestArea) { best = o; bestArea = w * h; }
        }
        if (best is null) return null;
        int x = Math.Max(left, best.Left), y = Math.Max(top, best.Top);
        int width = (Math.Min(right, best.Right) - x) & ~1, height = (Math.Min(bottom, best.Bottom) - y) & ~1;
        if (width < 2 || height < 2) return null;
        return new CaptureTarget(best, pid, x - best.Left, y - best.Top, width, height);
    }
}
