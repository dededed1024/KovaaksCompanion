using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Input;

/// <summary>
/// Thin Win32 shell: hidden message-only window on a dedicated thread, Raw Input with RIDEV_INPUTSINK
/// (works while the game is focused). Read-only. Every event is stamped with QPC and passed to the callback
/// on the capture thread; keep the callback cheap.
/// </summary>
public sealed class RawInputCapture : IDisposable
{
    const uint WM_INPUT = 0x00FF, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x100;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    readonly Action<RawMouseEvent> _onEvent;
    readonly WndProc _proc; // kept alive for the window's lifetime
    Thread? _thread;
    IntPtr _hwnd;

    public RawInputCapture(Action<RawMouseEvent> onEvent)
    {
        _onEvent = onEvent;
        _proc = Proc;
    }

    /// <summary>Starts the capture thread; returns once the window exists. Throws if registration fails.</summary>
    public void Start()
    {
        if (_thread != null) throw new InvalidOperationException("Already started.");
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready, e => error = e)) { IsBackground = true, Name = "RawInput" };
        _thread.Start();
        ready.Wait();
        if (error != null) { _thread = null; throw error; }
    }

    public void Dispose()
    {
        if (_thread == null) return;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
    }

    void Run(ManualResetEventSlim ready, Action<Exception> fail)
    {
        try
        {
            const string cls = "KovaaksCompanion.RawInput";
            var wc = new WNDCLASS { lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), lpszClassName = cls, hInstance = GetModuleHandle(null) };
            RegisterClass(ref wc);
            _hwnd = CreateWindowEx(0, cls, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            var rid = new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd };
            if (!RegisterRawInputDevices([rid], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                throw new System.ComponentModel.Win32Exception();
        }
        catch (Exception e) { fail(e); ready.Set(); return; }
        ready.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
    }

    IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT) { Read(lParam, Stopwatch.GetTimestamp()); return IntPtr.Zero; }
        if (msg == WM_DESTROY) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void Read(IntPtr hRawInput, long qpc)
    {
        uint size = 0;
        var header = (uint)(8 + IntPtr.Size * 2);
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, header);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, header) != size) return;
            if (Marshal.ReadInt32(buf, 0) != 0) return; // RIM_TYPEMOUSE
            var h = (int)header;
            var flags = (ushort)Marshal.ReadInt16(buf, h);
            var buttons = (ushort)Marshal.ReadInt16(buf, h + 4);
            int dx = Marshal.ReadInt32(buf, h + 12), dy = Marshal.ReadInt32(buf, h + 16);
            if ((flags & 1) == 0 && (dx != 0 || dy != 0)) _onEvent(new RawMouseEvent(qpc, RawMouseKind.Move, dx, dy));
            if ((buttons & 1) != 0) _onEvent(new RawMouseEvent(qpc, RawMouseKind.ButtonDown, Button: MouseButton.Left));
            if ((buttons & 2) != 0) _onEvent(new RawMouseEvent(qpc, RawMouseKind.ButtonUp, Button: MouseButton.Left));
            if ((buttons & 4) != 0) _onEvent(new RawMouseEvent(qpc, RawMouseKind.ButtonDown, Button: MouseButton.Right));
            if ((buttons & 8) != 0) _onEvent(new RawMouseEvent(qpc, RawMouseKind.ButtonUp, Button: MouseButton.Right));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)] struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr hRaw, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
