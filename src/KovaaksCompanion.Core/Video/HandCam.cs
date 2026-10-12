using KovaaksCompanion.Core.Video.Obs;

namespace KovaaksCompanion.Core.Video;

/// <summary>Where and how large the hand-cam is composited into the game clip. Percentages: width of the video width, height of the video height; gaps from the right / top edge of the video. The cam image fills the box (cover, centered crop).</summary>
public sealed record HandCamLayout(int SizePercent = 25, int RightPercent = 0, int TopPercent = 20, int HeightPercent = 25);

/// <summary>One configured hand-cam: DirectShow name, its index among same-named devices, and its place in the video.</summary>
public sealed record HandCamSlot(string Device = "", int Number = 0, int Size = 25, int Right = 0, int Top = 20, int Height = 25)
{
    public const int Max = 3;
    public HandCamLayout Layout => new(Size, Right, Top, Height);
}

/// <summary>The configured cams as a list with value equality, so <see cref="AppSettings"/> still compares by content.</summary>
public sealed class HandCamSlots : List<HandCamSlot>, IEquatable<HandCamSlots>
{
    public bool Equals(HandCamSlots? other) => other != null && this.SequenceEqual(other);
    public override bool Equals(object? obj) => Equals(obj as HandCamSlots);
    public override int GetHashCode() => this.Aggregate(0, (h, s) => HashCode.Combine(h, s));
}

/// <summary>A DirectShow video device; <see cref="Number"/> tells apart devices that share a name (0 = first). <see cref="Id"/> is libobs's device id (name:path), empty for a saved but absent device.</summary>
public sealed record HandCamDevice(string Name, int Number, string Id = "")
{
    // identity is name + number (what the settings store); the libobs id only says how to open the device
    public bool Equals(HandCamDevice? o) => o != null && o.Name == Name && o.Number == Number;
    public override int GetHashCode() => HashCode.Combine(Name, Number);
    public override string ToString() => Number == 0 ? Name : $"{Name} ({Number + 1})";
}

/// <summary>What to keep per run: the cam composited into the game video, game and cam as separate files, or all three.</summary>
public enum HandCamSave { Composite, Separate }

/// <summary>Reads a saved name; unknown ones (e.g. the removed "Both") become <see cref="HandCamSave.Composite"/> instead of failing the whole settings file.</summary>
public sealed class HandCamSaveConverter : System.Text.Json.Serialization.JsonConverter<HandCamSave>
{
    public override HandCamSave Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
        => reader.TokenType == System.Text.Json.JsonTokenType.String && Enum.TryParse<HandCamSave>(reader.GetString(), out var v) ? v : HandCamSave.Composite;
    public override void Write(System.Text.Json.Utf8JsonWriter writer, HandCamSave value, System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>Hand-cam config read from the settings: empty <see cref="Device"/> = off.</summary>
public sealed record HandCamConfig(IReadOnlyList<HandCamSlot> Cams, HandCamSave Save)
{
    public static HandCamConfig From(AppSettings s)
        => new(s.HandCamEnabled ? s.HandCams.Where(c => c.Device.Length > 0).Take(HandCamSlot.Max).ToList() : [], s.HandCamSave);
}

/// <summary>DirectShow video devices as libobs's dshow_input source lists them.</summary>
public static class HandCamDeviceList
{
    /// <summary>Video devices in libobs order; repeated names get increasing numbers. Needs the libobs runtime started; empty otherwise.</summary>
    public static List<HandCamDevice> List()
    {
        var devices = new List<HandCamDevice>();
        if (!ObsRuntime.IsStarted) return devices;
        foreach (var (name, id) in ObsRuntime.DshowDevices())
            devices.Add(new HandCamDevice(name, devices.Count(d => d.Name == name), id));
        return devices;
    }

    /// <summary>libobs id of the saved device, null when it is not connected.</summary>
    public static string? Resolve(HandCamSlot slot, IReadOnlyList<HandCamDevice> devices)
        => devices.FirstOrDefault(d => d.Name == slot.Device && d.Number == slot.Number)?.Id;
}

/// <summary>Where a hand cam sits in a game frame of a given size (percentages of the frame, see <see cref="HandCamLayout"/>).</summary>
public static class HandCamGeometry
{
    /// <summary>Box the cam occupies: width / height percent of the frame, even pixels.</summary>
    public static (int Width, int Height) CamBox(int gameWidth, int gameHeight, HandCamLayout l)
        => (Math.Max(16, gameWidth * Math.Clamp(l.SizePercent, 10, 40) / 100 / 2 * 2), Math.Max(16, gameHeight * Math.Clamp(l.HeightPercent, 10, 40) / 100 / 2 * 2));

    /// <summary>Top-left corner of the cam box: its right / top gap in percent, clamped inside the frame.</summary>
    public static (int X, int Y) Position(int gameWidth, int gameHeight, int camWidth, int camHeight, HandCamLayout l)
    {
        double right = Math.Clamp(l.RightPercent, 0, 100) / 100.0, top = Math.Clamp(l.TopPercent, 0, 100) / 100.0;
        var x = Math.Max(0, Math.Min(gameWidth - camWidth, gameWidth - camWidth - gameWidth * right));
        var y = Math.Max(0, Math.Min(gameHeight - camHeight, gameHeight * top));
        return ((int)Math.Round(x), (int)Math.Round(y));
    }
}
