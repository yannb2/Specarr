namespace Specarr.Media;

public enum TrackType
{
    Video,
    Audio,
    Subtitles
}

/// <summary>What Specarr shows about one media file, read from ffprobe.</summary>
public sealed class MediaDetails
{
    public required string Path { get; init; }

    /// <summary>"Matroska", "MP4"...</summary>
    public string Container { get; init; } = "";

    public long SizeBytes { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>Overall bit rate in bits per second.</summary>
    public long? BitRate { get; init; }

    /// <summary>The file (segment) title.</summary>
    public string? Title { get; init; }

    /// <summary>Global tags, without the title, muxing app and creation date.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Tags { get; init; } = [];

    public IReadOnlyList<TrackInfo> Tracks { get; init; } = [];

    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = [];

    /// <summary>Fonts and cover art.</summary>
    public int Attachments { get; init; }

    /// <summary>From dovi_tool, for the first frames. Null without Dolby Vision or when dovi_tool failed.</summary>
    public DolbyVisionDetails? DolbyVision { get; set; }

    /// <summary>Why <see cref="DolbyVision"/> is missing on a Dolby Vision file.</summary>
    public string? DolbyVisionError { get; set; }

    /// <summary>What wrote the file, from mediainfo: "MakeMKV v1.18.1", "mkvmerge v92.0". Null when mediainfo was not run.</summary>
    public string? WritingApplication { get; set; }

    /// <summary>The video encoder, from mediainfo: "x265", "x264". Null for a remux or a stream that does not say.</summary>
    public string? EncoderLibrary { get; set; }

    /// <summary>The encoder's settings line (x264/x265 options), when it wrote one.</summary>
    public string? EncoderSettings { get; set; }

    /// <summary>mediainfo's video bit rate, for files without track statistics.</summary>
    public long? MediaInfoVideoBitRate { get; set; }

    /// <summary>The guessed source (BDRemux, WEB-DL...), with the clues behind it.</summary>
    public SourceGuess? Source { get; set; }

    /// <summary>
    /// The main video track's bit rate: from the track, from mediainfo, or the
    /// overall bit rate minus the audio. Null when none of them is known.
    /// </summary>
    public long? VideoBitRate
    {
        get
        {
            var video = Video.FirstOrDefault();
            if (video?.BitRate is { } own)
            {
                return own;
            }

            if (MediaInfoVideoBitRate is { } fromMediaInfo)
            {
                return fromMediaInfo;
            }

            if (BitRate is not { } overall)
            {
                return null;
            }

            var audio = Audio.Sum(a => a.BitRate ?? 0);
            var estimate = overall - audio;
            return estimate > 0 ? estimate : null;
        }
    }

    /// <summary>True when <see cref="VideoBitRate"/> had to be worked out from the overall bit rate.</summary>
    public bool VideoBitRateIsEstimate => Video.FirstOrDefault()?.BitRate == null && MediaInfoVideoBitRate == null;

    public bool HasDolbyVision => Video.Any(v => v.DolbyVisionProfile != null);

    public IEnumerable<TrackInfo> Video => Tracks.Where(t => t.Type == TrackType.Video);
    public IEnumerable<TrackInfo> Audio => Tracks.Where(t => t.Type == TrackType.Audio);
    public IEnumerable<TrackInfo> Subtitles => Tracks.Where(t => t.Type == TrackType.Subtitles);

    public string? GetTag(string name)
    {
        return Tags.FirstOrDefault(t => string.Equals(t.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }
}

public sealed class TrackInfo
{
    /// <summary>Stream index in the file.</summary>
    public int Index { get; init; }

    public TrackType Type { get; init; }

    /// <summary>Codec in Radarr's words: HEVC, AVC, TrueHD Atmos, DTS-HD MA, PGS...</summary>
    public string Codec { get; init; } = "";

    /// <summary>ffprobe's codec name, e.g. hevc or truehd.</summary>
    public string RawCodec { get; init; } = "";

    /// <summary>ISO 639-2 code as stored, e.g. eng, fre, und.</summary>
    public string Language { get; init; } = "und";

    public string LanguageName { get; init; } = "Undetermined";

    /// <summary>Track name.</summary>
    public string? Title { get; init; }

    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsHearingImpaired { get; init; }
    public bool IsVisualImpaired { get; init; }
    public bool IsCommentary { get; init; }
    public bool IsOriginal { get; init; }
    public bool IsDub { get; init; }

    /// <summary>Bits per second, from the stream, the MKV statistics tags, mediainfo, or measured on samples.</summary>
    public long? BitRate { get; set; }

    /// <summary>True when <see cref="BitRate"/> was measured on a few samples of the file, not stored in it.</summary>
    public bool BitRateIsEstimate { get; set; }

    /// <summary>The peak bit rate from the stream header (mediainfo), e.g. for TrueHD with no average stored.</summary>
    public long? MaxBitRate { get; set; }

    /// <summary>"4.2 Mb/s", "≈ 4.2 Mb/s" (measured on samples) or "up to 9.6 Mb/s". Null when unknown.</summary>
    public string? BitRateText => BitRate is { } bits
        ? (BitRateIsEstimate ? "≈ " : "") + Format.BitRate(bits)
        : MaxBitRate is { } max ? $"up to {Format.BitRate(max)}" : null;

    // Video
    public int? Width { get; init; }
    public int? Height { get; init; }
    /// <summary>Video, and audio when ffprobe or mediainfo states it (lossless tracks).</summary>
    public int? BitDepth { get; set; }
    public double? FrameRate { get; init; }

    /// <summary>HDR10, HDR10+ or HLG; null for SDR.</summary>
    public string? HdrFormat { get; set; }

    /// <summary>Dolby Vision profile: "5", "7", "8.1"...</summary>
    public string? DolbyVisionProfile { get; init; }

    /// <summary>True for profile 7 with an enhancement layer (FEL or MEL; ffprobe cannot tell which).</summary>
    public bool HasEnhancementLayer { get; init; }

    /// <summary>Interlaced video (DVD, TV recordings).</summary>
    public bool IsInterlaced { get; init; }

    /// <summary>"FEL" or "MEL", from dovi_tool. Null when it was not read.</summary>
    public string? EnhancementLayer { get; set; }

    // Audio
    public int? Channels { get; init; }

    /// <summary>"7.1", "5.1", "2.0".</summary>
    public string? ChannelLayout { get; init; }

    public int? SampleRate { get; init; }

    /// <summary>The track's size in bytes, from mediainfo (or the MKV statistics tags).</summary>
    public long? StreamSize { get; set; }

    /// <summary>Subtitles: how many lines or pictures, from mediainfo's count of elements.</summary>
    public long? ElementCount { get; set; }

    /// <summary>2160p, 1080p, 720p, 576p, 480p.</summary>
    public string? Resolution => Height is null ? null : MediaParser.ResolutionLabel(Width ?? 0, Height.Value);

    /// <summary>"DV 7 HDR10", "HDR10+", "SDR". Null for anything but video.</summary>
    public string? DynamicRange
    {
        get
        {
            if (Type != TrackType.Video)
            {
                return null;
            }

            var parts = new List<string>();
            if (DolbyVisionProfile != null)
            {
                parts.Add(HasEnhancementLayer ? $"DV {DolbyVisionProfile} {EnhancementLayer ?? "(EL)"}" : $"DV {DolbyVisionProfile}");
            }

            if (HdrFormat != null)
            {
                parts.Add(HdrFormat);
            }

            return parts.Count == 0 ? "SDR" : string.Join(" ", parts);
        }
    }

    /// <summary>The flags that are set, in words.</summary>
    public IEnumerable<string> Flags
    {
        get
        {
            if (IsDefault) yield return "Default";
            if (IsForced) yield return "Forced";
            if (IsHearingImpaired) yield return "SDH";
            if (IsVisualImpaired) yield return "Audio description";
            if (IsCommentary) yield return "Commentary";
            if (IsOriginal) yield return "Original";
            if (IsDub) yield return "Dub";
        }
    }

    /// <summary>One line describing the track's format, without language and name.</summary>
    public string FormatSummary => BitRateText is { } rate ? $"{FormatDetails} · {rate}" : FormatDetails;

    /// <summary>The format without the bit rate, for tables that show it in its own column.</summary>
    public string FormatDetails
    {
        get
        {
            var parts = new List<string> { Codec };
            switch (Type)
            {
                case TrackType.Video:
                    if (Resolution != null) parts.Add(Resolution);
                    if (BitDepth != null) parts.Add($"{BitDepth}-bit");
                    if (DynamicRange != null) parts.Add(DynamicRange);
                    if (FrameRate != null) parts.Add($"{FrameRate:0.###} fps");
                    break;
                case TrackType.Audio:
                    if (ChannelLayout != null) parts.Add(ChannelLayout);
                    if (SampleRate != null) parts.Add($"{SampleRate / 1000.0:0.#} kHz");
                    if (BitDepth != null) parts.Add($"{BitDepth}-bit");
                    break;
                case TrackType.Subtitles:
                    if (ElementCount != null) parts.Add($"{ElementCount:N0} elements");
                    break;
            }

            if (StreamSize is { } size) parts.Add(Format.Size(size));

            return string.Join(" · ", parts);
        }
    }
}

public sealed record ChapterInfo(TimeSpan Start, string? Title);
