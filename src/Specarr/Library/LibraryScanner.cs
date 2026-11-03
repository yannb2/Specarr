using System.Diagnostics;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>A video file found on disk, before it is read.</summary>
public sealed record FoundFile(LibraryOptions Library, string Path, long Size, DateTime ModifiedUtc);

public sealed record WalkResult(IReadOnlyList<FoundFile> Files, IReadOnlyList<string> Problems, TimeSpan Elapsed);

/// <summary>
/// Lists the video files under each library root. Only folder listings: no
/// file is opened, so even a large library takes seconds.
/// </summary>
public static class LibraryScanner
{
    public static WalkResult Walk(SpecarrOptions options, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var files = new List<FoundFile>();
        var problems = new List<string>();
        var extensions = new HashSet<string>(options.VideoExtensions, StringComparer.OrdinalIgnoreCase);
        var ignored = new HashSet<string>(options.IgnoredFolderNames, StringComparer.OrdinalIgnoreCase);

        foreach (var library in options.Libraries)
        {
            var root = new DirectoryInfo(library.Path);
            if (!root.Exists)
            {
                problems.Add($"{library.Name}: {library.Path} is not mounted.");
                continue;
            }

            var pending = new Stack<DirectoryInfo>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var folder = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = folder.EnumerateFileSystemInfos().ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"Could not read {folder.FullName}: {e.Message}");
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (entry.Name.StartsWith('.'))
                    {
                        continue;
                    }

                    switch (entry)
                    {
                        case DirectoryInfo directory when !ignored.Contains(directory.Name)
                                                          && !directory.Attributes.HasFlag(FileAttributes.ReparsePoint):
                            pending.Push(directory);
                            break;
                        case FileInfo file when extensions.Contains(file.Extension):
                            files.Add(new FoundFile(library, file.FullName, file.Length, file.LastWriteTimeUtc));
                            break;
                    }
                }
            }
        }

        return new WalkResult(files, problems, watch.Elapsed);
    }
}
