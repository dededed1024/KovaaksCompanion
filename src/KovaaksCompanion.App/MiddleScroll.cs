using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Browser-style middle-click autoscroll for every ScrollViewer. Click (short, still) keeps it active until the next click or Escape; hold ends on release.</summary>
public static class MiddleScroll
{
    const double DeadZone = 10, Gain = 6, MaxSpeed = 4000, ClickMs = 300, ClickMove = 10;

    /// <summary>Raised when autoscroll starts, so custom wheel easing can stop and not fight it.</summary>
    public static event Action<ScrollViewer>? Started;

    static ScrollViewer? _sv;
    static Point _anchor;
    static bool _v, _h, _sticky;
    static readonly Stopwatch _frame = new(), _held = new();
    static TimeSpan _last;
    static Adorner? _adorner;
    static AdornerLayer? _layer;
    static object? _prevCursor;
    static Window? _window;

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseDownEvent, new MouseButtonEventHandler(OnMouseDown), true);

    static void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_sv != null || e.ChangedButton != MouseButton.Middle || e.Handled) return;
        var sv = (ScrollViewer)sender;
        if (!(sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0)) return;
        e.Handled = true;
        Start(sv, e.GetPosition(sv));
    }

    static void Start(ScrollViewer sv, Point anchor)
    {
        _sv = sv; _anchor = anchor;
        _v = sv.ScrollableHeight > 0; _h = sv.ScrollableWidth > 0;
        _sticky = false;
        Started?.Invoke(sv);
        if (!sv.CaptureMouse()) { _sv = null; return; }
        _prevCursor = sv.Cursor;
        sv.Cursor = _v && _h ? Cursors.ScrollAll : _v ? Cursors.ScrollNS : Cursors.ScrollWE;
        _layer = AdornerLayer.GetAdornerLayer(sv);
        if (_layer != null) { _adorner = new AnchorAdorner(sv, anchor, _v, _h); _layer.Add(_adorner); }
        sv.PreviewMouseDown += OnPreviewDown;
        sv.PreviewMouseUp += OnPreviewUp;
        sv.LostMouseCapture += OnLostCapture;
        sv.Unloaded += OnUnloaded;
        _window = Window.GetWindow(sv);
        if (_window != null) _window.PreviewKeyDown += OnKey;
        _held.Restart(); _frame.Restart(); _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (!_sticky) return; // hold mode: other buttons are ignored while the middle button is down
        e.Handled = true;
        End();
    }

    static void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _sv == null) return;
        e.Handled = true;
        var moved = (e.GetPosition(_sv) - _anchor).Length;
        if (_held.ElapsedMilliseconds <= ClickMs && moved < ClickMove) _sticky = true;
        else End();
    }

    static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        End();
    }

    static void OnLostCapture(object sender, MouseEventArgs e) => End();
    static void OnUnloaded(object sender, RoutedEventArgs e) => End();

    static void OnFrame(object? sender, EventArgs e)
    {
        if (_sv == null) return;
        var now = _frame.Elapsed;
        var dt = Math.Min((now - _last).TotalSeconds, 0.05);
        _last = now;
        var p = Mouse.GetPosition(_sv);
        if (_v) _sv.ScrollToVerticalOffset(_sv.VerticalOffset + Velocity(p.Y - _anchor.Y) * dt);
        if (_h) _sv.ScrollToHorizontalOffset(_sv.HorizontalOffset + Velocity(p.X - _anchor.X) * dt);
    }

    static double Velocity(double offset)
    {
        var d = Math.Abs(offset) - DeadZone;
        if (d <= 0) return 0;
        var s = Math.Min(d * Gain * (1 + d / 300), MaxSpeed);
        return offset < 0 ? -s : s;
    }

    static void End()
    {
        var sv = _sv;
        if (sv == null) return;
        _sv = null;
        CompositionTarget.Rendering -= OnFrame;
        sv.PreviewMouseDown -= OnPreviewDown;
        sv.PreviewMouseUp -= OnPreviewUp;
        sv.LostMouseCapture -= OnLostCapture;
        sv.Unloaded -= OnUnloaded;
        if (_window != null) { _window.PreviewKeyDown -= OnKey; _window = null; }
        if (_adorner != null) { _layer?.Remove(_adorner); _adorner = null; _layer = null; }
        sv.Cursor = _prevCursor as Cursor;
        if (sv.IsMouseCaptured) sv.ReleaseMouseCapture();
    }

    sealed class AnchorAdorner : Adorner
    {
        static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x23, 0x23, 0x26)));
        static readonly Pen Border = new(Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF))), 1);
        static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
        readonly Point _c;
        readonly bool _v, _h;

        static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }

        public AnchorAdorner(UIElement el, Point c, bool v, bool h) : base(el)
        {
            _c = c; _v = v; _h = h; IsHitTestVisible = false;
            Border.Freeze();
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawEllipse(Fill, Border, _c, 12, 12);
            dc.DrawEllipse(Ink, null, _c, 2, 2);
            if (_v) { Chevron(dc, 0, -1); Chevron(dc, 0, 1); }
            if (_h) { Chevron(dc, -1, 0); Chevron(dc, 1, 0); }
        }

        void Chevron(DrawingContext dc, int dx, int dy)
        {
            // Small arrow head 7px from the centre, pointing along (dx, dy).
            var tip = new Point(_c.X + dx * 9, _c.Y + dy * 9);
            var a = new Point(tip.X - dx * 3 + dy * 3, tip.Y - dy * 3 + dx * 3);
            var b = new Point(tip.X - dx * 3 - dy * 3, tip.Y - dy * 3 - dx * 3);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(tip, true, true);
                ctx.LineTo(b, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(Ink, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
        }
    }
}
