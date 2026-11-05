using Specarr.Options;

namespace Specarr.Library;

/// <summary>Where Specarr shows a path, or why it can't.</summary>
public sealed record OpenResult(string? Link, string? Problem, bool WaitForIndex = false);

/// <summary>
/// The page for a path another app links to (arrkit's "Open in Specarr"): a library file, a movie or show
/// folder, or a file or folder in a browsed folder (/downloads, /mnt/pc).
/// </summary>
public static class OpenTarget
{
    public static OpenResult Resolve(IndexSnapshot snapshot, bool indexLoading, SpecarrOptions options, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
        {
            return new OpenResult(null, "No path to open: the link should end with ?path=/full/path.");
        }

        var full = path.Trim();
        full = full.Length > 1 ? full.TrimEnd('/') : full;
        if (full.Split('/').Any(part => part is ".." or "."))
        {
            return new OpenResult(null, "That path has .. or . in it.");
        }

        // The library first: its file page has Compare with the other files of the title.
        if (snapshot.FilesById.GetValueOrDefault(PathId.Encode(full)) is { } file)
        {
            return new OpenResult($"file/{file.Id}", null);
        }

        if (snapshot.TitlesByFolder.GetValueOrDefault(full) is { } title)
        {
            return new OpenResult($"title/{title.Id}", null);
        }

        if (LibraryIndex.IsInLibrary(options, full) || options.Libraries.Any(l => l.Path.TrimEnd('/') == full))
        {
            if (indexLoading)
            {
                return new OpenResult(null, null, WaitForIndex: true);
            }

            return Directory.Exists(full)
                ? new OpenResult("", null)
                : new OpenResult(null, File.Exists(full)
                    ? $"{full} is in the library but not in Specarr's list yet. Settings > Status > Look for new files now."
                    : $"{full} is not there (any more).");
        }

        foreach (var root in options.Browse.OrderByDescending(b => b.Path.Length))
        {
            var rootPath = root.Path.TrimEnd('/');
            if (rootPath.Length == 0 || (full != rootPath && !full.StartsWith(rootPath + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            var relative = FolderBrowser.Relative(rootPath, full);
            if (Directory.Exists(full))
            {
                return new OpenResult(relative.Length == 0 ? $"browse/{root.Slug}" : $"browse/{root.Slug}?path={Uri.EscapeDataString(relative)}", null);
            }

            if (File.Exists(full))
            {
                return new OpenResult($"browse/{root.Slug}/file?path={Uri.EscapeDataString(relative)}", null);
            }

            return new OpenResult(null, $"{full} is not there (any more): it may have been imported or cleaned up.");
        }

        return new OpenResult(null,
            $"Specarr can't see {full}: it is not in the library ({string.Join(", ", options.Libraries.Select(l => l.Path))}) " +
            $"or a browsed folder ({string.Join(", ", options.Browse.Select(b => b.Path))}). Mount it in the specarr service.");
    }
}
