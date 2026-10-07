using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace KovaaksCompanion.App;

/// <summary>Overlay scrollbars: visible only while scrolling, hovered or dragged.</summary>
public static class ScrollFade
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(ScrollFade), new PropertyMetadata(false, OnEnabled));
    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool v) => d.SetValue(IsEnabledProperty, v);

    static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(State), typeof(ScrollFade));

    static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.Loaded -= OnLoaded; sv.Unloaded -= OnUnloaded;
        if (!(bool)e.NewValue) { Detach(sv); return; }
        sv.Loaded += OnLoaded; sv.Unloaded += OnUnloaded;
        if (sv.IsLoaded) Attach(sv);
    }

    static void OnLoaded(object s, RoutedEventArgs e) => Attach((ScrollViewer)s);
    static void OnUnloaded(object s, RoutedEventArgs e) => Detach((ScrollViewer)s);

    static void Attach(ScrollViewer sv)
    {
        Detach(sv);
        sv.ApplyTemplate();
        var v = sv.Template.FindName("PART_VerticalScrollBar", sv) as ScrollBar;
        var h = sv.Template.FindName("PART_HorizontalScrollBar", sv) as ScrollBar;
        var st = new State(sv, v is null ? null : new Bar(v), h is null ? null : new Bar(h));
        sv.SetValue(StateProperty, st);
    }

    static void Detach(ScrollViewer sv)
    {
        if (sv.GetValue(StateProperty) is not State st) return;
        st.Dispose();
        sv.ClearValue(StateProperty);
    }

    sealed class State : IDisposable
    {
        readonly ScrollViewer _sv; readonly Bar? _v, _h;
        public State(ScrollViewer sv, Bar? v, Bar? h) { _sv = sv; _v = v; _h = h; sv.ScrollChanged += OnScroll; }
        public void Dispose() { _sv.ScrollChanged -= OnScroll; _v?.Dispose(); _h?.Dispose(); }

        // Only real offset moves count; extent/viewport changes (layout, resize, content load) are ignored.
        void OnScroll(object s, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0) _v?.Show();
            if (e.HorizontalChange != 0 && e.ExtentWidthChange == 0 && e.ViewportWidthChange == 0) _h?.Show();
        }
    }

    sealed class Bar : IDisposable
    {
        readonly ScrollBar _bar; readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(900) };
        bool Held => _bar.IsMouseOver || _bar.IsMouseCaptureWithin;

        public Bar(ScrollBar bar)
        {
            _bar = bar;
            _timer.Tick += OnTick;
            bar.MouseEnter += OnHold; bar.MouseLeave += OnRelease;
            bar.IsMouseCaptureWithinChanged += OnCapture;
        }

        public void Dispose()
        {
            _timer.Stop(); _timer.Tick -= OnTick;
            _bar.MouseEnter -= OnHold; _bar.MouseLeave -= OnRelease;
            _bar.IsMouseCaptureWithinChanged -= OnCapture;
            _bar.BeginAnimation(UIElement.OpacityProperty, null);
            _bar.Opacity = 0; _bar.IsHitTestVisible = false;
        }

        void OnHold(object s, EventArgs e) => Show();
        void OnRelease(object s, EventArgs e) { if (!Held) Restart(); }
        void OnCapture(object s, DependencyPropertyChangedEventArgs e) { if (!Held) Restart(); }

        void OnTick(object? s, EventArgs e)
        {
            _timer.Stop();
            if (Held) return;
            var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            a.Completed += (_, _) => { if (_bar.Opacity == 0) _bar.IsHitTestVisible = false; };
            _bar.BeginAnimation(UIElement.OpacityProperty, a);
        }

        void Restart() { _timer.Stop(); _timer.Start(); }

        public void Show()
        {
            _bar.IsHitTestVisible = true;
            if (_bar.Opacity < 1) _bar.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            _timer.Stop();
            if (!Held) _timer.Start();
        }
    }
}
