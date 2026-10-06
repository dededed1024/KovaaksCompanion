using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video;

/// <summary>Game window client area in desktop pixels, the DXGI output showing it, and that output's refresh rate.</summary>
public sealed record CaptureTarget(DxgiOutput Output, int ProcessId, int X, int Y, int Width, int Height, int RefreshHz);

/// <summary>Locates the game's window and turns it into a ddagrab crop region.</summary>
public static unsafe class GameWindow
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string deviceName, int mode, byte* devMode);

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
            (uint Pid, Rect Client, long Area)? best = null;
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
                    best = (pid, new Rect { Left = origin.X, Top = origin.Y, Right = origin.X + w, Bottom = origin.Y + ht }, area);
                return true;
            }, IntPtr.Zero);
            if (best is null) return null;
            var r = best.Value.Client;
            return Resolve(r.Left, r.Top, r.Right, r.Bottom, (int)best.Value.Pid, outputs ?? MonitorResolver.Enumerate());
        }
        finally { SetThreadDpiAwarenessContext(prevCtx); }
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
        return new CaptureTarget(best, pid, x - best.Left, y - best.Top, width, height, RefreshRate(best));
    }

    /// <summary>Current refresh rate of the output's display mode; 60 when unknown.</summary>
    public static int RefreshRate(DxgiOutput output)
    {
        if (!OperatingSystem.IsWindows()) return 60;
        var dm = stackalloc byte[220];       // DEVMODEW: dmSize @68, dmDisplayFrequency @184
        new Span<byte>(dm, 220).Clear();
        *(ushort*)(dm + 68) = 220;
        if (!EnumDisplaySettingsW(output.DeviceName, -1, dm)) return 60;
        int hz = (int)*(uint*)(dm + 184);
        return hz > 1 ? Math.Min(hz, 240) : 60;
    }
}
