using Specarr.Library;
using Specarr.Options;

namespace Specarr.Tests;

[TestClass]
public sealed class OpenTargetTests
{
    [TestMethod]
    public void LibraryFilesAndTitlesOpenTheirPages()
    {
        var options = new SpecarrOptions().WithDefaults();
        var movies = options.Libraries[0];
        const string path = "/movies/anime/Akira (1988)/Akira (1988).mkv";
        var file = new IndexedFile(path, movies, LibraryLayout.Parse(movies, path, options.ExtrasFolderNames), 1, DateTime.UtcNow);
        var title = new TitleEntry
        {
            Id = "t1", Library = movies, Category = "anime", Folder = "/movies/anime/Akira (1988)", Name = "Akira", Year = 1988, Files = [file]
        };
        var snapshot = new IndexSnapshot
        {
            FilesById = new Dictionary<string, IndexedFile> { [file.Id] = file },
            TitlesByFolder = new Dictionary<string, TitleEntry> { [title.Folder!] = title }
        };

        Assert.AreEqual($"file/{file.Id}", OpenTarget.Resolve(snapshot, false, options, path).Link);
        Assert.AreEqual("title/t1", OpenTarget.Resolve(snapshot, false, options, "/movies/anime/Akira (1988)/").Link);
        Assert.IsTrue(OpenTarget.Resolve(IndexSnapshot.Empty, true, options, path).WaitForIndex);
        Assert.IsNotNull(OpenTarget.Resolve(IndexSnapshot.Empty, false, options, path).Problem);
    }

    [TestMethod]
    public void BrowsedFilesAndFoldersOpenTheBrowser()
    {
        var root = Directory.CreateTempSubdirectory("specarr-open").FullName;
        try
        {
            var usb = Path.Combine(root, "pc");
            Directory.CreateDirectory(Path.Combine(usb, "usb01", "Films"));
            File.WriteAllText(Path.Combine(usb, "usb01", "Films", "A #1.mkv"), "x");
            var options = new SpecarrOptions
            {
                Browse = [new BrowseFolderOptions { Name = "Downloads", Path = root }, new BrowseFolderOptions { Name = "External", Path = usb }]
            }.WithDefaults();

            // The deepest root wins: /mnt/pc inside /downloads would open under External.
            Assert.AreEqual("browse/external/file?path=usb01%2FFilms%2FA%20%231.mkv",
                OpenTarget.Resolve(IndexSnapshot.Empty, false, options, Path.Combine(usb, "usb01", "Films", "A #1.mkv")).Link);
            Assert.AreEqual("browse/external?path=usb01", OpenTarget.Resolve(IndexSnapshot.Empty, false, options, Path.Combine(usb, "usb01")).Link);
            Assert.AreEqual("browse/downloads", OpenTarget.Resolve(IndexSnapshot.Empty, false, options, root + "/").Link);
            Assert.IsNotNull(OpenTarget.Resolve(IndexSnapshot.Empty, false, options, Path.Combine(root, "gone.mkv")).Problem);
            Assert.IsNotNull(OpenTarget.Resolve(IndexSnapshot.Empty, false, options, "/etc/passwd").Problem);
            Assert.IsNotNull(OpenTarget.Resolve(IndexSnapshot.Empty, false, options, root + "/../etc").Problem);
            Assert.IsNotNull(OpenTarget.Resolve(IndexSnapshot.Empty, false, options, "relative.mkv").Problem);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
