using Specarr.Media;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>The numbers /api/stats returns, for a Homepage tile.</summary>
public sealed record LibraryStats(
    int Movies,
    int Shows,
    int Episodes,
    int Files,
    long SizeBytes,
    string Size,
    int Read,
    int Pending,
    int Errors,
    int Uhd,
    int DolbyVision,
    int SourceMismatches,
    bool Loading,
    int ToolUpdates = 0)
{
    public static LibraryStats From(LibraryIndex index)
    {
        var snapshot = index.Snapshot;
        var main = snapshot.Files.Where(f => !f.Place.IsExtra).ToList();
        var size = snapshot.Files.Sum(f => f.SizeBytes);
        return new LibraryStats(
            Movies: snapshot.Titles.Count(t => t.Library.Kind == LibraryKind.Movies && t.MainFiles.Any()),
            Shows: snapshot.Titles.Count(t => t.Library.Kind == LibraryKind.Series && t.MainFiles.Any()),
            Episodes: main.Count(f => f.Library.Kind == LibraryKind.Series && f.Place.Episode != null),
            Files: snapshot.Files.Count,
            SizeBytes: size,
            Size: Format.Size(size),
            Read: snapshot.Files.Count(f => f.Summary != null),
            Pending: snapshot.Files.Count(f => !f.IsProbed),
            Errors: snapshot.Files.Count(f => f.Error != null),
            Uhd: main.Count(f => f.Summary?.Resolution == "2160p"),
            DolbyVision: main.Count(f => f.Summary?.HasDolbyVision == true),
            SourceMismatches: main.Count(f => f.SourceMismatch),
            Loading: index.IsLoading);
    }
}
