using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>A zoom bar for a chart's window of runs. The window always ends at the newest run (the right end), so only the left handle moves: drag it or click the track to choose how many runs show, double click for the automatic window.</summary>
public sealed class RangeBar : FrameworkElement
{
    const double Pad = 8, HandleR = 6, Grab = 10;
    int _total, _off, _len;

    /// <summary>Raised while the user drags, with the first run and the number of runs.</summary>
    public event Action<int, int>? Changed;

    /// <summary>Raised on a double click.</summary>
    public event Action? Reset;

    public int MinLen { get; set; } = 5;

    public RangeBar()
    {
        Height = 16;
        Cursor = Cursors.SizeWE;
        Focusable = false;
    }

    public void Set(int total, int off, int len)
    {
        _total = total; _off = off; _len = len;
        InvalidateVisual();
    }

    static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    double TrackW => Math.Max(1, ActualWidth - 2 * Pad);

    (double X0, double X1) Thumb() =>
        _total <= 0 ? (Pad, Pad) : (Pad + _off / (double)_total * TrackW, Pad + TrackW);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_total <= 0) return;
        var mid = h / 2;
        // A thin track, the shown span on it in the accent colour, and a round handle at its left end.
        dc.DrawRoundedRectangle(Res("Wash"), null, new Rect(Pad, mid - 1.5, TrackW, 3), 1.5, 1.5);
        var (x0, x1) = Thumb();
        dc.DrawRoundedRectangle(Res("Accent"), null, new Rect(x0, mid - 1.5, Math.Max(1, x1 - x0), 3), 1.5, 1.5);
        var ring = new Pen(Res("Accent"), 2);
        dc.DrawEllipse(Res("Fg"), ring, new Point(x0, mid), HandleR - 1, HandleR - 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_total <= 0) return;
        if (e.ClickCount == 2) { Reset?.Invoke(); return; }
        var x = e.GetPosition(this).X;
        // The handle jumps to the click, then follows the drag.
        Apply(OffAt(x));
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;
        Apply(OffAt(e.GetPosition(this).X));
    }

    int OffAt(double x) => (int)Math.Round((x - Pad) / TrackW * _total);

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
    }

    /// <summary>Moves the left handle to run <paramref name="off"/>; the window keeps ending at the newest run.</summary>
    void Apply(int off)
    {
        var min = Math.Min(MinLen, _total);
        off = Math.Clamp(off, 0, _total - min);
        var len = _total - off;
        if (off == _off && len == _len) return;
        _off = off; _len = len;
        InvalidateVisual();
        Changed?.Invoke(off, len);
    }
}
