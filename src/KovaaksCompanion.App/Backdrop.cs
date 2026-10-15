using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Win11 Mica backdrop + dark title bar. Falls back to the opaque Bg brush where unsupported.</summary>
public static class Backdrop
{
    const int DwmUseImmersiveDarkMode = 20, DwmSystemBackdropType = 38, NoBackdrop = 1, MicaBackdrop = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

    /// <summary>Matches the title bar and Mica tone to the current theme (dark or light); for a window that is already shown.</summary>
    public static void SetDark(Window w)
    {
        int on = Theme.IsLight ? 0 : 1;
        DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, DwmUseImmersiveDarkMode, ref on, sizeof(int));
    }

    public static void Apply(Window w) => w.SourceInitialized += (_, _) => Refresh(w);

    /// <summary>Sets the title bar and the backdrop for the current theme: Mica under a theme tint, or none (opaque Bg) for Black. Works on a window that is already shown.</summary>
    public static void Refresh(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        SetDark(w);
        if (Environment.OSVersion.Version.Build < 22621) return; // keep opaque Bg
        var black = Theme.Current == "Black";
        int type = black ? NoBackdrop : MicaBackdrop;
        if (DwmSetWindowAttribute(hwnd, DwmSystemBackdropType, ref type, sizeof(int)) != 0) return;
        var m = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        if (DwmExtendFrameIntoClientArea(hwnd, ref m) != 0) return;
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } ct) ct.BackgroundColor = Colors.Transparent;
        w.SetResourceReference(Window.BackgroundProperty, black ? "Bg" : "BgWash"); // theme tint over the Mica
    }
}
