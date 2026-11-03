using System.Globalization;
using Specarr.Media;

namespace Specarr.Compare;

/// <summary>One line of the side-by-side table: the first file on the left, the other on the right.</summary>
public sealed record CompareRow(string Label, string? Left, string? Right, TrackInfo? LeftTrack = null, TrackInfo? RightTrack = null)
{
    public bool Differs => LeftTrack != null || RightTrack != null
        ? TrackKey(LeftTrack) != TrackKey(RightTrack)
        : !string.Equals(Left ?? "", Right ?? "", StringComparison.Ordinal);

    public bool IsTrack => LeftTrack != null || RightTrack != null;

    private static string TrackKey(TrackInfo? track)
    {
        return track == null
            ? ""
            : string.Join('|', track.Language, track.FormatDetails, track.Title, string.Join(',', track.Flags),
                // A measured bit rate varies a little from file to file; only a stored one counts as a difference.
                track.BitRateIsEstimate ? "" : track.BitRateText, track.StreamSizeText);
    }
}

public sealed record CompareSection(string Title, string Icon, IReadOnlyList<CompareRow> Rows)
{
    public int DifferenceCount => Rows.Count(r => r.Differs);
}

/// <summary>Builds the side-by-side comparison of two files.</summary>
public static class Comparison
{
    public static List<CompareSection> Build(MediaDetails left, MediaDetails right)
    {
        var sections = new List<CompareSection>
        {
            new("General", "bi-file-earmark-play", General(left, right)),
            new("Video", "bi-film", Video(left, right)),
            new("Dolby Vision", "bi-brightness-high", DolbyVision(left, right)),
            new("Audio", "bi-volume-up", Tracks("Audio", left.Audio, right.Audio)),
            new("Subtitles", "bi-chat-text", Tracks("Subtitle", left.Subtitles, right.Subtitles)),
            new("Tags", "bi-tags", Tags(left, right)),
            new("Chapters", "bi-bookmark", Chapters(left, right))
        };

        // A row empty on both sides (no bit rate in either file) says nothing.
        return sections
            .Select(s => s with { Rows = s.Rows.Where(r => r.IsTrack || !string.IsNullOrEmpty(r.Left) || !string.IsNullOrEmpty(r.Right)).ToList() })
            .Where(s => s.Rows.Count > 0)
            .ToList();
    }

    private static List<CompareRow> General(MediaDetails a, MediaDetails b)
    {
        return
        [
            new("File name", Path.GetFileName(a.Path), Path.GetFileName(b.Path)),
            new("Size", Format.Size(a.SizeBytes), Format.Size(b.SizeBytes)),
            new("Container", a.Container, b.Container),
            new("Duration", Format.Duration(a.Duration), Format.Duration(b.Duration)),
            new("Overall bit rate", BitRate(a.BitRate), BitRate(b.BitRate)),
            new("File title", a.Title, b.Title),
            new("Tracks", TrackCounts(a), TrackCounts(b)),
            new("Chapters", a.Chapters.Count.ToString(), b.Chapters.Count.ToString()),
            new("Attachments", a.Attachments.ToString(), b.Attachments.ToString()),
            new("Video bit rate", BitRate(a.VideoBitRate), BitRate(b.VideoBitRate)),
            new("Encoder", a.EncoderLibrary, b.EncoderLibrary),
            new("Guessed source", SourceText(a.Source), SourceText(b.Source))
        ];
    }

    private static string? SourceText(SourceGuess? guess)
    {
        return guess == null ? null : $"{guess.Word} ({guess.Confidence.ToString().ToLowerInvariant()} confidence)";
    }

    private static List<CompareRow> Video(MediaDetails a, MediaDetails b)
    {
        var left = a.Video.ToList();
        var right = b.Video.ToList();
        var rows = new List<CompareRow>();
        var count = Math.Max(left.Count, right.Count);

        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : null;
            var r = i < right.Count ? right[i] : null;
            var prefix = count > 1 ? $"Video {i + 1}: " : "";

            rows.Add(new($"{prefix}Codec", l?.Codec, r?.Codec));
            rows.Add(new($"{prefix}Resolution", Resolution(l), Resolution(r)));
            rows.Add(new($"{prefix}Dynamic range", l?.DynamicRange, r?.DynamicRange));
            rows.Add(new($"{prefix}Bit depth", l?.BitDepth is { } lb ? $"{lb}-bit" : null, r?.BitDepth is { } rb ? $"{rb}-bit" : null));
            rows.Add(new($"{prefix}Frame rate", l?.FrameRate?.ToString("0.###", CultureInfo.InvariantCulture), r?.FrameRate?.ToString("0.###", CultureInfo.InvariantCulture)));
            rows.Add(new($"{prefix}Bit rate", l?.BitRateText, r?.BitRateText));
            rows.Add(new($"{prefix}Stream size", l?.StreamSizeText, r?.StreamSizeText));
            rows.Add(new($"{prefix}Track name", l?.Title, r?.Title));
            rows.Add(new($"{prefix}Language", l?.LanguageName, r?.LanguageName));
        }

        return rows;
    }

    /// <summary>
    /// dovi_tool's summary lines side by side. Both sides read the same number of
    /// frames, so the frame count itself is left out.
    /// </summary>
    private static List<CompareRow> DolbyVision(MediaDetails a, MediaDetails b)
    {
        var left = a.DolbyVision;
        var right = b.DolbyVision;
        if (left == null && right == null)
        {
            return [];
        }

        var labels = (left?.Lines ?? []).Concat(right?.Lines ?? [])
            .Select(l => l.Key)
            .Where(k => !k.Equals("Frames", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return labels.Select(k => new CompareRow(k, left?.Get(k), right?.Get(k))).ToList();
    }

    /// <summary>Tracks side by side by position: the first audio track against the first audio track.</summary>
    private static List<CompareRow> Tracks(string label, IEnumerable<TrackInfo> a, IEnumerable<TrackInfo> b)
    {
        var left = a.ToList();
        var right = b.ToList();
        var count = Math.Max(left.Count, right.Count);
        var rows = new List<CompareRow>(count);

        for (var i = 0; i < count; i++)
        {
            var l = i < left.Count ? left[i] : null;
            var r = i < right.Count ? right[i] : null;
            rows.Add(new CompareRow($"{label} {i + 1}", null, null, l, r));
        }

        return rows;
    }

    private static List<CompareRow> Tags(MediaDetails a, MediaDetails b)
    {
        var keys = MediaParser.OrderTags(a.Tags.Concat(b.Tags)
                .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()))
            .Select(t => t.Key);

        return keys.Select(k => new CompareRow(k, a.GetTag(k), b.GetTag(k))).ToList();
    }

    private static List<CompareRow> Chapters(MediaDetails a, MediaDetails b)
    {
        var count = Math.Max(a.Chapters.Count, b.Chapters.Count);
        var rows = new List<CompareRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(new CompareRow($"Chapter {i + 1}",
                i < a.Chapters.Count ? Chapter(a.Chapters[i]) : null,
                i < b.Chapters.Count ? Chapter(b.Chapters[i]) : null));
        }

        return rows;
    }

    private static string Chapter(ChapterInfo chapter)
    {
        var time = $"{(int)chapter.Start.TotalHours}:{chapter.Start.Minutes:D2}:{chapter.Start.Seconds:D2}";
        return string.IsNullOrEmpty(chapter.Title) ? time : $"{time}  {chapter.Title}";
    }

    private static string TrackCounts(MediaDetails details)
    {
        return $"{details.Video.Count()} video, {details.Audio.Count()} audio, {details.Subtitles.Count()} subtitles";
    }

    private static string? Resolution(TrackInfo? track)
    {
        return track?.Width is { } w && track.Height is { } h ? $"{track.Resolution} ({w}×{h})" : null;
    }

    private static string? BitRate(long? bitsPerSecond)
    {
        return bitsPerSecond is { } b ? Format.BitRate(b) : null;
    }
}
