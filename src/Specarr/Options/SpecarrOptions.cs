namespace Specarr.Options;

/// <summary>
/// Settings from appsettings.json, or from environment variables in Docker:
/// <c>Specarr__Libraries__0__Path=/movies</c>, <c>Specarr__RescanHours=6</c>.
/// </summary>
public class SpecarrOptions
{
    public const string Section = "Specarr";

    /// <summary>The library roots to browse, such as /movies and /tv.</summary>
    public List<LibraryOptions> Libraries { get; set; } = [];

    /// <summary>Extensions listed as video files. Anything else is ignored.</summary>
    public List<string> VideoExtensions { get; set; } = [];

    /// <summary>
    /// Folders never looked into. Folders whose name starts with a dot
    /// (.recycle, .Trash) are always skipped.
    /// </summary>
    public List<string> IgnoredFolderNames { get; set; } = [];

    /// <summary>
    /// Folders inside a movie or show folder that hold extras, as Jellyfin
    /// names them. Their files are listed as extras, hidden by default.
    /// </summary>
    public List<string> ExtrasFolderNames { get; set; } = [];

    /// <summary>The SQLite file that keeps what was read from each file, so a restart does not read everything again.</summary>
    public string DatabasePath { get; set; } = "/config/specarr.db";

    /// <summary>How often to look for new, changed and deleted files. 0 = only at start and on request.</summary>
    public double RescanHours { get; set; } = 6;

    /// <summary>Files read at the same time in the background. 1 keeps the disks quiet.</summary>
    public int ProbeConcurrency { get; set; } = 1;

    /// <summary>Probe the first frames of HDR10 video for HDR10+ metadata (about a second per file).</summary>
    public bool DetectHdr10Plus { get; set; } = true;

    /// <summary>Read the Dolby Vision RPU of the first 500 frames with dovi_tool (a second or two per file).</summary>
    public bool ReadDolbyVision { get; set; } = true;

    /// <summary>Run mediainfo too, for the encoder (x264, x265) and the writing application (MakeMKV): clues for the source.</summary>
    public bool ReadMediaInfo { get; set; } = true;

    public static readonly string[] DefaultVideoExtensions =
        [".mkv", ".mp4", ".m4v", ".avi", ".ts", ".m2ts", ".mov", ".webm", ".wmv", ".mpg", ".mpeg", ".iso"];

    public static readonly string[] DefaultIgnoredFolderNames = ["@eaDir", "#recycle", "lost+found", "$RECYCLE.BIN"];

    public static readonly string[] DefaultExtrasFolderNames =
    [
        "extras", "behind the scenes", "deleted scenes", "interviews", "scenes", "samples", "sample", "shorts",
        "featurettes", "clips", "other", "trailers", "theme-music", "backdrops"
    ];

    /// <summary>Fills in the defaults for lists the configuration left empty.</summary>
    public SpecarrOptions WithDefaults()
    {
        if (Libraries.Count == 0)
        {
            Libraries =
            [
                new LibraryOptions { Name = "Movies", Path = "/movies", Kind = LibraryKind.Movies },
                new LibraryOptions { Name = "TV", Path = "/tv", Kind = LibraryKind.Series }
            ];
        }

        if (VideoExtensions.Count == 0)
        {
            VideoExtensions = [..DefaultVideoExtensions];
        }

        if (IgnoredFolderNames.Count == 0)
        {
            IgnoredFolderNames = [..DefaultIgnoredFolderNames];
        }

        if (ExtrasFolderNames.Count == 0)
        {
            ExtrasFolderNames = [..DefaultExtrasFolderNames];
        }

        if (ProbeConcurrency < 1)
        {
            ProbeConcurrency = 1;
        }

        return this;
    }
}

public class LibraryOptions
{
    /// <summary>Shown in the UI, e.g. "Movies".</summary>
    public string Name { get; set; } = "";

    /// <summary>Path inside the container, e.g. /movies.</summary>
    public string Path { get; set; } = "";

    /// <summary>Movies: one folder per movie. Series: one folder per show, with seasons and episodes.</summary>
    public LibraryKind Kind { get; set; } = LibraryKind.Movies;

    /// <summary>
    /// True when the first level holds categories (/movies/anime/Title (Year)),
    /// false when title folders sit right under the root.
    /// </summary>
    public bool Categories { get; set; } = true;
}

public enum LibraryKind
{
    Movies,
    Series
}
