using System.Globalization;
using System.Text.Json;

namespace Specarr.Media;

/// <summary>Turns ffprobe's JSON into <see cref="MediaDetails"/>. No I/O, so it is easy to test.</summary>
public static class MediaParser
{
    /// <summary>Container tags that are not worth showing: every muxer writes them.</summary>
    private static readonly HashSet<string> HiddenTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "encoder", "creation_time", "major_brand", "minor_version", "compatible_brands"
    };

    /// <summary>
    /// The tags tag_release.py writes, in the order they are shown. Anything
    /// else follows in alphabetical order.
    /// </summary>
    public static readonly string[] PreferredTagOrder =
    [
        "TMDB", "IMDB", "TVDB2", "RELEASE_NAME", "RELEASE_GROUP", "SOURCE", "QUALITY", "VIDEO_CODEC",
        "HDR_FORMAT", "DV_PROFILE", "EDITION", "ORIGINAL_LANGUAGE", "ABSOLUTE_EPISODE", "IMPORTED"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static FFprobeResult? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<FFprobeResult>(json, JsonOptions);
    }

    public static FFprobeFramesResult? DeserializeFrames(string json)
    {
        return JsonSerializer.Deserialize<FFprobeFramesResult>(json, JsonOptions);
    }

    public static MediaDetails Parse(string path, long sizeBytes, FFprobeResult probe)
    {
        var tracks = new List<TrackInfo>();
        var attachments = 0;

        foreach (var stream in probe.Streams)
        {
            switch (stream.CodecType)
            {
                case "video" when stream.Disposition?.AttachedPic == 1:
                case "attachment":
                    attachments++;
                    break;
                case "video":
                    tracks.Add(ParseTrack(stream, TrackType.Video));
                    break;
                case "audio":
                    tracks.Add(ParseTrack(stream, TrackType.Audio));
                    break;
                case "subtitle":
                    tracks.Add(ParseTrack(stream, TrackType.Subtitles));
                    break;
            }
        }

        var formatTags = probe.Format?.Tags ?? [];

        return new MediaDetails
        {
            Path = path,
            Container = ContainerName(probe.Format, path),
            SizeBytes = sizeBytes,
            Duration = ParseSeconds(probe.Format?.Duration),
            BitRate = ParseLong(probe.Format?.BitRate),
            Title = GetTag(formatTags, "title"),
            Tags = OrderTags(formatTags.Where(t => !HiddenTags.Contains(t.Key))),
            Tracks = tracks,
            Chapters = probe.Chapters
                .Select(c => new ChapterInfo(ParseSeconds(c.StartTime) ?? TimeSpan.Zero, GetTag(c.Tags, "title")))
                .ToList(),
            Attachments = attachments
        };
    }

    public static List<KeyValuePair<string, string>> OrderTags(IEnumerable<KeyValuePair<string, string>> tags)
    {
        return tags
            .OrderBy(t =>
            {
                var i = Array.FindIndex(PreferredTagOrder, p => string.Equals(p, t.Key, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            })
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static TrackInfo ParseTrack(FFprobeStream stream, TrackType type)
    {
        var codec = stream.CodecName ?? "";
        var language = GetTag(stream.Tags, "language") ?? "und";
        var disposition = stream.Disposition ?? new FFprobeDisposition();
        var dovi = stream.SideDataList?.FirstOrDefault(s =>
            string.Equals(s.SideDataType, FFprobeSideData.DoviConfigurationRecord, StringComparison.OrdinalIgnoreCase));

        return new TrackInfo
        {
            Index = stream.Index,
            Type = type,
            RawCodec = codec,
            Codec = type switch
            {
                TrackType.Video => VideoCodecName(codec),
                TrackType.Audio => AudioCodecName(codec, stream.Profile),
                _ => SubtitleCodecName(codec)
            },
            Language = language,
            LanguageName = Languages.Name(language),
            Title = TrackTitle(stream.Tags),
            IsDefault = disposition.Default == 1,
            IsForced = disposition.Forced == 1,
            IsHearingImpaired = disposition.HearingImpaired == 1,
            IsVisualImpaired = disposition.VisualImpaired == 1,
            IsCommentary = disposition.Comment == 1,
            IsOriginal = disposition.Original == 1,
            IsDub = disposition.Dub == 1,
            BitRate = ParseLong(stream.BitRate) ?? StatisticsBitRate(stream.Tags),
            Width = type == TrackType.Video ? stream.Width : null,
            Height = type == TrackType.Video ? stream.Height : null,
            BitDepth = type == TrackType.Video ? BitDepth(stream) : null,
            FrameRate = type == TrackType.Video ? ParseRate(stream.AverageFrameRate) ?? ParseRate(stream.FrameRate) : null,
            HdrFormat = type == TrackType.Video ? HdrFormat(stream.ColorTransfer) : null,
            DolbyVisionProfile = DolbyVisionProfile(dovi),
            HasEnhancementLayer = dovi is { DvProfile: 7, ElPresentFlag: 1 },
            IsInterlaced = type == TrackType.Video && stream.FieldOrder is { Length: > 0 } order
                && !string.Equals(order, "progressive", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(order, "unknown", StringComparison.OrdinalIgnoreCase),
            Channels = type == TrackType.Audio ? stream.Channels : null,
            ChannelLayout = type == TrackType.Audio ? ChannelLayout(stream.Channels, stream.ChannelLayout) : null,
            SampleRate = type == TrackType.Audio ? (int?)ParseLong(stream.SampleRate) : null
        };
    }

    public static string ContainerName(FFprobeFormat? format, string path)
    {
        var name = format?.FormatName ?? "";
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return name switch
        {
            _ when name.StartsWith("matroska", StringComparison.Ordinal) => extension == ".webm" ? "WebM" : "Matroska",
            _ when name.StartsWith("mov,mp4", StringComparison.Ordinal) => "MP4",
            "avi" => "AVI",
            "mpegts" => extension == ".m2ts" ? "Blu-ray (M2TS)" : "MPEG-TS",
            "mpeg" => "MPEG-PS",
            "asf" => "WMV",
            "" => extension.TrimStart('.').ToUpperInvariant(),
            _ => format?.FormatLongName ?? name
        };
    }

    public static string VideoCodecName(string codec)
    {
        return codec.ToLowerInvariant() switch
        {
            "hevc" => "HEVC",
            "h264" => "AVC",
            "av1" => "AV1",
            "vc1" => "VC-1",
            "mpeg2video" => "MPEG2",
            "mpeg4" => "MPEG-4",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "msmpeg4v3" => "DivX 3",
            _ => codec.ToUpperInvariant()
        };
    }

    /// <summary>Radarr's words, as in the file names: TrueHD Atmos, EAC3 Atmos, DTS-HD MA, DTS-X.</summary>
    public static string AudioCodecName(string codec, string? profile)
    {
        var atmos = profile?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true;
        return codec.ToLowerInvariant() switch
        {
            "truehd" => atmos ? "TrueHD Atmos" : "TrueHD",
            "eac3" => atmos ? "EAC3 Atmos" : "EAC3",
            "ac3" => "AC3",
            "dts" => DtsName(profile),
            "aac" => profile?.Contains("HE-AAC", StringComparison.OrdinalIgnoreCase) == true ? "HE-AAC" : "AAC",
            "flac" => "FLAC",
            "alac" => "ALAC",
            "opus" => "Opus",
            "vorbis" => "Vorbis",
            "mp3" => "MP3",
            "mp2" => "MP2",
            _ when codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) => "PCM",
            _ => codec.ToUpperInvariant()
        };
    }

    private static string DtsName(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
        {
            return "DTS";
        }

        if (profile.Contains("DTS:X", StringComparison.OrdinalIgnoreCase))
        {
            return "DTS-X";
        }

        foreach (var known in new[] { "DTS-HD MA", "DTS-HD HRA", "DTS-ES", "DTS Express", "DTS 96/24" })
        {
            if (profile.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return "DTS";
    }

    public static string SubtitleCodecName(string codec)
    {
        return codec.ToLowerInvariant() switch
        {
            "subrip" or "srt" => "SRT",
            "hdmv_pgs_subtitle" => "PGS",
            "ass" => "ASS",
            "ssa" => "SSA",
            "dvd_subtitle" => "VobSub",
            "dvb_subtitle" => "DVBSub",
            "mov_text" => "TX3G",
            "webvtt" => "WebVTT",
            "hdmv_text_subtitle" => "HDMV Text",
            _ => codec.ToUpperInvariant()
        };
    }

    public static string? HdrFormat(string? colorTransfer)
    {
        return colorTransfer?.ToLowerInvariant() switch
        {
            "smpte2084" => "HDR10",
            "arib-std-b67" => "HLG",
            _ => null
        };
    }

    /// <summary>"5", "7", "8.1"... Profile 8's suffix is the base layer: 1 = HDR10, 2 = SDR, 4 = HLG.</summary>
    public static string? DolbyVisionProfile(FFprobeSideData? dovi)
    {
        if (dovi?.DvProfile is not { } profile)
        {
            return null;
        }

        return profile == 8 && dovi.DvBlSignalCompatibilityId is > 0
            ? $"8.{dovi.DvBlSignalCompatibilityId}"
            : profile.ToString(CultureInfo.InvariantCulture);
    }

    public static bool HasHdr10Plus(FFprobeFramesResult? frames)
    {
        return frames?.Frames.Any(f => f.SideDataList?.Any(s =>
            s.SideDataType?.Contains("HDR10+", StringComparison.OrdinalIgnoreCase) == true
            || s.SideDataType?.Contains("2094-40", StringComparison.Ordinal) == true) == true) == true;
    }

    public static string? ChannelLayout(int? channels, string? layout)
    {
        var fromLayout = layout?.ToLowerInvariant() switch
        {
            null or "" => null,
            "mono" => "1.0",
            "stereo" => "2.0",
            "quad" or "quad(side)" => "4.0",
            var l when char.IsDigit(l[0]) => l.Split('(')[0],
            _ => null
        };

        if (fromLayout != null)
        {
            return fromLayout;
        }

        return channels switch
        {
            null or 0 => null,
            1 => "1.0",
            2 => "2.0",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            var n => $"{n}ch"
        };
    }

    /// <summary>Scope films (3840x1600) are still 2160p, so the width counts too.</summary>
    public static string ResolutionLabel(int width, int height)
    {
        return width >= 3200 || height >= 2000 ? "2160p"
            : width >= 1800 || height >= 1000 ? "1080p"
            : width >= 1200 || height >= 700 ? "720p"
            : height >= 560 ? "576p"
            : height >= 470 ? "480p"
            : $"{height}p";
    }

    private static int? BitDepth(FFprobeStream stream)
    {
        if (int.TryParse(stream.BitsPerRawSample, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bits) && bits > 0)
        {
            return bits;
        }

        var pixelFormat = stream.PixelFormat ?? "";
        if (pixelFormat.Contains("12", StringComparison.Ordinal)) return 12;
        if (pixelFormat.Contains("10", StringComparison.Ordinal)) return 10;
        return string.IsNullOrEmpty(pixelFormat) ? null : 8;
    }

    /// <summary>The track name: MKV keeps it in "title", MP4 in "name" (udta.name).</summary>
    private static string? TrackTitle(Dictionary<string, string>? tags)
    {
        return GetTag(tags, "title") ?? GetTag(tags, "name");
    }

    /// <summary>mkvmerge writes BPS (sometimes BPS-eng) as a track statistics tag.</summary>
    private static long? StatisticsBitRate(Dictionary<string, string>? tags)
    {
        return ParseLong(GetTag(tags, "BPS") ?? GetTag(tags, "BPS-eng"));
    }

    private static string? GetTag(Dictionary<string, string>? tags, string name)
    {
        if (tags == null)
        {
            return null;
        }

        foreach (var (key, value) in tags)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static TimeSpan? ParseSeconds(string? seconds)
    {
        return double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0
            ? TimeSpan.FromSeconds(s)
            : null;
    }

    private static long? ParseLong(string? value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static double? ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate))
        {
            return null;
        }

        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        return null;
    }
}
