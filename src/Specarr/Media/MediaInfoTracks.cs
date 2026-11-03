using System.Globalization;
using System.Text.Json;

namespace Specarr.Media;

/// <summary>One track's facts from mediainfo that ffprobe leaves out, by stream order (ffprobe's index).</summary>
public sealed record MediaInfoTrackFacts(int StreamOrder, int? BitDepth, long? StreamSize, long? ElementCount);

/// <summary>
/// Bit depth (audio too: 16 or 24-bit for lossless tracks), stream size and,
/// for subtitles, the count of elements (the lines or pictures), from
/// <c>mediainfo --Output=JSON</c>. Shared with Salvarr/Specarr: same file in both.
/// </summary>
public static class MediaInfoTracks
{
    public static List<MediaInfoTrackFacts> Parse(string? json)
    {
        var facts = new List<MediaInfoTrackFacts>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return facts;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
            {
                return facts;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                if (Text(track, "StreamOrder") is { } order
                    && int.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    var bitDepth = Number(track, "BitDepth");
                    facts.Add(new MediaInfoTrackFacts(index, bitDepth is > 0 and < 64 ? (int)bitDepth : null,
                        Number(track, "StreamSize"), Number(track, "ElementCount")));
                }
            }
        }
        catch (JsonException)
        {
        }

        return facts;
    }

    /// <summary>Fills in what the tracks do not have yet; ffprobe's own values stay.</summary>
    public static void Apply(MediaDetails details, IEnumerable<MediaInfoTrackFacts>? facts)
    {
        var byStream = (facts ?? []).GroupBy(f => f.StreamOrder).ToDictionary(g => g.Key, g => g.First());
        foreach (var track in details.Tracks)
        {
            if (!byStream.TryGetValue(track.Index, out var info))
            {
                continue;
            }

            if (track.Type is TrackType.Video or TrackType.Audio)
            {
                track.BitDepth ??= info.BitDepth;
            }

            track.StreamSize ??= info.StreamSize;
            if (track.Type == TrackType.Subtitles)
            {
                track.ElementCount ??= info.ElementCount;
            }
        }
    }

    private static string? Text(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;
    }

    private static long? Number(JsonElement element, string name)
    {
        return Text(element, name) is { } text
               && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : null;
    }
}
