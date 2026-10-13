using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KovaaksCompanion.App;

public partial class StatsView
{
    bool _gifBusy;

    /// <summary>Saves the profile card as a looping GIF: one 7 s watermark drift cycle, rendered frame by frame.</summary>
    async Task SaveProfileGifAsync(TextBlock label, string idle)
    {
        if (_gifBusy || ProfileCard.ActualWidth <= 0) return;
        _gifBusy = true;
        const int frames = 234, delayCs = 3; // ~33 fps x 7 s, the length of one drift loop
        var charts = _profileCharts.ToList();
        var file = Path.Combine(_host.Settings.CapturesFolder, $"profile_{DateTime.Now:yyyyMMdd_HHmmss}.gif");
        var effect = ProfileCard.Effect;
        var xform = ProfileCard.RenderTransform;
        var ok = false;
        // The button shows an icon glyph; the progress text needs a text font meanwhile.
        var iconFont = label.FontFamily; var iconSize = label.FontSize;
        label.FontFamily = new FontFamily("Segoe UI"); label.FontSize = 11;
        try
        {
            _profileCapturing = true;
            foreach (var c in charts) { c.BeginManualDrift(); c.Marquee = true; }
            if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Collapsed; RefitProfile(); }
            ProfileCard.UpdateLayout();
            ProfileCard.Effect = null;
            ProfileCard.RenderTransform = Transform.Identity;

            var w = ProfileCard.ActualWidth; var h = ProfileCard.ActualHeight;
            var off = VisualTreeHelper.GetOffset(ProfileCard);
            var k = Math.Min(1, 1200 / w);
            int ox = (int)Math.Round(off.X * k), oy = (int)Math.Round(off.Y * k);
            int cw = (int)Math.Round(w * k), ch = (int)Math.Round(h * k);
            var rect = new Int32Rect(ox, oy, cw, ch);
            var bg = (ProfileCard.Background as SolidColorBrush)?.Color ?? Color.FromRgb(0x1B, 0x1B, 0x1F);

            byte[] Grab(double phase)
            {
                foreach (var c in charts) c.SetDriftPhase(phase);
                var rtb = new RenderTargetBitmap(ox + cw, oy + ch, 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(ProfileCard);
                var buf = new byte[cw * ch * 4];
                rtb.CopyPixels(rect, buf, cw * 4, 0);
                return buf;
            }

            var samples = Enumerable.Range(0, 16).Select(i => Grab(i / 16.0)).ToArray();
            _host.Settings.EnsureCapturesFolder();
            await using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            var enc = await Task.Run(() => new GifFrameEncoder(fs, cw, ch, delayCs, (bg.R, bg.G, bg.B), samples));
            Task? pending = null;
            for (var i = 0; i < frames; i++)
            {
                if (!ProfileCard.IsLoaded) break;
                var buf = Grab(i / (double)frames);
                if (pending != null) await pending;
                pending = Task.Run(() => enc.AddFrame(buf));
                label.Text = $"{(i + 1) * 100 / frames}%";
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            if (pending != null) await pending;
            ok = ProfileCard.IsLoaded;
            enc.Finish();
        }
        catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("gif", ex.Message); }
        finally
        {
            ProfileCard.Effect = effect;
            ProfileCard.RenderTransform = xform;
            _profileCapturing = false;
            foreach (var c in charts) { c.EndManualDrift(); c.Marquee = _host.Ui.ProfileAnimated; }
            if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Visible; RefitProfile(); }
            if (!ok) { try { File.Delete(file); } catch { } }
            _gifBusy = false;
        }
        if (ok)
        {
            // Pasting into a chat attaches the GIF file.
            try
            {
                var data = new DataObject();
                data.SetFileDropList([file]);
                data.SetData("GIF", File.ReadAllBytes(file));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("clipboard", ex.Message); }
        }
        label.FontFamily = iconFont; label.FontSize = iconSize;
        label.Text = idle;
        if (!ok) return;
        label.FontFamily = new FontFamily("Segoe UI"); label.FontSize = 16; label.Text = "✓";
        await Task.Delay(1200);
        label.FontFamily = iconFont; label.FontSize = iconSize; label.Text = idle;
    }
}
