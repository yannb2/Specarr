using System.Globalization;
using System.Text.RegularExpressions;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>
/// Works out where a file sits from its path:
/// <c>/movies/anime/Akira (1988)/Akira (1988) [...].mkv</c> or
/// <c>/tv/live-action/Show (2010)/Season 01/Show (2010) S01E02 [...].mkv</c>.
/// No I/O, so it is easy to test.
/// </summary>
public static partial class LibraryLayout
{
    public static LibraryPlace Parse(LibraryOptions library, string path, IReadOnlyCollection<string> extrasFolderNames)
    {
        var relative = Path.GetRelativePath(library.Path, path).Replace('\\', '/');
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var fileName = parts[^1];
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var folders = parts[..^1];

        string? category = null;
        var start = 0;
        if (library.Categories && folders.Length >= 2)
        {
            category = folders[0];
            start = 1;
        }

        string? titleFolder = null;
        string titleSource;
        string[] below = [];
        if (folders.Length > start)
        {
            titleFolder = Path.Combine(new[] { library.Path }.Concat(folders[..(start + 1)]).ToArray());
            titleSource = folders[start];
            below = folders[(start + 1)..];
        }
        else
        {
            // Loose in the root or the category: the file name stands for the title.
            titleSource = stem;
        }

        var (name, year) = SplitYear(titleSource);
        var isExtra = below.Any(f => extrasFolderNames.Contains(f, StringComparer.OrdinalIgnoreCase))
                      || ExtraSuffixRegex().IsMatch(stem);

        int? season = null, episode = null, episodeEnd = null;
        if (library.Kind == LibraryKind.Series)
        {
            foreach (var folder in below)
            {
                if (SeasonFolderRegex().Match(folder) is { Success: true } m)
                {
                    season = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                }
                else if (folder.Equals("Specials", StringComparison.OrdinalIgnoreCase))
                {
                    season = 0;
                }
            }

            if (EpisodeRegex().Match(stem) is { Success: true } e)
            {
                season ??= int.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);
                episode = int.Parse(e.Groups[2].Value, CultureInfo.InvariantCulture);
                if (e.Groups[3].Success)
                {
                    episodeEnd = int.Parse(e.Groups[3].Value, CultureInfo.InvariantCulture);
                }
            }
        }

        return new LibraryPlace
        {
            Category = category,
            TitleFolder = titleFolder,
            TitleName = name,
            Year = year,
            Season = season,
            Episode = episode,
            EpisodeEnd = episodeEnd,
            IsExtra = isExtra
        };
    }

    /// <summary>"Akira (1988)" -> ("Akira", 1988). Anything after the year (an edition, brackets) is dropped.</summary>
    public static (string Name, int? Year) SplitYear(string text)
    {
        if (YearRegex().Match(text) is { Success: true } m)
        {
            var name = m.Groups[1].Value.Trim();
            if (name.Length > 0)
            {
                return (name, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
            }
        }

        return (text.Trim(), null);
    }

    [GeneratedRegex(@"^(.*?)\s*\((\d{4})\)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^(?:Season|Saison|Series)\s*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S(\d{1,3})E(\d{1,4})(?:-?E(\d{1,4}))?", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeRegex();

    /// <summary>Jellyfin's suffixes for extras next to the movie: Movie-trailer.mkv.</summary>
    [GeneratedRegex(@"-(trailer|sample|featurette|behindthescenes|deleted|deletedscene|interview|scene|short|clip|extra|other)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExtraSuffixRegex();
}
