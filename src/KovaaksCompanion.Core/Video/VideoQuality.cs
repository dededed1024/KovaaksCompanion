using System.Text.Json;
using System.Text.Json.Serialization;

namespace KovaaksCompanion.Core.Video;

/// <summary>Recording quality preset; maps to the encoder's constant-quality target.</summary>
[JsonConverter(typeof(VideoQualityConverter))]
public enum VideoQuality { Low, Medium, High, Ultra }

public static class VideoQualityExtensions
{
    /// <summary>Constant-quality value for <see cref="VideoOptions.Quality"/> (lower = better).</summary>
    public static int ToCq(this VideoQuality q) => q switch
    {
        VideoQuality.Low => 28,
        VideoQuality.Medium => 23,
        VideoQuality.Ultra => 14,
        _ => 18,
    };
}

/// <summary>Writes the preset name; unknown or non-string values read as High so one bad field never resets the settings file.</summary>
sealed class VideoQualityConverter : JsonConverter<VideoQuality>
{
    public override VideoQuality Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && Enum.TryParse<VideoQuality>(reader.GetString(), true, out var q) && Enum.IsDefined(q)) return q;
        reader.Skip();
        return VideoQuality.High;
    }

    public override void Write(Utf8JsonWriter writer, VideoQuality value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
