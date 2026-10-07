using System.Windows;
using System.Windows.Input;

namespace KovaaksCompanion.App;

/// <summary>Click on release: the press must start on the element and the pointer move under 6px. The click gets the pointer in window coordinates.</summary>
static class RowClick
{
    public static void Attach(UIElement el, Action<Point?> click, Action<bool>? pressed = null)
    {
        Point down = default;
        var armed = false;
        el.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            armed = true;
            down = e.GetPosition(el);
            el.CaptureMouse();
            pressed?.Invoke(true);
        };
        el.MouseLeftButtonUp += (_, e) =>
        {
            if (!armed) return;
            armed = false;
            el.ReleaseMouseCapture();
            pressed?.Invoke(false);
            var p = e.GetPosition(el);
            if ((p - down).Length < 6 && el.InputHitTest(p) != null) click(Window.GetWindow(el) is { } w ? e.GetPosition(w) : null);
        };
        el.LostMouseCapture += (_, _) =>
        {
            if (!armed) return;
            armed = false;
            pressed?.Invoke(false);
        };
    }
}
