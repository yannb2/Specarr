using System.Globalization;
using System.Text.Json;

namespace Specarr.Media;

/// <summary>A track's bit rate as mediainfo reports it: the average when the file stores one, and the peak from the stream header.</summary>
public sealed record MediaInfoBitRate(int StreamOrder, long? BitRate, long? MaxBitRate);

/// <summary>
/// Audio bit rates for tracks the file does not state. Lossy codecs (AC3,
/// EAC3, DTS core) carry it in their header; lossless ones (TrueHD, DTS-HD MA,
/// FLAC, PCM) only have it in the MKV statistics tags mkvmerge writes. Without
/// them, this measures the audio packets of a few short samples spread over the
/// film: about a minute of reading, done once per file.
/// </summary>
public static class AudioBitRate
{
    /// <summary>How many samples, and how long each one is.</summary>
    public const int SampleCount = 6;

    public const int SampleSeconds = 10;

    /// <summary>
    /// Where the samples start, in seconds: spread between 5% and 95% of the
    /// film, so the studio logos and credits do not weigh too much. Null means
    /// "read it all" (short files).
    /// </summary>
    public static IReadOnlyList<double>? SampleStarts(TimeSpan? duration)
    {
        if (duration is not { } d || d.TotalSeconds <= SampleCount * SampleSeconds * 2)
        {
            return null;
        }

        var first = d.TotalSeconds * 0.05;
        var span = d.TotalSeconds * 0.90 - SampleSeconds;
        return Enumerable.Range(0, SampleCount)
            .Select(i => Math.Round(first + span * i / (SampleCount - 1), 1))
            .ToList();
    }

    /// <summary>ffprobe arguments that list the size and length of every audio packet in the samples.</summary>
    public static List<string> Arguments(string path, IReadOnlyList<double>? starts)
    {
        List<string> arguments = ["-v", "error", "-select_streams", "a", "-show_entries", "packet=stream_index,duration_time,size", "-of", "compact=p=0"];
        if (starts != null)
        {
            arguments.AddRange(["-read_intervals",
                string.Join(',', starts.Select(s => $"{s.ToString("0.#", CultureInfo.InvariantCulture)}%+{SampleSeconds}"))]);
        }

        arguments.Add(path);
        return arguments;
    }

    /// <summary>
    /// Adds up ffprobe's packet lines ("stream_index=1|duration_time=0.000833|size=40")
    /// into bits per second for each stream. Streams with less than a second of audio are left out.
    /// </summary>
    public static Dictionary<int, long> Parse(string output)
    {
        var bytes = new Dictionary<int, long>();
        var seconds = new Dictionary<int, double>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int? stream = null;
            double duration = 0;
            long size = 0;
            foreach (var field in line.Split('|'))
            {
                var equals = field.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var value = field[(equals + 1)..];
                switch (field[..equals])
                {
                    case "stream_index" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s):
                        stream = s;
                        break;
                    case "duration_time" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t):
                        duration = t;
                        break;
                    case "size" when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b):
                        size = b;
                        break;
                }
            }

            if (stream is not { } index || duration <= 0)
            {
                continue;
            }

            bytes[index] = bytes.GetValueOrDefault(index) + size;
            seconds[index] = seconds.GetValueOrDefault(index) + duration;
        }

        return bytes
            .Where(b => seconds[b.Key] >= 1)
            .ToDictionary(b => b.Key, b => (long)Math.Round(b.Value * 8 / seconds[b.Key]));
    }

    /// <summary>Measures the audio streams of a file. Empty when ffprobe fails.</summary>
    public static async Task<Dictionary<int, long>> MeasureAsync(string ffprobe, string path, TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(ffprobe, Arguments(path, SampleStarts(duration)),
            TimeSpan.FromMinutes(3), cancellationToken);
        return result.Success ? Parse(result.Output) : [];
    }

    /// <summary>The audio and video tracks' bit rates in <c>mediainfo --Output=JSON</c>, by stream order (ffprobe's index).</summary>
    public static List<MediaInfoBitRate> ParseMediaInfo(string? json)
    {
        var rates = new List<MediaInfoBitRate>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return rates;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("media", out var media) || !media.TryGetProperty("track", out var tracks)
                || tracks.ValueKind != JsonValueKind.Array)
            {
                return rates;
            }

            foreach (var track in tracks.EnumerateArray())
            {
                if (Text(track, "StreamOrder") is { } order
                    && int.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    rates.Add(new MediaInfoBitRate(index, Number(track, "BitRate"), Number(track, "BitRate_Maximum")));
                }
            }
        }
        catch (JsonException)
        {
        }

        return rates;
    }

    /// <summary>
    /// Fills in the audio bit rates the file did not state: mediainfo's average
    /// first, then the measured one (marked as an estimate). mediainfo's peak is kept too.
    /// </summary>
    public static void Apply(MediaDetails details, IEnumerable<MediaInfoBitRate>? mediaInfo, IReadOnlyDictionary<int, long>? measured)
    {
        var byStream = (mediaInfo ?? []).GroupBy(m => m.StreamOrder).ToDictionary(g => g.Key, g => g.First());
        foreach (var track in details.Audio)
        {
            byStream.TryGetValue(track.Index, out var info);
            track.MaxBitRate ??= info?.MaxBitRate;
            if (track.BitRate != null)
            {
                continue;
            }

            if (info?.BitRate is { } average)
            {
                track.BitRate = average;
            }
            else if (measured != null && measured.TryGetValue(track.Index, out var bits))
            {
                track.BitRate = bits;
                track.BitRateIsEstimate = true;
            }
        }
    }

    /// <summary>The audio tracks still without a bit rate after mediainfo.</summary>
    public static bool NeedsMeasuring(MediaDetails details, IEnumerable<MediaInfoBitRate>? mediaInfo)
    {
        var known = (mediaInfo ?? []).Where(m => m.BitRate != null).Select(m => m.StreamOrder).ToHashSet();
        return details.Audio.Any(a => a.BitRate == null && !known.Contains(a.Index));
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
