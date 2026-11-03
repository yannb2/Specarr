using System.Text;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>A video file in the library, with what the background scan read from it.</summary>
public sealed class IndexedFile
{
    public IndexedFile(string path, LibraryOptions library, LibraryPlace place, long sizeBytes, DateTime modifiedUtc)
    {
        Path = path;
        Library = library;
        Place = place;
        SizeBytes = sizeBytes;
        ModifiedUtc = modifiedUtc;
        Id = PathId.Encode(path);
        Named = SourceGuesser.Named(FileName, null);
    }

    public string Path { get; }
    public LibraryOptions Library { get; }
    public LibraryPlace Place { get; }
    public long SizeBytes { get; }
    public DateTime ModifiedUtc { get; }
    public string Id { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public long ModifiedTicks => ModifiedUtc.Ticks;

    /// <summary>Null until the file has been read.</summary>
    public FileSummary? Summary { get; private set; }

    /// <summary>Why the file could not be read.</summary>
    public string? Error { get; private set; }

    /// <summary>The source the file name or the SOURCE tag claims.</summary>
    public NamedSource? Named { get; private set; }

    public bool IsProbed => Summary != null || Error != null;

    /// <summary>
    /// Read by an older Specarr that did not read everything this one shows
    /// (see <see cref="FileSummary.ReadVersion"/>). Its summary is shown while it waits to be read again.
    /// </summary>
    public bool NeedsRefresh { get; set; }

    /// <summary>
    /// The guess and the name disagree. A guess with low confidence never
    /// counts: it is a hint, not a finding.
    /// </summary>
    public bool SourceMismatch => Summary is { } s && Named is { } n
                                  && s.Source != SourceKind.Unknown && s.SourceConfidence != Confidence.Low
                                  && s.Source != n.Kind;

    public void SetResult(FileSummary? summary, string? error)
    {
        Summary = summary;
        Error = summary == null ? error : null;
        NeedsRefresh = false;
        Named = SourceGuesser.Named(FileName, summary?.Tag("SOURCE"));
    }
}

/// <summary>Where a file sits in the library: category, movie or show folder, season and episode.</summary>
public sealed record LibraryPlace
{
    /// <summary>live-action, anime, animated, other. Null for a library without categories.</summary>
    public string? Category { get; init; }

    /// <summary>The movie or show folder, e.g. /movies/anime/Akira (1988). Null for a file loose in the root.</summary>
    public string? TitleFolder { get; init; }

    /// <summary>"Akira", without the year.</summary>
    public string TitleName { get; init; } = "";

    public int? Year { get; init; }

    public int? Season { get; init; }
    public int? Episode { get; init; }

    /// <summary>The last episode of a multi-episode file (S01E01-E02).</summary>
    public int? EpisodeEnd { get; init; }

    /// <summary>A trailer, featurette or other extra.</summary>
    public bool IsExtra { get; init; }

    /// <summary>"S01E02" or "S01E01-E02", or null.</summary>
    public string? EpisodeLabel => Season is { } s && Episode is { } e
        ? EpisodeEnd is { } end && end != e ? $"S{s:00}E{e:00}-E{end:00}" : $"S{s:00}E{e:00}"
        : null;

    public string TitleWithYear => Year is { } y ? $"{TitleName} ({y})" : TitleName;
}

/// <summary>A movie or a show: one title folder and the files in it.</summary>
public sealed class TitleEntry
{
    public required string Id { get; init; }
    public required LibraryOptions Library { get; init; }
    public required string? Category { get; init; }
    public required string? Folder { get; init; }
    public required string Name { get; init; }
    public required int? Year { get; init; }
    public required IReadOnlyList<IndexedFile> Files { get; init; }

    public string NameWithYear => Year is { } y ? $"{Name} ({y})" : Name;

    public IEnumerable<IndexedFile> MainFiles => Files.Where(f => !f.Place.IsExtra);

    public long SizeBytes => Files.Sum(f => f.SizeBytes);

    public DateTime NewestUtc => Files.Count == 0 ? DateTime.MinValue : Files.Max(f => f.ModifiedUtc);

    /// <summary>The best resolution among the main files, e.g. 2160p.</summary>
    public string? BestResolution => MainFiles
        .Select(f => f.Summary?.Resolution)
        .Where(r => r != null)
        .OrderByDescending(Format.ResolutionRank)
        .FirstOrDefault();

    public int SeasonCount => MainFiles.Select(f => f.Place.Season).Where(s => s is > 0).Distinct().Count();
}

/// <summary>A file path as a URL-safe id, so pages can link to /file/{id}.</summary>
public static class PathId
{
    public static string Encode(string path)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(path))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string? Decode(string id)
    {
        try
        {
            var base64 = id.Replace('-', '+').Replace('_', '/');
            base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
