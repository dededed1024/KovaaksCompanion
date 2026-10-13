using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KovaaksCompanion.Core.Diagnostics;
using KovaaksCompanion.Core.Video.Ffmpeg;

namespace KovaaksCompanion.App;

/// <summary>Video surface backed by the FFmpeg libraries of the libobs bundle (no Windows Media Player needed). Seeks are frame accurate and speed changes keep the position.
/// Decodes on the GPU (D3D11VA) when possible and plays the audio track through the default output device at any speed.</summary>
public sealed class VideoSurface : FrameworkElement
{
    const int OpenTimeoutMs = 8000;

    FfmpegPlayer? _p;
    WriteableBitmap? _bmp;
    int _ver, _bmpW, _bmpH;
    bool _opened, _ended, _playing, _hooked, _failed, _dirty;
    double _speed = 1, _lastPos = -1;
    long _openedAt, _forceUntil;

    public event Action? MediaOpened;
    public event Action? MediaEnded;
    public event Action<Exception>? MediaFailed;

    public double Duration { get; private set; }
    public int NaturalVideoWidth { get; private set; }
    public int NaturalVideoHeight { get; private set; }

    public double SpeedRatio
    {
        get => _speed;
        set
        {
            if (Math.Abs(value - _speed) < 1e-6) return;
            _speed = value;
            _p?.SetSpeed(value);
        }
    }

    bool _muted;
    public bool Muted
    {
        get => _muted;
        set { _muted = value; if (_p is not null) _p.Muted = value; }
    }

    public double Position
    {
        get
        {
            if (_p is null) return 0;
            return _ended ? Duration : _p.Position;
        }
        set
        {
            if (_p is null || !_opened) return;
            _ended = false;
            _dirty = true;
            _forceUntil = Environment.TickCount64 + 1500;
            _p.Seek(value);
        }
    }

    /// <summary>Starts loading <paramref name="path"/> (replaces any current video); <see cref="MediaOpened"/> or <see cref="MediaFailed"/> follows.</summary>
    public void Open(string path)
    {
        Close();
        int ver = ++_ver;
        _openedAt = Environment.TickCount64;
        Hook(true);
        Task.Run(() =>
        {
            FfmpegPlayer? p = null;
            Exception? error = null;
            try
            {
                var root = AppHost.ResolveLibobs();
                if (root is null || !FfmpegNative.Load(System.IO.Path.Combine(root, "bin", "64bit"))) error = new InvalidOperationException("ffmpeg: " + (root is null ? "libobs bundle missing" : FfmpegNative.LastError));
                else p = FfmpegPlayer.Open(path) ?? throw new InvalidOperationException("Cannot play the file.");
            }
            catch (Exception e) { error = e; }
            Dispatcher.BeginInvoke(() =>
            {
                if (ver != _ver) { p?.Dispose(); return; }
                if (p is null) { Fail(error ?? new InvalidOperationException("Cannot play the file.")); return; }
                _p = p;
                p.Muted = _muted;
            });
        });
    }

    public void Close()
    {
        _ver++;
        Hook(false);
        var p = _p;
        _p = null;
        if (p is not null) Task.Run(p.Dispose);
        _opened = _ended = _playing = _failed = _dirty = false;
        _lastPos = -1;
        Duration = 0; NaturalVideoWidth = NaturalVideoHeight = 0;
        _bmp = null; _bmpW = _bmpH = 0;
        InvalidateVisual();
    }

    public void Play()
    {
        _playing = true;
        if (_p is null || !_opened) return;
        _ended = false;
        _p.Play();
    }

    public void Pause()
    {
        _playing = false;
        if (_p is null || !_opened) return;
        _p.Pause();
    }

    void Fail(Exception e)
    {
        if (_failed) return;
        _failed = true;
        AppLog.Write("replay", "ffmpeg player failed: " + e);
        Hook(false);
        MediaFailed?.Invoke(e);
    }

    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on) CompositionTarget.Rendering += OnRendering; else CompositionTarget.Rendering -= OnRendering;
    }

    void OnRendering(object? sender, EventArgs e)
    {
        var p = _p;
        if (p is null)
        {
            if (!_failed && Environment.TickCount64 - _openedAt > OpenTimeoutMs) Fail(new InvalidOperationException("Cannot play the file."));
            return;
        }
        long now = Environment.TickCount64;
        if (!_opened)
        {
            double dur = p.Duration;
            int w = p.VideoWidth, h = p.VideoHeight;
            if (dur <= 0 || w <= 0 || h <= 0)
            {
                if (now - _openedAt > OpenTimeoutMs) Fail(new InvalidOperationException("Cannot play the file."));
                return;
            }
            Duration = dur; NaturalVideoWidth = w; NaturalVideoHeight = h;
            _opened = true;
            MediaOpened?.Invoke();
            if (_p is null) return;
        }

        if (!_ended && _playing && p.State == FfmpegPlayer.StateEnded)
        {
            _ended = true; _playing = false;
            MediaEnded?.Invoke();
            return;
        }

        double pos = p.Position;
        if (!_dirty && pos == _lastPos && now >= _forceUntil) return;
        _lastPos = pos; _dirty = false;
        Draw(p);
    }

    void Draw(FfmpegPlayer p)
    {
        if (ActualWidth < 1 || ActualHeight < 1 || NaturalVideoWidth <= 0 || NaturalVideoHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double aspect = (double)NaturalVideoWidth / NaturalVideoHeight;
        double pw = ActualWidth * dpi.DpiScaleX, ph = ActualHeight * dpi.DpiScaleY;
        double w = Math.Min(pw, ph * aspect), h = w / aspect;
        if (w > NaturalVideoWidth) { w = NaturalVideoWidth; h = NaturalVideoHeight; }
        int iw = Math.Max(2, (int)Math.Round(w) & ~1), ih = Math.Max(2, (int)Math.Round(h) & ~1);
        if (_bmp is null || _bmpW != iw || _bmpH != ih)
        {
            _bmp = new WriteableBitmap(iw, ih, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Bgr32, null);
            (_bmpW, _bmpH) = (iw, ih);
            InvalidateVisual();
        }
        var bmp = _bmp;
        bmp.Lock();
        try
        {
            bool ok = p.ReadFrame(iw, ih, (src, stride) => CopyRows(src, stride, bmp.BackBuffer, bmp.BackBufferStride, ih, iw * 4));
            if (ok) bmp.AddDirtyRect(new Int32Rect(0, 0, iw, ih));
        }
        finally { bmp.Unlock(); }
    }

    static unsafe void CopyRows(nint src, int srcStride, nint dst, int dstStride, int rows, int bytes)
    {
        for (int y = 0; y < rows; y++) Buffer.MemoryCopy((byte*)src + (long)y * srcStride, (byte*)dst + (long)y * dstStride, dstStride, bytes);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bmp is null) return;
        dc.DrawImage(_bmp, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        _dirty = true;
    }
}
