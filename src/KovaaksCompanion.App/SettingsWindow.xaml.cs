using System.Windows;
using KovaaksCompanion.Core;

namespace KovaaksCompanion.App;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings s)
    {
        InitializeComponent();
        Backdrop.Apply(this);
        Result = s;
        Kovaaks.Text = s.KovaaksPath; Ffmpeg.Text = s.FfmpegPath; Data.Text = s.DataFolder; Steam.Text = s.SteamId;
        Fps.Text = s.Fps.ToString(); Buffer.Text = s.BufferMinutes.ToString();
    }

    void Ok(object sender, RoutedEventArgs e)
    {
        Result = Result with
        {
            KovaaksPath = Kovaaks.Text.Trim(), FfmpegPath = Ffmpeg.Text.Trim(), DataFolder = Data.Text.Trim(), SteamId = Steam.Text.Trim(),
            Fps = int.TryParse(Fps.Text, out var f) ? f : Result.Fps,
            BufferMinutes = int.TryParse(Buffer.Text, out var b) ? b : Result.BufferMinutes,
        };
        DialogResult = true;
    }
}
