using System.Windows;
using System.Windows.Media;

namespace KovaaksCompanion.App;

/// <summary>Confetti cannons in both bottom corners: pieces fly up and inward, flutter down under drag and fade out. Drawn directly, one frame per render tick, and idle once every piece is gone.</summary>
public sealed class Confetti : FrameworkElement
{
    struct Piece
    {
        public double X, Y, Vx, Vy, Rot, Vr, W, H, Flip, Vf, Age, Life;
        public Brush Fill;
        /// <summary>Stays on the floor once landed instead of fading out.</summary>
        public bool Rests, Landed;
    }

    const double RestShare = 0.12;

    const double Drag = 2.4;
    readonly List<Piece> _pieces = [];
    readonly Random _rng = new();
    DateTime _last;
    bool _running;

    /// <summary>Fires both cannons. <paramref name="colors"/> are mixed with a default party palette.</summary>
    public void Fire(IEnumerable<Color> colors)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var palette = colors.Concat([Color.FromRgb(0xFF, 0x37, 0x5F), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2)])
            .Select(c => { var b = new SolidColorBrush(c); b.Freeze(); return (Brush)b; }).ToArray();
        _pieces.RemoveAll(p => p.Landed);
        Cannon(0, h, 1, palette, h);
        Cannon(w, h, -1, palette, h);
        if (_running) return;
        _running = true;
        _last = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
    }

    void Cannon(double x, double y, int dir, Brush[] palette, double h)
    {
        for (var i = 0; i < 110; i++)
        {
            var rests = _rng.NextDouble() < RestShare;
            var angle = (50 + _rng.NextDouble() * 30) * Math.PI / 180; // above the horizontal, toward the middle
            var speed = h * (1.5 + _rng.NextDouble() * 1.5);
            var size = 9 + _rng.NextDouble() * 9;
            _pieces.Add(new Piece
            {
                X = x, Y = y,
                Vx = dir * Math.Cos(angle) * speed, Vy = -Math.Sin(angle) * speed,
                Rot = _rng.NextDouble() * 360, Vr = (_rng.NextDouble() - 0.5) * 900,
                W = size, H = size * (0.45 + _rng.NextDouble() * 0.4),
                Flip = _rng.NextDouble() * Math.PI * 2, Vf = 6 + _rng.NextDouble() * 8,
                Age = -_rng.NextDouble() * 0.2, Life = 3 + _rng.NextDouble() * 1.8,
                Fill = palette[_rng.Next(palette.Length)],
                Rests = rests,
            });
        }
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var dt = Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        var g = ActualHeight * 1.2;
        var moving = false;
        for (var i = _pieces.Count - 1; i >= 0; i--)
        {
            var p = _pieces[i];
            if (p.Landed) continue;
            p.Age += dt;
            if (p.Rests && p.Age > 0 && p.Vy > 0 && p.Y >= ActualHeight - p.H - 2)
            {
                p.Landed = true; p.Vx = p.Vy = 0; p.Flip = 0;
                p.Y = ActualHeight - p.H / 2 - _rng.NextDouble() * 12;
                _pieces[i] = p;
                continue;
            }
            if ((!p.Rests && p.Age >= p.Life) || p.Y > ActualHeight + 40) { _pieces.RemoveAt(i); continue; }
            moving = true;
            if (p.Age > 0)
            {
                p.Vx -= p.Vx * Drag * dt;
                p.Vy += (g - p.Vy * Drag) * dt;
                p.X += p.Vx * dt; p.Y += p.Vy * dt;
                p.Rot += p.Vr * dt; p.Flip += p.Vf * dt;
            }
            _pieces[i] = p;
        }
        if (!moving) { CompositionTarget.Rendering -= OnFrame; _running = false; }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var p in _pieces)
        {
            if (p.Age <= 0) continue;
            var fade = p.Rests ? 1 : Math.Clamp((p.Life - p.Age) / (p.Life * 0.3), 0, 1);
            dc.PushOpacity(fade);
            dc.PushTransform(new TranslateTransform(p.X, p.Y));
            dc.PushTransform(new RotateTransform(p.Rot));
            dc.PushTransform(new ScaleTransform(1, Math.Max(0.15, Math.Abs(Math.Cos(p.Flip)))));
            dc.DrawRectangle(p.Fill, null, new Rect(-p.W / 2, -p.H / 2, p.W, p.H));
            dc.Pop(); dc.Pop(); dc.Pop(); dc.Pop();
        }
    }
}
