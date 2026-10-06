using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Win11 Mica backdrop + dark title bar. Falls back to the opaque Bg brush where unsupported.</summary>
public static class Backdrop
{
    const int DwmUseImmersiveDarkMode = 20, DwmSystemBackdropType = 38, MicaBackdrop = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    public static void Apply(Window w) => w.SourceInitialized += (_, _) =>
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        int on = 1, mica = MicaBackdrop;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        if (Environment.OSVersion.Version.Build < 22621) return; // keep opaque Bg
        if (DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref mica, sizeof(int)) != 0) return;
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref m) != 0) return;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;
        w.Background = Brushes.Transparent;
    };
}
