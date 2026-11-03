using System.Text.RegularExpressions;

namespace Specarr.Media;

/// <summary>Where a file comes from, in the words the file names and the SOURCE tag use.</summary>
public enum SourceKind
{
    Unknown,
    BDRemux,
    BDRip,
    DVDRemux,
    DVDRip,
    WebDl,
    WebRip,
    Hdtv
}

public enum Confidence
{
    Low,
    Medium,
    High
}

/// <summary>One thing noticed about the file, and which sources it points to (positive) or away from (negative).</summary>
public sealed record SourceClue(string Text, IReadOnlyDictionary<SourceFamily, int> Points)
{
    public IEnumerable<SourceFamily> For => Points.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => p.Key);
    public IEnumerable<SourceFamily> Against => Points.Where(p => p.Value < 0).OrderBy(p => p.Value).Select(p => p.Key);
}

/// <summary>
/// The kind of source before the resolution is taken into account: a remux
/// of a 480p disc is a DVDRemux, of a 1080p or 2160p disc a BDRemux.
/// </summary>
public enum SourceFamily
{
    Remux,
    DiscEncode,
    WebDl,
    WebRip,
    Hdtv
}

/// <summary>The best guess, how sure it is, and the clues behind it.</summary>
public sealed record SourceGuess(
    SourceKind Kind,
    Confidence Confidence,
    IReadOnlyList<SourceClue> Clues,
    IReadOnlyDictionary<SourceFamily, int> Scores)
{
    public string Word => Kind.Word();
}

/// <summary>The source a file says it is, from the SOURCE tag or the file name.</summary>
public sealed record NamedSource(SourceKind Kind, string From);

public static class SourceKindExtensions
{
    /// <summary>The same words as Yann's file names and tag_release.py's SOURCE tag.</summary>
    public static string Word(this SourceKind kind) => kind switch
    {
        SourceKind.BDRemux => "BDRemux",
        SourceKind.BDRip => "BDRip",
        SourceKind.DVDRemux => "DVDRemux",
        SourceKind.DVDRip => "DVDRip",
        SourceKind.WebDl => "WEB-DL",
        SourceKind.WebRip => "WEBRip",
        SourceKind.Hdtv => "HDTV",
        _ => "Unknown"
    };

    public static string Describe(this SourceKind kind) => kind switch
    {
        SourceKind.BDRemux => "Blu-ray copied without re-encoding",
        SourceKind.BDRip => "Blu-ray re-encoded",
        SourceKind.DVDRemux => "DVD copied without re-encoding",
        SourceKind.DVDRip => "DVD re-encoded",
        SourceKind.WebDl => "Downloaded from a streaming service as it is",
        SourceKind.WebRip => "Streaming service, re-encoded",
        SourceKind.Hdtv => "TV broadcast",
        _ => "Not enough clues"
    };

    public static string Word(this SourceFamily family) => family switch
    {
        SourceFamily.Remux => "remux",
        SourceFamily.DiscEncode => "disc encode",
        SourceFamily.WebDl => "WEB-DL",
        SourceFamily.WebRip => "WEBRip",
        SourceFamily.Hdtv => "HDTV",
        _ => family.ToString()
    };
}

/// <summary>
/// Guesses the source from what is inside the file: video bit rate for its
/// resolution, codecs, the encoder tag, audio and subtitle formats, the Dolby
/// Vision profile, the container and chapters. Each clue adds or removes
/// points; the family with the most points wins. No I/O, so it is easy to test.
/// </summary>
public static partial class SourceGuesser
{
    private static readonly string[] Encoders =
        ["x264", "x265", "svt-av1", "svtav1", "rav1e", "libaom", "aom", "nvenc", "qsv", "amf", "handbrake", "xvid", "divx", "vp9"];

    public static SourceGuess Guess(MediaDetails details)
    {
        var clues = new List<SourceClue>();
        var video = details.Video.FirstOrDefault();
        if (video == null)
        {
            return new SourceGuess(SourceKind.Unknown, Confidence.Low, [], new Dictionary<SourceFamily, int>());
        }

        var width = video.Width ?? 0;
        var height = video.Height ?? 0;
        var resolution = video.Resolution ?? "";
        var isUhd = resolution == "2160p";
        var isFullHd = resolution == "1080p";
        var isHd = isUhd || isFullHd || resolution == "720p";
        var isSd = !isHd && height > 0;

        void Add(string text, params (SourceFamily Family, int Points)[] points)
        {
            clues.Add(new SourceClue(text, points.ToDictionary(p => p.Family, p => p.Points)));
        }

        // Container
        var extension = Path.GetExtension(details.Path).ToLowerInvariant();
        switch (extension)
        {
            case ".iso":
                Add("A disc image (.iso)", (SourceFamily.Remux, 8));
                break;
            case ".m2ts":
                Add("A Blu-ray stream file (.m2ts)", (SourceFamily.Remux, 3));
                break;
            case ".ts":
                Add("A transport stream (.ts), as TV recordings are", (SourceFamily.Hdtv, 3), (SourceFamily.Remux, 1));
                break;
            case ".mp4" or ".m4v":
                Add("An MP4 file: streaming downloads often are, disc copies rarely",
                    (SourceFamily.WebDl, 1), (SourceFamily.WebRip, 1), (SourceFamily.Remux, -2));
                break;
            case ".avi":
                Add("An AVI file: old re-encodes", (SourceFamily.DiscEncode, 2), (SourceFamily.WebRip, 1), (SourceFamily.Remux, -4));
                break;
        }

        // Video codec for its resolution
        switch (video.RawCodec)
        {
            case "mpeg2video" when isSd:
                Add("MPEG-2 video in SD: what DVDs hold, nobody re-encodes to it", (SourceFamily.Remux, 4), (SourceFamily.Hdtv, 1));
                break;
            case "mpeg2video":
                Add("MPEG-2 video in HD: early Blu-rays or a TV broadcast", (SourceFamily.Remux, 2), (SourceFamily.Hdtv, 3));
                break;
            case "vc1":
                Add("VC-1 video: only on Blu-ray and HD DVD discs, nobody re-encodes to it", (SourceFamily.Remux, 5), (SourceFamily.WebDl, -2));
                break;
            case "hevc" when isFullHd || resolution == "720p":
                Add($"HEVC at {resolution}: Blu-ray discs only use HEVC for 2160p, so it was re-encoded or streamed",
                    (SourceFamily.Remux, -5), (SourceFamily.DiscEncode, 2), (SourceFamily.WebDl, 1), (SourceFamily.WebRip, 1));
                break;
            case "h264" when isUhd:
                Add("AVC at 2160p: UHD Blu-rays are always HEVC", (SourceFamily.Remux, -5), (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 1));
                break;
            case "av1":
                Add("AV1 video: never on discs; some streaming services and re-encodes use it",
                    (SourceFamily.Remux, -6), (SourceFamily.DiscEncode, 2), (SourceFamily.WebDl, 2), (SourceFamily.WebRip, 2));
                break;
            case "mpeg4" or "msmpeg4v3" or "wmv3":
                Add($"{video.Codec} video: an old re-encode", (SourceFamily.Remux, -6), (SourceFamily.DiscEncode, 3), (SourceFamily.WebRip, 1));
                break;
            case "h264" when isSd && IsDvdFrame(width, height):
                Add($"AVC at the DVD frame size ({width}x{height}): a re-encoded DVD",
                    (SourceFamily.DiscEncode, 2), (SourceFamily.Remux, -3));
                break;
        }

        // Encoder tag and writing application
        if (details.EncoderLibrary is { } encoder && IsEncoder(encoder))
        {
            Add($"The video says it was encoded with {encoder}: re-encoded, so not a remux or a WEB-DL",
                (SourceFamily.Remux, -8), (SourceFamily.WebDl, -3), (SourceFamily.DiscEncode, 3), (SourceFamily.WebRip, 3));
        }
        else if (details.WritingApplication != null && details.EncoderLibrary == null)
        {
            Add("No encoder tag in the video: remuxes and WEB-DLs usually have none",
                (SourceFamily.Remux, 1), (SourceFamily.WebDl, 1));
        }

        if (details.WritingApplication is { } app)
        {
            if (app.Contains("MakeMKV", StringComparison.OrdinalIgnoreCase))
            {
                Add($"Written by {app}, which copies discs without re-encoding", (SourceFamily.Remux, 6));
            }
            else if (app.Contains("HandBrake", StringComparison.OrdinalIgnoreCase))
            {
                Add($"Written by {app}, a re-encoder", (SourceFamily.Remux, -6), (SourceFamily.DiscEncode, 3), (SourceFamily.WebRip, 2));
            }
        }

        // Video bit rate for its resolution
        if (details.VideoBitRate is { } bitRate)
        {
            var mbps = bitRate / 1_000_000.0;
            var how = details.VideoBitRateIsEstimate ? " (worked out from the overall bit rate)" : "";
            var rate = $"{mbps:0.#} Mb/s video{how}";
            if (isUhd)
            {
                if (mbps >= 45) Add($"{rate}: as high as UHD Blu-ray discs", (SourceFamily.Remux, 4), (SourceFamily.WebDl, -2));
                else if (mbps >= 30) Add($"{rate}: low for a UHD disc, high for a stream", (SourceFamily.Remux, 2), (SourceFamily.DiscEncode, 1));
                else if (mbps >= 14) Add($"{rate}: typical of 4K streams and good 4K encodes", (SourceFamily.Remux, -3), (SourceFamily.WebDl, 2), (SourceFamily.DiscEncode, 2));
                else Add($"{rate}: too low for a disc", (SourceFamily.Remux, -5), (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 2), (SourceFamily.WebRip, 2));
            }
            else if (isFullHd)
            {
                if (mbps >= 25) Add($"{rate}: as high as Blu-ray discs", (SourceFamily.Remux, 4), (SourceFamily.WebDl, -2));
                else if (mbps >= 17) Add($"{rate}: low for a Blu-ray, high for an encode", (SourceFamily.Remux, 2), (SourceFamily.DiscEncode, 1));
                else if (mbps >= 10) Add($"{rate}: typical of Blu-ray encodes", (SourceFamily.Remux, -2), (SourceFamily.DiscEncode, 2), (SourceFamily.WebDl, 1));
                else if (mbps >= 4.5) Add($"{rate}: typical of 1080p streams", (SourceFamily.Remux, -4), (SourceFamily.WebDl, 2), (SourceFamily.DiscEncode, 1), (SourceFamily.WebRip, 1));
                else Add($"{rate}: very low for 1080p", (SourceFamily.Remux, -5), (SourceFamily.WebRip, 2), (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 1));
            }
            else if (resolution == "720p")
            {
                if (mbps >= 9) Add($"{rate}: high for 720p, a Blu-ray encode", (SourceFamily.DiscEncode, 2), (SourceFamily.Remux, -2));
                else Add($"{rate}: typical of 720p streams and encodes", (SourceFamily.Remux, -3), (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 1), (SourceFamily.WebRip, 1));
            }
            else if (isSd)
            {
                if (video.RawCodec == "mpeg2video" && mbps >= 3.5) Add($"{rate}: as high as DVDs", (SourceFamily.Remux, 2));
                else if (mbps < 2.5) Add($"{rate}: a small SD encode", (SourceFamily.DiscEncode, 1), (SourceFamily.WebRip, 1), (SourceFamily.Remux, -2));
            }
        }

        if (isHd && resolution == "720p")
        {
            Add("720p: there are almost no 720p discs", (SourceFamily.Remux, -3));
        }

        if (video.IsInterlaced)
        {
            if (isSd) Add("Interlaced video: DVDs and TV", (SourceFamily.Remux, 1), (SourceFamily.Hdtv, 1));
            else Add("Interlaced HD video: a TV broadcast", (SourceFamily.Hdtv, 4));
        }

        // Dolby Vision and HDR10+
        if (video.DolbyVisionProfile is { } profile)
        {
            if (profile.StartsWith('7'))
                Add($"Dolby Vision profile 7{(video.EnhancementLayer is { } el ? $" {el}" : "")}: only on UHD Blu-ray discs",
                    (SourceFamily.Remux, 4), (SourceFamily.DiscEncode, 1), (SourceFamily.WebDl, -4), (SourceFamily.WebRip, -3));
            else if (profile.StartsWith('5'))
                Add("Dolby Vision profile 5: only streaming services use it, and re-encodes rarely keep it",
                    (SourceFamily.WebDl, 4), (SourceFamily.WebRip, 1), (SourceFamily.Remux, -4), (SourceFamily.DiscEncode, -2));
            else if (profile.StartsWith('8'))
                Add($"Dolby Vision profile {profile}: streaming, or converted from a disc's profile 7",
                    (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 1), (SourceFamily.Remux, -1));
        }

        if (video.HdrFormat == "HDR10+")
        {
            Add("HDR10+: common on Amazon streams, on a few discs", (SourceFamily.WebDl, 1));
        }

        // Audio
        var audio = details.Audio.ToList();
        var lossless = audio.Where(IsLossless).ToList();
        var dts = audio.Where(a => a.RawCodec == "dts" && !IsLossless(a)).ToList();
        if (lossless.Count > 0)
        {
            Add($"Lossless audio ({string.Join(", ", lossless.Select(a => a.Codec).Distinct())}): only discs have it, streaming services don't",
                (SourceFamily.Remux, 3), (SourceFamily.DiscEncode, 1), (SourceFamily.WebDl, -4), (SourceFamily.WebRip, -3));
        }
        else if (audio.Any(a => a.RawCodec == "flac"))
        {
            Add("FLAC audio: re-encoders turn a disc's PCM into FLAC", (SourceFamily.DiscEncode, 2), (SourceFamily.Remux, 1), (SourceFamily.WebDl, -2));
        }
        else if (dts.Count > 0)
        {
            Add("DTS audio: on discs, not on streaming services", (SourceFamily.Remux, 1), (SourceFamily.DiscEncode, 1), (SourceFamily.WebDl, -2));
        }

        if (audio.Count > 0 && lossless.Count == 0 && dts.Count == 0 && audio.All(a => a.RawCodec is "eac3" or "aac"))
        {
            if (audio.Any(a => a.RawCodec == "eac3"))
                Add("Only Dolby Digital Plus (EAC3) audio: what streaming services send",
                    (SourceFamily.WebDl, 3), (SourceFamily.WebRip, 2), (SourceFamily.Remux, -3));
            else
                Add("Only AAC audio: streams and small encodes", (SourceFamily.WebDl, 1), (SourceFamily.WebRip, 2), (SourceFamily.DiscEncode, 1), (SourceFamily.Remux, -3));
        }

        if (audio.Any(a => a.RawCodec == "opus"))
        {
            Add("Opus audio: only re-encoders use it", (SourceFamily.DiscEncode, 2), (SourceFamily.WebRip, 2), (SourceFamily.Remux, -4));
        }

        if (audio.Count > 0 && audio.All(a => a.RawCodec is "ac3" or "mp2"))
        {
            if (isSd) Add("Only Dolby Digital (AC3) audio, in SD: what DVDs hold", (SourceFamily.Remux, 1), (SourceFamily.DiscEncode, 1));
            else Add("Only Dolby Digital (AC3) audio in HD: TV, streams and encodes; Blu-rays usually have better",
                (SourceFamily.Hdtv, 2), (SourceFamily.WebDl, 1), (SourceFamily.DiscEncode, 1), (SourceFamily.Remux, -1));
        }

        var audioLanguages = audio.Select(a => a.Language).Where(l => l != "und").Distinct().Count();
        if (audioLanguages >= 3)
        {
            Add($"{audioLanguages} audio languages: typical of discs", (SourceFamily.Remux, 1), (SourceFamily.DiscEncode, 1));
        }

        // Subtitles
        var subtitles = details.Subtitles.ToList();
        if (subtitles.Any(s => s.RawCodec == "hdmv_pgs_subtitle"))
        {
            Add("PGS subtitles: picture subtitles copied from a Blu-ray", (SourceFamily.Remux, 2), (SourceFamily.DiscEncode, 1), (SourceFamily.WebDl, -2));
        }
        else if (subtitles.Any(s => s.RawCodec == "dvd_subtitle"))
        {
            Add("VobSub subtitles: picture subtitles copied from a DVD", (SourceFamily.Remux, 2), (SourceFamily.DiscEncode, 1), (SourceFamily.WebDl, -2));
        }
        else if (subtitles.Any(s => s.RawCodec == "dvb_subtitle"))
        {
            Add("DVB subtitles: from a TV broadcast", (SourceFamily.Hdtv, 4));
        }
        else if (subtitles.Any(s => s.RawCodec is "webvtt" or "mov_text"))
        {
            Add("WebVTT or MP4 text subtitles: from a streaming service", (SourceFamily.WebDl, 2), (SourceFamily.WebRip, 1));
        }
        else if (subtitles.Count > 0 && subtitles.All(s => s.RawCodec is "subrip" or "ass" or "ssa" or "text"))
        {
            Add("Only text subtitles (SRT/ASS): streams, or added later", (SourceFamily.WebDl, 1), (SourceFamily.WebRip, 1), (SourceFamily.Remux, -1));
        }

        // Chapters
        if (details.Chapters.Count == 0 && details.Duration is { TotalMinutes: > 40 })
        {
            Add("No chapters: discs almost always have them", (SourceFamily.Remux, -1), (SourceFamily.WebDl, 1), (SourceFamily.WebRip, 1));
        }

        // Totals
        var scores = Enum.GetValues<SourceFamily>().ToDictionary(f => f, _ => 0);
        foreach (var clue in clues)
        {
            foreach (var (family, points) in clue.Points)
            {
                scores[family] += points;
            }
        }

        var ranked = scores.OrderByDescending(s => s.Value).ToList();
        var best = ranked[0];
        var margin = best.Value - ranked[1].Value;
        if (best.Value <= 0 || clues.Count == 0)
        {
            return new SourceGuess(SourceKind.Unknown, Confidence.Low, clues, scores);
        }

        var confidence = margin >= 6 ? Confidence.High : margin >= 3 ? Confidence.Medium : Confidence.Low;
        return new SourceGuess(ToKind(best.Key, isSd), confidence, clues, scores);
    }

    /// <summary>A remux of an SD disc is a DVDRemux, of an HD disc a BDRemux.</summary>
    public static SourceKind ToKind(SourceFamily family, bool isSd) => family switch
    {
        SourceFamily.Remux => isSd ? SourceKind.DVDRemux : SourceKind.BDRemux,
        SourceFamily.DiscEncode => isSd ? SourceKind.DVDRip : SourceKind.BDRip,
        SourceFamily.WebDl => SourceKind.WebDl,
        SourceFamily.WebRip => SourceKind.WebRip,
        SourceFamily.Hdtv => SourceKind.Hdtv,
        _ => SourceKind.Unknown
    };

    /// <summary>The source the file claims: the SOURCE tag (tag_release.py) first, then a word in the file name.</summary>
    public static NamedSource? Named(string fileName, string? sourceTag)
    {
        if (!string.IsNullOrWhiteSpace(sourceTag) && FromWords(sourceTag) is { } fromTag)
        {
            return new NamedSource(fromTag, "SOURCE tag");
        }

        return FromWords(Path.GetFileNameWithoutExtension(fileName)) is { } fromName ? new NamedSource(fromName, "file name") : null;
    }

    /// <summary>Finds a source word: BDRemux, BDRip, DVDRemux, DVDRip, WEB-DL, WEBRip, HDTV, and the scene spellings.</summary>
    public static SourceKind? FromWords(string text)
    {
        // The most specific words first: DVDRemux before Remux, WEBRip before WEB.
        if (DvdRemuxRegex().IsMatch(text)) return SourceKind.DVDRemux;
        if (RemuxRegex().IsMatch(text)) return SourceKind.BDRemux;
        if (DvdRipRegex().IsMatch(text)) return SourceKind.DVDRip;
        if (WebRipRegex().IsMatch(text)) return SourceKind.WebRip;
        if (WebDlRegex().IsMatch(text)) return SourceKind.WebDl;
        if (BdRipRegex().IsMatch(text)) return SourceKind.BDRip;
        if (HdtvRegex().IsMatch(text)) return SourceKind.Hdtv;
        return null;
    }

    private static bool IsEncoder(string encoder)
    {
        return Encoders.Any(e => encoder.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLossless(TrackInfo track)
    {
        return track.RawCodec is "truehd" or "mlp"
               || track.RawCodec.StartsWith("pcm_", StringComparison.Ordinal)
               || track.Codec.StartsWith("DTS-HD MA", StringComparison.Ordinal)
               || track.Codec == "DTS-X";
    }

    private static bool IsDvdFrame(int width, int height)
    {
        return width is 720 or 704 && height is 480 or 576;
    }

    private const string Before = @"(?<![A-Za-z0-9])";
    private const string After = @"(?![A-Za-z0-9])";

    [GeneratedRegex(Before + @"DVD[ ._-]?Remux" + After, RegexOptions.IgnoreCase)]
    private static partial Regex DvdRemuxRegex();

    [GeneratedRegex(Before + @"(BD[ ._-]?Remux|Remux)" + After, RegexOptions.IgnoreCase)]
    private static partial Regex RemuxRegex();

    [GeneratedRegex(Before + @"(DVD[ ._-]?Rip|DVD(5|9)?)" + After, RegexOptions.IgnoreCase)]
    private static partial Regex DvdRipRegex();

    [GeneratedRegex(Before + @"WEB[ ._-]?Rip" + After, RegexOptions.IgnoreCase)]
    private static partial Regex WebRipRegex();

    [GeneratedRegex(Before + @"(WEB[ ._-]?DL|WEB)" + After, RegexOptions.IgnoreCase)]
    private static partial Regex WebDlRegex();

    [GeneratedRegex(Before + @"(BD[ ._-]?Rip|BR[ ._-]?Rip|Blu[ ._-]?Ray|BDRemux)" + After, RegexOptions.IgnoreCase)]
    private static partial Regex BdRipRegex();

    [GeneratedRegex(Before + @"(HDTV|PDTV|SDTV)" + After, RegexOptions.IgnoreCase)]
    private static partial Regex HdtvRegex();
}
