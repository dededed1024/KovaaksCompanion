using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Smooth wheel scrolling for every ScrollViewer (set by the implicit style): the offset eases towards a target, about 96px per notch.
/// Of nested scrollers the innermost one that can still move gets the wheel; otherwise it chains to the outer one.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, OnEnabled));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    static ScrollViewer? _ws;
    static double _target, _lastSet;
    static bool _easing;
    static readonly System.Diagnostics.Stopwatch Clock = new();
    static TimeSpan _lastTick;

    static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if (e.NewValue is true) { sv.PreviewMouseWheel += OnWheel; sv.ScrollChanged += OnScrolled; }
        else { sv.PreviewMouseWheel -= OnWheel; sv.ScrollChanged -= OnScrolled; }
    }

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        var outer = (ScrollViewer)sender;
        var sv = outer;
        var node = e.OriginalSource as DependencyObject;
        var inner = false;
        while (node != null && node != outer)
        {
            if (node is ScrollViewer s && s.ScrollableHeight > 0
                && (e.Delta > 0 ? s.VerticalOffset > 0 : e.Delta < 0 && s.VerticalOffset < s.ScrollableHeight))
            {
                sv = s; inner = true;
                break;
            }
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        if (!inner && outer.ScrollableHeight <= 0) return; // nothing here can move: leave the wheel to the scroller around it
        e.Handled = true;
        if (_easing && sv != _ws) Stop();
        _ws = sv;
        if (!_easing) _target = sv.VerticalOffset;
        _target = Math.Clamp(_target - e.Delta * 0.8, 0, sv.ScrollableHeight);
        Start(sv);
    }

    /// <summary>Eases <paramref name="sv"/> to <paramref name="offset"/>.</summary>
    public static void To(ScrollViewer sv, double offset)
    {
        if (_easing && sv != _ws) Stop();
        _target = Math.Clamp(offset, 0, sv.ScrollableHeight);
        Start(sv);
    }

    static void Start(ScrollViewer sv)
    {
        _ws = sv;
        if (_easing) return;
        _easing = true;
        _lastSet = sv.VerticalOffset;
        _lastTick = TimeSpan.Zero;
        Clock.Restart();
        CompositionTarget.Rendering += OnEase;
    }

    static void OnEase(object? sender, EventArgs e)
    {
        var now = Clock.Elapsed;
        var dt = Math.Min((now - _lastTick).TotalSeconds, 0.05);
        _lastTick = now;
        var cur = _ws!.VerticalOffset;
        var diff = _target - cur;
        if (Math.Abs(diff) < 0.5) { _lastSet = _target; _ws.ScrollToVerticalOffset(_target); Stop(); return; }
        _lastSet = cur + diff * (1 - Math.Exp(-dt / 0.05));
        _ws.ScrollToVerticalOffset(_lastSet);
    }

    public static void Stop()
    {
        if (!_easing) return;
        _easing = false;
        CompositionTarget.Rendering -= OnEase;
    }

    /// <summary>A scroll we did not cause (scrollbar drag, bring-into-view) cancels the easing.</summary>
    static void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        if (_easing && sender == _ws && e.VerticalChange != 0 && Math.Abs(_ws!.VerticalOffset - _lastSet) > 1.5) Stop();
    }
}
