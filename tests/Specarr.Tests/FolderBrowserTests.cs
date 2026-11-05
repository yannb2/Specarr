using Specarr.Library;
using Specarr.Options;

namespace Specarr.Tests;

[TestClass]
public sealed class FolderBrowserTests
{
    private static readonly string[] Videos = SpecarrOptions.DefaultVideoExtensions;

    [TestMethod]
    public void ResolveStaysInsideTheRoot()
    {
        Assert.AreEqual("/downloads", FolderBrowser.Resolve("/downloads/", null));
        Assert.AreEqual("/downloads", FolderBrowser.Resolve("/downloads", "/"));
        Assert.AreEqual("/downloads/torrents/radarr", FolderBrowser.Resolve("/downloads", "torrents/radarr/"));
        Assert.AreEqual("/downloads/etc/passwd", FolderBrowser.Resolve("/downloads", "/etc/passwd"));
        Assert.IsNull(FolderBrowser.Resolve("/downloads", ".."));
        Assert.IsNull(FolderBrowser.Resolve("/downloads", "torrents/../../etc"));
        Assert.IsNull(FolderBrowser.Resolve("/downloads", "torrents\\..\\..\\etc"));
        Assert.IsNull(FolderBrowser.Resolve("/downloads", "./x"));
    }

    [TestMethod]
    public void KindsByExtension()
    {
        Assert.AreEqual(EntryKind.Video, FolderBrowser.KindOf("Film.mkv", Videos));
        Assert.AreEqual(EntryKind.Video, FolderBrowser.KindOf("00800.M2TS", Videos));
        Assert.AreEqual(EntryKind.DiscImage, FolderBrowser.KindOf("Film.iso", Videos), "ISOs are videos in the library list, but ffprobe can't read them");
        Assert.AreEqual(EntryKind.Audio, FolderBrowser.KindOf("01 - Track.flac", Videos));
        Assert.AreEqual(EntryKind.Subtitle, FolderBrowser.KindOf("Film.en.srt", Videos));
        Assert.AreEqual(EntryKind.Archive, FolderBrowser.KindOf("film.part01.rar", Videos));
        Assert.AreEqual(EntryKind.Archive, FolderBrowser.KindOf("film.r07", Videos));
        Assert.AreEqual(EntryKind.Archive, FolderBrowser.KindOf("film.7z.002", Videos));
        Assert.AreEqual(EntryKind.Other, FolderBrowser.KindOf("release.nfo", Videos));
    }

    [TestMethod]
    public void ListsOneLevelWithKindsAndCounts()
    {
        var root = Directory.CreateTempSubdirectory("specarr-browse").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "torrents", "radarr", "Film"));
            Directory.CreateDirectory(Path.Combine(root, ".recycle"));
            File.WriteAllText(Path.Combine(root, "torrents", "radarr", "Film", "Film.mkv"), "x");
            File.WriteAllText(Path.Combine(root, "torrents", "radarr", "Film", "Film.nfo"), "nfo");

            var top = FolderBrowser.List(root, "", Videos);
            Assert.IsNull(top.Error);
            Assert.AreEqual("", top.RelativePath);
            CollectionAssert.AreEquivalent(new[] { ".recycle", "torrents" }, top.Entries.Select(e => e.Name).ToArray());
            Assert.IsTrue(top.Entries.Single(e => e.Name == ".recycle").IsHidden);
            Assert.AreEqual(1, top.Entries.Single(e => e.Name == "torrents").ItemCount);

            var film = FolderBrowser.List(root, "torrents/radarr/Film", Videos);
            var mkv = film.Entries.Single(e => e.Name == "Film.mkv");
            Assert.AreEqual("torrents/radarr/Film/Film.mkv", mkv.RelativePath);
            Assert.AreEqual(1L, mkv.SizeBytes);
            Assert.IsTrue(mkv.CanOpen);
            Assert.IsFalse(film.Entries.Single(e => e.Name == "Film.nfo").CanOpen);

            Assert.IsNotNull(FolderBrowser.List(root, "../", Videos).Error);
            Assert.IsNotNull(FolderBrowser.List(root, "nowhere", Videos).Error);
            Assert.IsNotNull(FolderBrowser.List(root, "torrents/radarr/Film/Film.mkv", Videos).Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DownloadsIsBrowsedByDefault()
    {
        var options = new SpecarrOptions().WithDefaults();
        var downloads = options.Browse.Single();
        Assert.AreEqual("/downloads", downloads.Path);
        Assert.AreEqual("downloads", downloads.Slug);
        Assert.AreSame(downloads, FolderBrowser.Find(options, "Downloads"));
        Assert.IsNull(FolderBrowser.Find(options, "movies"));
        Assert.AreEqual("usb-disks", new BrowseFolderOptions { Name = "USB disks" }.Slug);
    }

    [TestMethod]
    public void OnlyLibraryRowsAreForgottenByTheWalk()
    {
        var options = new SpecarrOptions().WithDefaults();
        Assert.IsTrue(LibraryIndex.IsInLibrary(options, "/movies/anime/Akira (1988)/Akira.mkv"));
        Assert.IsFalse(LibraryIndex.IsInLibrary(options, "/downloads/torrents/radarr/Akira.mkv"));
        Assert.IsFalse(LibraryIndex.IsInLibrary(options, "/movies-old/Akira.mkv"));
    }
}
