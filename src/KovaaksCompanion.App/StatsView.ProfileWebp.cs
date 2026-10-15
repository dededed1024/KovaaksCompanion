using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KovaaksCompanion.App;

public partial class StatsView
{
    bool _webpBusy;

    /// <summary>Saves the profile card as a looping animated WebP: one 7 s watermark drift cycle, rendered frame by frame.</summary>
    async Task SaveProfileWebpAsync(TextBlock label, string idle)
    {
        if (_webpBusy || ProfileCard.ActualWidth <= 0) return;
        _webpBusy = true;
        const int frames = 234, delayMs = 30; // ~33 fps x 7 s, the length of one drift loop
        var charts = _profileCharts.ToList();
        var file = Path.Combine(_host.Settings.ScreenshotsFolder, $"profile_{DateTime.Now:yyyyMMdd_HHmmss}.webp");
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

            byte[] Grab(double phase)
            {
                foreach (var c in charts) c.SetDriftPhase(phase);
                var rtb = new RenderTargetBitmap(ox + cw, oy + ch, 96 * k, 96 * k, PixelFormats.Pbgra32);
                rtb.Render(ProfileCard);
                var buf = new byte[cw * ch * 4];
                rtb.CopyPixels(rect, buf, cw * 4, 0);
                return buf;
            }

            _host.Settings.EnsureScreenshotsFolder();
            using var enc = new WebpFrameEncoder(cw, ch, delayMs);
            Task? pending = null;
            var swAll = System.Diagnostics.Stopwatch.StartNew(); long grabMs = 0, waitMs = 0, encMs = 0;
            for (var i = 0; i < frames; i++)
            {
                if (!ProfileCard.IsLoaded) break;
                var t0 = swAll.ElapsedMilliseconds;
                var buf = Grab(i / (double)frames);
                var t1 = swAll.ElapsedMilliseconds; grabMs += t1 - t0;
                if (pending != null) await pending;
                waitMs += swAll.ElapsedMilliseconds - t1;
                pending = Task.Run(() => { var s0 = swAll.ElapsedMilliseconds; enc.AddFrame(buf); System.Threading.Interlocked.Add(ref encMs, swAll.ElapsedMilliseconds - s0); });
                // Background priority waits for a full render pass per frame; yield rarely and at input priority.
                if (i % 6 == 5)
                {
                    label.Text = $"{(i + 1) * 100 / frames}%";
                    await Dispatcher.Yield(DispatcherPriority.Input);
                }
            }
            if (pending != null) await pending;
            KovaaksCompanion.Core.Diagnostics.AppLog.Write("webp", $"{cw}x{ch} total {swAll.ElapsedMilliseconds} ms, grab {grabMs}, wait-encoder {waitMs}, encode {encMs}");
            ok = ProfileCard.IsLoaded;
            if (ok)
            {
                label.Text = "…";
                ok = await enc.FinishAsync(file);
            }
        }
        catch (Exception ex) { KovaaksCompanion.Core.Diagnostics.AppLog.Write("webp", ex.Message); }
        finally
        {
            ProfileCard.Effect = effect;
            ProfileCard.RenderTransform = xform;
            _profileCapturing = false;
            foreach (var c in charts) { c.EndManualDrift(); c.Marquee = _host.Ui.ProfileAnimated; }
            if (_profileAddBox != null) { _profileAddBox.Visibility = Visibility.Visible; RefitProfile(); }
            if (!ok) { try { File.Delete(file); } catch (Exception e) { KovaaksCompanion.Core.Diagnostics.ErrorReporter.Report("profile webp delete", e, KovaaksCompanion.Core.Diagnostics.ErrorSeverity.Low); } }
            _webpBusy = false;
        }
        if (ok)
        {
            // Pasting into a chat attaches the WebP file.
            try
            {
                var data = new DataObject();
                data.SetFileDropList([file]);
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
