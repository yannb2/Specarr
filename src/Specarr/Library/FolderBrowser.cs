using Specarr.Options;

namespace Specarr.Library;

/// <summary>What a file in a browsed folder is, for its icon and whether it can be opened.</summary>
public enum EntryKind
{
    Folder,
    Video,
    Audio,
    Subtitle,
    DiscImage,
    Archive,
    Other
}

/// <summary>A file or folder in a browsed folder.</summary>
public sealed record BrowseEntry
{
    public required string Name { get; init; }

    /// <summary>Path from the browsed root, e.g. torrents/radarr/Film.mkv. Goes in the address.</summary>
    public required string RelativePath { get; init; }

    public required string FullPath { get; init; }
    public required EntryKind Kind { get; init; }

    /// <summary>Null for a folder.</summary>
    public long? SizeBytes { get; init; }

    public DateTime ModifiedUtc { get; init; }

    /// <summary>Files and folders right inside a folder; null for a file or a folder that can't be read.</summary>
    public int? ItemCount { get; init; }

    public bool IsFolder => Kind == EntryKind.Folder;
    public bool IsHidden => Name.StartsWith('.');

    /// <summary>Specarr can show its tracks: a video or audio file (not a disc image: ffprobe can't read those).</summary>
    public bool CanOpen => Kind is EntryKind.Video or EntryKind.Audio;
}

/// <summary>One folder's content, or why it could not be read.</summary>
public sealed record FolderListing(string RelativePath, string FullPath, IReadOnlyList<BrowseEntry> Entries, string? Error);

/// <summary>
/// Lists folders outside the library (/downloads) one level at a time. Read-only:
/// it never writes, and never leaves the browsed root (no "..", no absolute paths).
/// </summary>
public static class FolderBrowser
{
    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mka", ".m4a", ".mp3", ".wav", ".ogg", ".opus", ".aac", ".ac3", ".eac3", ".dts", ".thd", ".wv", ".ape", ".dsf"
    };

    public static readonly IReadOnlySet<string> SubtitleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sup", ".sub", ".idx", ".vtt"
    };

    public static readonly IReadOnlySet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z", ".tar", ".gz", ".r00", ".r01", ".001"
    };

    public static readonly IReadOnlySet<string> DiscImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".iso", ".img", ".bin", ".cue", ".nrg"
    };

    public static BrowseFolderOptions? Find(SpecarrOptions options, string? slug) =>
        options.Browse.FirstOrDefault(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The full path of <paramref name="relative"/> under the root, or null when it would leave
    /// the root ("..", "."). A leading slash counts from the root, not from /.
    /// </summary>
    public static string? Resolve(string root, string? relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('/');
        if (rootFull.Length == 0)
        {
            rootFull = "/";
        }

        var clean = (relative ?? "").Replace('\\', '/').Trim().Trim('/');
        if (clean.Length == 0)
        {
            return rootFull;
        }

        if (clean.Split('/').Any(part => part is ".." or "."))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(rootFull, clean));
        return full == rootFull || full.StartsWith(rootFull.TrimEnd('/') + "/", StringComparison.Ordinal) ? full : null;
    }

    /// <summary>The path from the root, with forward slashes; "" for the root itself.</summary>
    public static string Relative(string root, string full)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(root), full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    public static EntryKind KindOf(string name, IEnumerable<string> videoExtensions)
    {
        var extension = Path.GetExtension(name);
        if (DiscImageExtensions.Contains(extension))
        {
            return EntryKind.DiscImage;
        }

        if (videoExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return EntryKind.Video;
        }

        if (AudioExtensions.Contains(extension))
        {
            return EntryKind.Audio;
        }

        if (SubtitleExtensions.Contains(extension))
        {
            return EntryKind.Subtitle;
        }

        // Split archives: Film.part01.rar, Film.r00, Film.7z.001.
        return ArchiveExtensions.Contains(extension) || System.Text.RegularExpressions.Regex.IsMatch(extension, @"^\.(r\d\d|\d{3})$")
            ? EntryKind.Archive
            : EntryKind.Other;
    }

    /// <summary>The folder's files and folders, folders first. A folder that can't be read gives an error, not an exception.</summary>
    public static FolderListing List(string root, string? relative, IEnumerable<string> videoExtensions)
    {
        var full = Resolve(root, relative);
        if (full == null)
        {
            return new FolderListing("", root, [], "That path is outside the folder.");
        }

        var rel = Relative(root, full);
        if (!Directory.Exists(full))
        {
            return new FolderListing(rel, full, [], File.Exists(full) ? "That is a file, not a folder." : "This folder does not exist (any more).");
        }

        var extensions = videoExtensions.ToList();
        var entries = new List<BrowseEntry>();
        try
        {
            foreach (var info in new DirectoryInfo(full).EnumerateFileSystemInfos())
            {
                try
                {
                    var childRel = rel.Length == 0 ? info.Name : $"{rel}/{info.Name}";
                    if (info is DirectoryInfo folder)
                    {
                        entries.Add(new BrowseEntry
                        {
                            Name = info.Name, RelativePath = childRel, FullPath = info.FullName, Kind = EntryKind.Folder,
                            ModifiedUtc = info.LastWriteTimeUtc, ItemCount = Count(folder)
                        });
                    }
                    else if (info is FileInfo file)
                    {
                        entries.Add(new BrowseEntry
                        {
                            Name = info.Name, RelativePath = childRel, FullPath = info.FullName, Kind = KindOf(info.Name, extensions),
                            SizeBytes = file.Exists ? file.Length : null, ModifiedUtc = info.LastWriteTimeUtc
                        });
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A file that vanished or can't be looked at: skip it, keep the rest.
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FolderListing(rel, full, entries, $"Could not read this folder: {e.Message}");
        }

        return new FolderListing(rel, full, entries, null);
    }

    private static int? Count(DirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateFileSystemInfos().Count();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
