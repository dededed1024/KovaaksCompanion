using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>Part of the cam picture cut away on each side, as fractions of the picture (0-0.9) in the cam's own orientation (before any turn).</summary>
public sealed record WebcamCut(double Left = 0, double Top = 0, double Right = 0, double Bottom = 0)
{
    [System.Text.Json.Serialization.JsonIgnore] public double Width => 1 - Left - Right;
    [System.Text.Json.Serialization.JsonIgnore] public double Height => 1 - Top - Bottom;
    [System.Text.Json.Serialization.JsonIgnore] public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>Where and how large the webcam is composited into the game clip. Percentages: width of the video width the whole cam picture would take (the height follows the cam's own aspect; <see cref="WebcamLayout.Cut"/> then trims it, the scale stays); the cam's center as a position in the video from the left / top edge, pulled inside the frame when it would stick out.</summary>
public sealed record WebcamLayout(int SizePercent = 25, int XPercent = 100, int YPercent = 0, int Rotation = 0, WebcamCut? Cut = null)
{
    /// <summary>True when the cam is turned a quarter, so its box is portrait.</summary>
    public bool Sideways => Rotation is 90 or 270;
}

/// <summary>One configured webcam: DirectShow name, its index among same-named devices, its place in the video and its rotation (0, 90, 180 or 270 degrees clockwise).</summary>
/// <remarks><see cref="Cut"/>: parts of the picture cut away, null = none. <see cref="Aspect"/>: the cam's own width / height when known (settings preview only), 0 = unknown.</remarks>
public sealed record WebcamSlot(string Device = "", int Number = 0, int Size = 25, int X = 100, int Y = 0, int Rotation = 0, WebcamCut? Cut = null, double Aspect = 0)
{
    public const int Max = 3;
    public WebcamLayout Layout => new(Size, X, Y, Rotation, Cut);
}

/// <summary>The configured cams as a list with value equality, so <see cref="AppSettings"/> still compares by content.</summary>
public sealed class WebcamSlots : List<WebcamSlot>, IEquatable<WebcamSlots>
{
    public bool Equals(WebcamSlots? other) => other != null && this.SequenceEqual(other);
    public override bool Equals(object? obj) => Equals(obj as WebcamSlots);
    public override int GetHashCode() => this.Aggregate(0, (h, s) => HashCode.Combine(h, s));
}

/// <summary>A DirectShow video device; <see cref="Number"/> tells apart devices that share a name (0 = first). <see cref="Id"/> is libobs's device id (name:path), empty for a saved but absent device.</summary>
public sealed record WebcamDevice(string Name, int Number, string Id = "")
{
    // identity is name + number (what the settings store); the libobs id only says how to open the device
    public bool Equals(WebcamDevice? o) => o != null && o.Name == Name && o.Number == Number;
    public override int GetHashCode() => HashCode.Combine(Name, Number);
    public override string ToString() => Number == 0 ? Name : $"{Name} ({Number + 1})";
}

/// <summary>What to keep per run: the cam composited into the game video, game and cam as separate files, or all three.</summary>
public enum WebcamSave { Composite, Separate }

/// <summary>Reads a saved name; unknown ones (e.g. the removed "Both") become <see cref="WebcamSave.Composite"/> instead of failing the whole settings file.</summary>
public sealed class WebcamSaveConverter : System.Text.Json.Serialization.JsonConverter<WebcamSave>
{
    public override WebcamSave Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => reader.TokenType == System.Text.Json.JsonTokenType.String && Enum.TryParse<WebcamSave>(reader.GetString(), out var v) ? v : WebcamSave.Composite;
    public override void Write(System.Text.Json.Utf8JsonWriter writer, WebcamSave value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>webcam config read from the settings: empty <see cref="Device"/> = off.</summary>
public sealed record WebcamConfig(IReadOnlyList<WebcamSlot> Cams, WebcamSave Save)
{
    public static WebcamConfig From(AppSettings s)
        => new(s.WebcamEnabled ? s.Webcams.Where(c => c.Device.Length > 0).Take(WebcamSlot.Max).ToList() : [], s.WebcamSave);
}

/// <summary>DirectShow video devices as libobs's dshow_input source lists them.</summary>
public static class WebcamDeviceList
{
    /// <summary>Video devices in libobs order; repeated names get increasing numbers. Needs the libobs runtime started; empty otherwise.</summary>
    public static List<WebcamDevice> List()
    {
        var devices = new List<WebcamDevice>();
        if (!ObsRuntime.IsStarted) return devices;
        foreach (var (name, id) in ObsRuntime.DshowDevices())
            devices.Add(new WebcamDevice(name, devices.Count(d => d.Name == name), id));
        return devices;
    }

    /// <summary>libobs id of the saved device, null when it is not connected.</summary>
    public static string? Resolve(WebcamSlot slot, IReadOnlyList<WebcamDevice> devices)
        => devices.FirstOrDefault(d => d.Name == slot.Device && d.Number == slot.Number)?.Id;
}

/// <summary>Where a webcam sits in a game frame of a given size (percentages of the frame, see <see cref="WebcamLayout"/>).</summary>
public static class WebcamGeometry
{
    /// <summary>Shape assumed until the cam has delivered its first frame (and in the settings preview).</summary>
    public const double DefaultAspect = 16.0 / 9;

    /// <summary>Box the cam occupies in a frame, as shown (after the turn): the picture's own width (before any turn) is <see cref="WebcamLayout.SizePercent"/> of the frame width, the cut trims it; never larger than the frame.</summary>
    public static (double Width, double Height) BoxSize(double gameWidth, double gameHeight, WebcamLayout l, double sourceAspect = DefaultAspect)
    {
        var src = sourceAspect > 0 ? sourceAspect : DefaultAspect;
        double cw = l.Cut?.Width ?? 1, ch = l.Cut?.Height ?? 1;
        // the size sets the scale (the picture's own width), so turning the cam keeps its size
        var fullW = gameWidth * Math.Clamp(l.SizePercent, 10, 40) / 100;
        double w = l.Sideways ? fullW / src * ch : fullW * cw, h = l.Sideways ? fullW * cw : fullW / src * ch;
        if (h > gameHeight) { w *= gameHeight / h; h = gameHeight; }
        if (w > gameWidth) { h *= gameWidth / w; w = gameWidth; }
        return (w, h);
    }

    /// <summary>Same as <see cref="BoxSize"/> in even pixels.</summary>
    public static (int Width, int Height) CamBox(int gameWidth, int gameHeight, WebcamLayout l, double sourceAspect = DefaultAspect)
    {
        var (w, h) = BoxSize(gameWidth, gameHeight, l, sourceAspect);
        return (Math.Max(16, (int)w / 2 * 2), Math.Max(16, (int)h / 2 * 2));
    }

    /// <summary>Top-left corner of the cam box centered on the layout's position, pulled inside the frame.</summary>
    public static (int X, int Y) Position(int gameWidth, int gameHeight, double camWidth, double camHeight, WebcamLayout l)
    {
        var x = Math.Clamp(gameWidth * Math.Clamp(l.XPercent, 0, 100) / 100.0 - camWidth / 2.0, 0, Math.Max(0, gameWidth - camWidth));
        var y = Math.Clamp(gameHeight * Math.Clamp(l.YPercent, 0, 100) / 100.0 - camHeight / 2.0, 0, Math.Max(0, gameHeight - camHeight));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }
}

/// <summary>Reads a cam's own shape by opening it for a moment.</summary>
public static class WebcamProbe
{
    /// <summary>Width / height of what the device delivers, 0 when it gives no frame within <paramref name="wait"/> (or is busy, e.g. already recording).</summary>
    public static async Task<double> AspectAsync(string deviceId, TimeSpan wait)
    {
        if (!ObsRuntime.IsStarted || deviceId.Length == 0) return 0;
        var src = ObsRuntime.Run(() => ObsCamSource.CreateNative(deviceId, "probe"));
        if (src == 0) return 0;
        try
        {
            var until = DateTime.UtcNow + wait;
            while (DateTime.UtcNow < until)
            {
                var (w, h) = ObsRuntime.Run(() => ((int)ObsNative.obs_source_get_width(src), (int)ObsNative.obs_source_get_height(src)));
                if (w > 0 && h > 0) return (double)w / h;
                await Task.Delay(100);
            }
            return 0;
        }
        finally { ObsRuntime.Run(() => { ObsNative.obs_source_remove(src); ObsNative.obs_source_release(src); return true; }); }
    }
}
