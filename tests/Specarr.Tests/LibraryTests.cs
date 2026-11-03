using Specarr.Library;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Tests;

[TestClass]
public sealed class LibraryTests
{
    private static readonly LibraryOptions Movies = new() { Name = "Movies", Path = "/movies", Kind = LibraryKind.Movies };
    private static readonly LibraryOptions Tv = new() { Name = "TV", Path = "/tv", Kind = LibraryKind.Series };
    private static readonly string[] Extras = SpecarrOptions.DefaultExtrasFolderNames;

    [TestMethod]
    public void Movie_in_a_category()
    {
        var place = LibraryLayout.Parse(Movies, "/movies/anime/Akira (1988)/Akira (1988) [2160p][HEVC][BDRemux].mkv", Extras);

        Assert.AreEqual("anime", place.Category);
        Assert.AreEqual("/movies/anime/Akira (1988)", place.TitleFolder);
        Assert.AreEqual("Akira", place.TitleName);
        Assert.AreEqual(1988, place.Year);
        Assert.IsFalse(place.IsExtra);
        Assert.IsNull(place.Episode);
    }

    [TestMethod]
    public void Other_category_is_not_an_extras_folder()
    {
        // "other" is a category at the first level, and Jellyfin's extras folder name inside a title.
        var movie = LibraryLayout.Parse(Movies, "/movies/other/Film (2001)/Film (2001).mkv", Extras);
        var extra = LibraryLayout.Parse(Movies, "/movies/live-action/Film (2001)/other/Making of.mkv", Extras);

        Assert.AreEqual("other", movie.Category);
        Assert.IsFalse(movie.IsExtra);
        Assert.IsTrue(extra.IsExtra);
        Assert.AreEqual("/movies/live-action/Film (2001)", extra.TitleFolder);
    }

    [TestMethod]
    public void Extras_by_folder_and_suffix()
    {
        Assert.IsTrue(LibraryLayout.Parse(Movies, "/movies/live-action/Alien (1979)/Featurettes/The Beast Within.mkv", Extras).IsExtra);
        Assert.IsTrue(LibraryLayout.Parse(Movies, "/movies/live-action/Alien (1979)/Alien (1979)-trailer.mkv", Extras).IsExtra);
        Assert.IsFalse(LibraryLayout.Parse(Movies, "/movies/live-action/Alien (1979)/Alien (1979) - Director's Cut [1080p].mkv", Extras).IsExtra);
    }

    [TestMethod]
    public void Episode_in_a_season_folder()
    {
        var place = LibraryLayout.Parse(Tv, "/tv/live-action/The Expanse (2015)/Season 02/The Expanse (2015) S02E05 [1080p][WEB-DL] - Home.mkv", Extras);

        Assert.AreEqual("live-action", place.Category);
        Assert.AreEqual("The Expanse", place.TitleName);
        Assert.AreEqual(2015, place.Year);
        Assert.AreEqual(2, place.Season);
        Assert.AreEqual(5, place.Episode);
        Assert.AreEqual("S02E05", place.EpisodeLabel);
    }

    [TestMethod]
    public void Multi_episode_and_specials()
    {
        var multi = LibraryLayout.Parse(Tv, "/tv/anime/One Piece (1999)/Season 01/One Piece (1999) S01E01-E02 (001) [1080p].mkv", Extras);
        var special = LibraryLayout.Parse(Tv, "/tv/anime/One Piece (1999)/Specials/One Piece (1999) S00E03.mkv", Extras);

        Assert.AreEqual("S01E01-E02", multi.EpisodeLabel);
        Assert.AreEqual(0, special.Season);
    }

    [TestMethod]
    public void Library_without_categories()
    {
        var flat = new LibraryOptions { Name = "Movies", Path = "/movies", Kind = LibraryKind.Movies, Categories = false };
        var place = LibraryLayout.Parse(flat, "/movies/Heat (1995)/Heat (1995).mkv", Extras);

        Assert.IsNull(place.Category);
        Assert.AreEqual("Heat", place.TitleName);
    }

    [TestMethod]
    public void Loose_file_in_a_category()
    {
        var place = LibraryLayout.Parse(Movies, "/movies/live-action/Heat (1995).mkv", Extras);

        Assert.IsNull(place.Category);
        Assert.AreEqual("/movies/live-action", place.TitleFolder);

        // Loose in the root: the file name stands for the title.
        var root = LibraryLayout.Parse(Movies, "/movies/Heat (1995).mkv", Extras);
        Assert.IsNull(root.TitleFolder);
        Assert.AreEqual("Heat", root.TitleName);
    }

    [TestMethod]
    public void Walk_skips_dot_folders_and_lists_videos()
    {
        var root = Directory.CreateTempSubdirectory("specarr-walk").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "live-action", "Heat (1995)"));
            Directory.CreateDirectory(Path.Combine(root, ".recycle", "Old (2000)"));
            Directory.CreateDirectory(Path.Combine(root, "@eaDir"));
            File.WriteAllText(Path.Combine(root, "live-action", "Heat (1995)", "Heat (1995).mkv"), "x");
            File.WriteAllText(Path.Combine(root, "live-action", "Heat (1995)", "movie.nfo"), "x");
            File.WriteAllText(Path.Combine(root, ".recycle", "Old (2000)", "Old (2000).mkv"), "x");
            File.WriteAllText(Path.Combine(root, "@eaDir", "thumb.mkv"), "x");

            var options = new SpecarrOptions { Libraries = [new LibraryOptions { Name = "Movies", Path = root }] }.WithDefaults();
            var walk = LibraryScanner.Walk(options);

            Assert.HasCount(1, walk.Files);
            Assert.EndsWith("Heat (1995).mkv", walk.Files[0].Path);
            Assert.HasCount(0, walk.Problems);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Missing_library_is_a_problem()
    {
        var options = new SpecarrOptions { Libraries = [new LibraryOptions { Name = "Gone", Path = "/does/not/exist" }] }.WithDefaults();

        Assert.HasCount(1, LibraryScanner.Walk(options).Problems);
    }

    [TestMethod]
    public void Snapshot_groups_files_by_title()
    {
        IndexedFile File(string path) => new(path, Movies, LibraryLayout.Parse(Movies, path, Extras), 10, DateTime.UtcNow);
        var files = new List<IndexedFile>
        {
            File("/movies/live-action/Alien (1979)/Alien (1979) [2160p].mkv"),
            File("/movies/live-action/Alien (1979)/Alien (1979) - Director's Cut [1080p].mkv"),
            File("/movies/live-action/Alien (1979)/Featurettes/Making of.mkv"),
            File("/movies/anime/Akira (1988)/Akira (1988).mkv")
        };

        var snapshot = LibraryIndex.BuildSnapshot(files, new WalkResult([], [], TimeSpan.Zero));

        Assert.HasCount(2, snapshot.Titles);
        var alien = snapshot.Titles.Single(t => t.Name == "Alien");
        Assert.HasCount(3, alien.Files);
        Assert.AreEqual(2, alien.MainFiles.Count());
        Assert.AreSame(alien, snapshot.TitleOf(files[2]));
        Assert.AreSame(files[3], snapshot.FilesById[files[3].Id]);
    }

    [TestMethod]
    public void Mismatch_needs_a_confident_guess()
    {
        var file = new IndexedFile("/movies/live-action/Film (2001)/Film (2001) [1080p][WEB-DL].mkv", Movies,
            LibraryLayout.Parse(Movies, "/movies/live-action/Film (2001)/Film (2001) [1080p][WEB-DL].mkv", Extras), 10, DateTime.UtcNow);

        file.SetResult(new FileSummary { Source = SourceKind.BDRemux, SourceConfidence = Confidence.Low }, null);
        Assert.IsFalse(file.SourceMismatch);

        file.SetResult(new FileSummary { Source = SourceKind.BDRemux, SourceConfidence = Confidence.High }, null);
        Assert.IsTrue(file.SourceMismatch);

        file.SetResult(new FileSummary { Source = SourceKind.WebDl, SourceConfidence = Confidence.High }, null);
        Assert.IsFalse(file.SourceMismatch);
    }
}
