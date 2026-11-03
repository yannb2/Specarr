using System.Text.Json.Serialization;

namespace Specarr.Media;

// The parts of `ffprobe -print_format json -show_streams -show_chapters -show_format`
// that Specarr reads. Numbers ffprobe can print as "N/A" are kept as strings.

public class FFprobeResult
{
    [JsonPropertyName("streams")] public List<FFprobeStream> Streams { get; set; } = [];

    [JsonPropertyName("chapters")] public List<FFprobeChapter> Chapters { get; set; } = [];

    [JsonPropertyName("format")] public FFprobeFormat? Format { get; set; }
}

public class FFprobeStream
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }

    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }

    [JsonPropertyName("codec_long_name")] public string? CodecLongName { get; set; }

    /// <summary>Tells DTS from DTS-HD MA, and plain TrueHD from TrueHD Atmos.</summary>
    [JsonPropertyName("profile")] public string? Profile { get; set; }

    [JsonPropertyName("width")] public int? Width { get; set; }

    [JsonPropertyName("height")] public int? Height { get; set; }

    [JsonPropertyName("pix_fmt")] public string? PixelFormat { get; set; }

    [JsonPropertyName("bits_per_raw_sample")] public string? BitsPerRawSample { get; set; }

    [JsonPropertyName("r_frame_rate")] public string? FrameRate { get; set; }

    [JsonPropertyName("avg_frame_rate")] public string? AverageFrameRate { get; set; }

    /// <summary>smpte2084 = PQ (HDR10), arib-std-b67 = HLG.</summary>
    [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; set; }

    [JsonPropertyName("color_primaries")] public string? ColorPrimaries { get; set; }

    /// <summary>progressive, tt, bb, tb, bt. Anything but progressive is interlaced (DVD, TV).</summary>
    [JsonPropertyName("field_order")] public string? FieldOrder { get; set; }

    [JsonPropertyName("channels")] public int? Channels { get; set; }

    [JsonPropertyName("channel_layout")] public string? ChannelLayout { get; set; }

    [JsonPropertyName("sample_rate")] public string? SampleRate { get; set; }

    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }

    [JsonPropertyName("duration")] public string? Duration { get; set; }

    /// <summary>Dolby Vision files carry a "DOVI configuration record" here.</summary>
    [JsonPropertyName("side_data_list")] public List<FFprobeSideData>? SideDataList { get; set; }

    [JsonPropertyName("disposition")] public FFprobeDisposition? Disposition { get; set; }

    /// <summary>
    /// Track tags: language, title, and for MKV the statistics mkvmerge writes
    /// (BPS, DURATION, NUMBER_OF_FRAMES...).
    /// </summary>
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
}

public class FFprobeSideData
{
    public const string DoviConfigurationRecord = "DOVI configuration record";

    [JsonPropertyName("side_data_type")] public string? SideDataType { get; set; }

    [JsonPropertyName("dv_profile")] public int? DvProfile { get; set; }

    [JsonPropertyName("dv_level")] public int? DvLevel { get; set; }

    [JsonPropertyName("el_present_flag")] public int? ElPresentFlag { get; set; }

    [JsonPropertyName("dv_bl_signal_compatibility_id")] public int? DvBlSignalCompatibilityId { get; set; }
}

public class FFprobeDisposition
{
    [JsonPropertyName("default")] public int Default { get; set; }

    [JsonPropertyName("forced")] public int Forced { get; set; }

    [JsonPropertyName("hearing_impaired")] public int HearingImpaired { get; set; }

    [JsonPropertyName("visual_impaired")] public int VisualImpaired { get; set; }

    [JsonPropertyName("comment")] public int Comment { get; set; }

    [JsonPropertyName("original")] public int Original { get; set; }

    [JsonPropertyName("dub")] public int Dub { get; set; }

    /// <summary>Cover art. ffprobe lists it as a video stream; it is not a real track.</summary>
    [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
}

public class FFprobeChapter
{
    [JsonPropertyName("start_time")] public string? StartTime { get; set; }

    [JsonPropertyName("end_time")] public string? EndTime { get; set; }

    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
}

public class FFprobeFormat
{
    [JsonPropertyName("format_name")] public string? FormatName { get; set; }

    [JsonPropertyName("format_long_name")] public string? FormatLongName { get; set; }

    [JsonPropertyName("duration")] public string? Duration { get; set; }

    [JsonPropertyName("size")] public string? Size { get; set; }

    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }

    /// <summary>
    /// Container tags. For MKV these are the global tags (TMDB, RELEASE_NAME...)
    /// plus the segment title, the muxing app and the creation date.
    /// </summary>
    [JsonPropertyName("tags")] public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Output of <c>ffprobe -show_frames</c>: only the per-frame side data is read.</summary>
public class FFprobeFramesResult
{
    [JsonPropertyName("frames")] public List<FFprobeFrame> Frames { get; set; } = [];
}

public class FFprobeFrame
{
    [JsonPropertyName("side_data_list")] public List<FFprobeSideData>? SideDataList { get; set; }
}
