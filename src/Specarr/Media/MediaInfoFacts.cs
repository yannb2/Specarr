using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Specarr.Media;

/// <summary>
/// The few things mediainfo knows that ffprobe does not: which program wrote
/// the file (MakeMKV, mkvmerge) and which encoder made the video (x264, x265).
/// Both are clues for the source.
/// </summary>
public sealed record MediaInfoFacts
{
    public string? WritingApplication { get; init; }
    public string? EncoderLibrary { get; init; }
    public string? EncoderSettings { get; init; }
    public long? VideoBitRate { get; init; }

    /// <summary>Each track's bit rate (average and peak) as mediainfo reports it, by stream order.</summary>
    public List<MediaInfoBitRate> TrackBitRates { get; init; } = [];

    /// <summary>Reads <c>mediainfo --Output=JSON</c>. Null when the text is not mediainfo JSON.</summary>
    public static MediaInfoFacts? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        MediaInfoRoot? root;
        try
        {
            root = JsonSerializer.Deserialize<MediaInfoRoot>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        var tracks = root?.Media?.Tracks;
        if (tracks == null)
        {
            return null;
        }

        var general = tracks.FirstOrDefault(t => t.Type == "General");
        var video = tracks.FirstOrDefault(t => t.Type == "Video");

        return new MediaInfoFacts
        {
            WritingApplication = Get(general, "Encoded_Application"),
            EncoderLibrary = EncoderName(Get(video, "Encoded_Library_Name"), Get(video, "Encoded_Library")),
            EncoderSettings = Get(video, "Encoded_Library_Settings"),
            VideoBitRate = long.TryParse(Get(video, "BitRate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) && b > 0
                ? b
                : null,
            TrackBitRates = AudioBitRate.ParseMediaInfo(json)
        };
    }

    /// <summary>"x264 - core 164 r3095" -> x264. Keeps the name short for the table.</summary>
    internal static string? EncoderName(string? name, string? library)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        if (string.IsNullOrWhiteSpace(library))
        {
            return null;
        }

        var dash = library.IndexOf(" - ", StringComparison.Ordinal);
        return (dash > 0 ? library[..dash] : library).Trim();
    }

    private static string? Get(MediaInfoTrack? track, string key)
    {
        if (track?.Fields == null || !track.Fields.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private sealed class MediaInfoRoot
    {
        [JsonPropertyName("media")] public MediaInfoMedia? Media { get; set; }
    }

    private sealed class MediaInfoMedia
    {
        [JsonPropertyName("track")] public List<MediaInfoTrack>? Tracks { get; set; }
    }

    private sealed class MediaInfoTrack
    {
        [JsonPropertyName("@type")] public string? Type { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement>? Fields { get; set; }
    }
}
