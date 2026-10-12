using System.Windows;
using System.Windows.Threading;

namespace KovaaksCompanion.App;

/// <summary>
/// The stack of open popups, oldest first. Only the top one is interactive: every layer below it, the page behind them
/// and the title bar's search and settings buttons are made inert (no clicks, hover or keyboard focus) while it is open.
/// Esc and an outside click close the top one only.
/// </summary>
static class Popups
{
    sealed record Entry(object Owner, UIElement Layer, Func<bool> Close, Action Focus);

    static readonly List<Entry> Stack = [];
    static readonly List<UIElement> Base = [];

    public static bool Any => Stack.Count > 0;

    /// <summary>A new main window starts with no popups and its own page elements.</summary>
    public static void Reset()
    {
        Stack.Clear();
        Base.Clear();
    }

    /// <summary>Elements that are inert while any popup is open (the page behind the popups, the title bar buttons).</summary>
    public static void AddBase(params UIElement[] elements)
    {
        foreach (var e in elements) if (!Base.Contains(e)) Base.Add(e);
        Sync();
    }

    /// <summary>A popup was opened: it becomes the top one. <paramref name="close"/> returns false when it had nothing to close.</summary>
    public static void Opened(object owner, UIElement layer, Func<bool> close, Action focus)
    {
        Stack.RemoveAll(x => Equals(x.Owner, owner));
        Stack.Add(new Entry(owner, layer, close, focus));
        Sync();
    }

    /// <summary>A popup was closed (its fade-out may still run); the one below becomes the top one.</summary>
    public static void Closed(object owner)
    {
        var i = Stack.FindIndex(x => Equals(x.Owner, owner));
        if (i < 0) return;
        var wasTop = i == Stack.Count - 1;
        var gone = Stack[i];
        Stack.RemoveAt(i);
        gone.Layer.IsEnabled = true;
        Sync();
        if (wasTop && Stack.Count > 0)
        {
            var top = Stack[^1];
            gone.Layer.Dispatcher.BeginInvoke(DispatcherPriority.Input, top.Focus);
        }
    }

    /// <summary>Closes the top popup; false when none was open.</summary>
    public static bool CloseTop() => Stack.Count > 0 && Stack[^1].Close();

    static void Sync()
    {
        var any = Stack.Count > 0;
        foreach (var b in Base) { b.IsEnabled = !any; b.IsHitTestVisible = !any; }
        for (var i = 0; i < Stack.Count; i++) Stack[i].Layer.IsEnabled = i == Stack.Count - 1;
    }
}
