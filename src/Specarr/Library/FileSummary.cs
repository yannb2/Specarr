using System.Text.Json;
using System.Text.Json.Serialization;
using Specarr.Media;

namespace Specarr.Library;

/// <summary>
/// The short version of <see cref="MediaDetails"/> that the tables and
/// filters need, kept in memory for every file and saved in the cache as JSON.
/// </summary>
public sealed record FileSummary
{
    /// <summary>Bump when the summary or the source guess changes: older summaries are rebuilt from the cache without reading the files again.</summary>
    public const int CurrentVersion = 2;

    /// <summary>
    /// Summaries older than this need the file read again, not only rebuilt:
    /// version 2 reads audio bit rates (mediainfo per track, measured samples).
    /// </summary>
    public const int ReadVersion = 2;

    public string Container { get; init; } = "";
    public double? DurationSeconds { get; init; }
    public long? BitRate { get; init; }
    public long? VideoBitRate { get; init; }
    public string? Title { get; init; }

    public string? VideoCodec { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Resolution { get; init; }
    public int? BitDepth { get; init; }
    public double? FrameRate { get; init; }

    /// <summary>"DV 7 FEL HDR10", "HDR10+", "SDR".</summary>
    public string? DynamicRange { get; init; }

    public string? HdrFormat { get; init; }
    public string? DvProfile { get; init; }
    public string? DvLayer { get; init; }

    public List<AudioSummary> Audio { get; init; } = [];
    public List<SubtitleSummary> Subtitles { get; init; } = [];
    public int Chapters { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public SourceKind Source { get; init; }
    public Confidence SourceConfidence { get; init; }
    public string? Encoder { get; init; }

    [JsonIgnore] public TimeSpan? Duration => DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    /// <summary>"TrueHD Atmos 7.1"; the first audio track, which players pick by default.</summary>
    [JsonIgnore] public string? MainAudio => Audio.FirstOrDefault(a => a.IsDefault)?.Label ?? Audio.FirstOrDefault()?.Label;

    [JsonIgnore] public bool HasDolbyVision => DvProfile != null;

    public string? Tag(string name) => Tags.TryGetValue(name, out var value) ? value : null;

    public static FileSummary From(MediaDetails details)
    {
        var video = details.Video.FirstOrDefault();
        return new FileSummary
        {
            Container = details.Container,
            DurationSeconds = details.Duration?.TotalSeconds,
            BitRate = details.BitRate,
            VideoBitRate = details.VideoBitRate,
            Title = details.Title,
            VideoCodec = video?.Codec,
            Width = video?.Width,
            Height = video?.Height,
            Resolution = video?.Resolution,
            BitDepth = video?.BitDepth,
            FrameRate = video?.FrameRate,
            DynamicRange = video?.DynamicRange,
            HdrFormat = video?.HdrFormat,
            DvProfile = video?.DolbyVisionProfile,
            DvLayer = video?.EnhancementLayer ?? details.DolbyVision?.Layer,
            Audio = details.Audio.Select(a => new AudioSummary(a.Codec, a.ChannelLayout, a.Language, a.IsDefault, a.IsCommentary)
            {
                BitRate = a.BitRate,
                BitRateIsEstimate = a.BitRateIsEstimate
            }).ToList(),
            Subtitles = details.Subtitles.Select(s => new SubtitleSummary(s.Codec, s.Language, s.IsForced, s.IsHearingImpaired, s.IsDefault)).ToList(),
            Chapters = details.Chapters.Count,
            Tags = details.Tags
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase),
            Source = details.Source?.Kind ?? SourceKind.Unknown,
            SourceConfidence = details.Source?.Confidence ?? Confidence.Low,
            Encoder = details.EncoderLibrary
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static FileSummary? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            var summary = JsonSerializer.Deserialize<FileSummary>(json, JsonOptions);
            if (summary != null && summary.Tags.Comparer != StringComparer.OrdinalIgnoreCase)
            {
                summary = summary with { Tags = new Dictionary<string, string>(summary.Tags, StringComparer.OrdinalIgnoreCase) };
            }

            return summary;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AudioSummary(string Codec, string? Channels, string Language, bool IsDefault, bool IsCommentary)
{
    public long? BitRate { get; init; }
    public bool BitRateIsEstimate { get; init; }

    [JsonIgnore] public string Label => Channels == null ? Codec : $"{Codec} {Channels}";

    /// <summary>"4.2 Mb/s" or "≈ 4.2 Mb/s"; null when unknown.</summary>
    [JsonIgnore] public string? BitRateText => BitRate is { } bits ? (BitRateIsEstimate ? "≈ " : "") + Format.BitRate(bits) : null;
}

public sealed record SubtitleSummary(string Codec, string Language, bool IsForced, bool IsHearingImpaired, bool IsDefault);
