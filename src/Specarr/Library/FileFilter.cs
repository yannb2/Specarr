using Specarr.Media;

namespace Specarr.Library;

/// <summary>The filters of the Files page. Kept apart from the page so the rules can be tested.</summary>
public sealed record FileFilter
{
    public string? Search { get; init; }
    public string? Library { get; init; }
    public string? Category { get; init; }

    /// <summary>2160p, 1080p, 720p or sd.</summary>
    public string? Resolution { get; init; }

    /// <summary>A video codec as shown: HEVC, AVC...</summary>
    public string? Codec { get; init; }

    /// <summary>sdr, hdr10, hdr10plus, hlg, dv, dv5, dv7, dv7fel, dv7mel, dv8.</summary>
    public string? Range { get; init; }

    /// <summary>An audio codec as shown (TrueHD Atmos, EAC3...), or lossless / atmos.</summary>
    public string? Audio { get; init; }

    /// <summary>A language code that must be among the audio tracks.</summary>
    public string? AudioLanguage { get; init; }

    /// <summary>A subtitle language code; with <see cref="SubtitlesMissing"/> it must be absent instead.</summary>
    public string? SubtitleLanguage { get; init; }

    public bool SubtitlesMissing { get; init; }

    /// <summary>A guessed source word: BDRemux, WEB-DL...; or "unknown".</summary>
    public string? Source { get; init; }

    /// <summary>Only files whose name or SOURCE tag disagrees with the guess.</summary>
    public bool MismatchOnly { get; init; }

    public bool IncludeExtras { get; init; }

    /// <summary>pending (not read yet) or error.</summary>
    public string? State { get; init; }

    public IEnumerable<IndexedFile> Apply(IEnumerable<IndexedFile> files)
    {
        if (!IncludeExtras)
        {
            files = files.Where(f => !f.Place.IsExtra);
        }

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var words = Search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            files = files.Where(f => words.All(w => f.Path.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                    || (f.Summary?.Title?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false)));
        }

        if (!string.IsNullOrEmpty(Library))
        {
            files = files.Where(f => f.Library.Name == Library);
        }

        if (!string.IsNullOrEmpty(Category))
        {
            files = files.Where(f => string.Equals(f.Place.Category, Category, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(Resolution))
        {
            files = Resolution == "sd"
                ? files.Where(f => f.Summary?.Resolution is { } r && Format.ResolutionRank(r) < 720)
                : files.Where(f => f.Summary?.Resolution == Resolution);
        }

        if (!string.IsNullOrEmpty(Codec))
        {
            files = files.Where(f => string.Equals(f.Summary?.VideoCodec, Codec, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(Range))
        {
            files = files.Where(f => f.Summary is { } s && MatchesRange(s, Range));
        }

        if (!string.IsNullOrEmpty(Audio))
        {
            files = files.Where(f => f.Summary is { } s && s.Audio.Any(a => MatchesAudio(a, Audio)));
        }

        if (!string.IsNullOrEmpty(AudioLanguage))
        {
            files = files.Where(f => f.Summary is { } s && s.Audio.Any(a => string.Equals(a.Language, AudioLanguage, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrEmpty(SubtitleLanguage))
        {
            files = files.Where(f => f.Summary is { } s
                                     && s.Subtitles.Any(t => string.Equals(t.Language, SubtitleLanguage, StringComparison.OrdinalIgnoreCase)) != SubtitlesMissing);
        }

        if (!string.IsNullOrEmpty(Source))
        {
            files = files.Where(f => f.Summary is { } s && string.Equals(s.Source.Word(), Source, StringComparison.OrdinalIgnoreCase));
        }

        if (MismatchOnly)
        {
            files = files.Where(f => f.SourceMismatch);
        }

        files = State switch
        {
            "pending" => files.Where(f => !f.IsProbed),
            "error" => files.Where(f => f.Error != null),
            _ => files
        };

        return files;
    }

    public static bool MatchesRange(FileSummary summary, string range)
    {
        var dv = summary.DvProfile ?? "";
        return range switch
        {
            "sdr" => summary.HdrFormat == null && summary.DvProfile == null,
            "hdr10" => summary.HdrFormat == "HDR10",
            "hdr10plus" => summary.HdrFormat == "HDR10+",
            "hlg" => summary.HdrFormat == "HLG",
            "dv" => summary.DvProfile != null,
            "dv5" => dv.StartsWith('5'),
            "dv7" => dv.StartsWith('7'),
            "dv7fel" => dv.StartsWith('7') && summary.DvLayer == "FEL",
            "dv7mel" => dv.StartsWith('7') && summary.DvLayer == "MEL",
            "dv8" => dv.StartsWith('8'),
            _ => true
        };
    }

    public static bool MatchesAudio(AudioSummary audio, string filter)
    {
        return filter switch
        {
            "lossless" => audio.Codec.StartsWith("TrueHD", StringComparison.Ordinal) || audio.Codec is "PCM" or "FLAC" or "ALAC" or "DTS-HD MA" or "DTS-X",
            "atmos" => audio.Codec.EndsWith("Atmos", StringComparison.Ordinal),
            _ => string.Equals(audio.Codec, filter, StringComparison.OrdinalIgnoreCase)
        };
    }
}
