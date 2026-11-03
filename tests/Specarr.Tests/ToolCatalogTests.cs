using Specarr.Tools;

namespace Specarr.Tests;

[TestClass]
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string id) => ToolCatalog.All.Single(t => t.Id == id);

    [TestMethod]
    public void ReadsInstalledVersions()
    {
        Assert.AreEqual("26.10", ToolCatalog.InstalledVersion(Tool("mediainfo"), "MediaInfo Command line,\nMediaInfoLib - v26.10"));
        Assert.AreEqual("2.3.4", ToolCatalog.InstalledVersion(Tool("dovi_tool"), "dovi_tool 2.3.4"));
        Assert.AreEqual("9.0.2", ToolCatalog.InstalledVersion(Tool("ffprobe"), "ffprobe version n9.0.2-3-g1234abcd-20261103 Copyright"));
        Assert.AreEqual("6.1.1", ToolCatalog.InstalledVersion(Tool("ffprobe"), "ffprobe version 6.1.1-3ubuntu5 Copyright"));
    }

    [TestMethod]
    public void FindsTheNewestBtbNReleaseBranch()
    {
        const string json = """
            {"tag_name":"latest","assets":[
              {"name":"ffmpeg-master-latest-linux64-gpl.tar.xz"},
              {"name":"ffmpeg-n8.1-latest-linux64-gpl-8.1.tar.xz"},
              {"name":"ffmpeg-n9.0-latest-linux64-gpl-shared-9.0.tar.xz"},
              {"name":"ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz"},
              {"name":"ffmpeg-n10.0-latest-win64-gpl-10.0.zip"}]}
            """;
        Assert.AreEqual("9.0", ToolCatalog.Highest(Tool("ffprobe").UpstreamPattern!, ToolCatalog.GitHubAssetNames(json)));
        Assert.AreEqual("", ToolCatalog.GitHubAssetNames("not json"));
    }

    [TestMethod]
    public void SkipsReleaseTagsOfAnotherKind()
    {
        var pattern = Tool("dovi_tool").UpstreamPattern!;
        Assert.AreEqual("2.3.5", ToolCatalog.Highest(pattern, ToolCatalog.GitHubTag("{\"tag_name\":\"2.3.5\"}")!));
        Assert.IsNull(ToolCatalog.Highest(pattern, ToolCatalog.GitHubTag("{\"tag_name\":\"libdovi-3.4.0\"}")!));
        Assert.AreEqual("26.10", ToolCatalog.GitHubTag("{\"tag_name\":\"v26.10\"}"));
    }

    [TestMethod]
    public void ComparesVersionsByNumber()
    {
        Assert.IsGreaterThan(0, VersionComparer.Instance.Compare("2.3.10", "2.3.4"));
        Assert.IsGreaterThan(0, VersionComparer.Instance.Compare("26.10", "26.05"));
        Assert.AreEqual(0, VersionComparer.Instance.Compare("103.0", "103"));
    }

    [TestMethod]
    public void UpdateAvailableComparesWithWhatIsInstalled()
    {
        var status = new ToolStatus { Tool = Tool("dovi_tool"), Installed = "2.3.4", Latest = "2.3.5" };
        Assert.IsTrue(status.UpdateAvailable);
        Assert.IsFalse(status with { Latest = "2.3.4" } is { UpdateAvailable: true });

        // The BtbN branch build reports 9.0.2 while the newest branch is 9.0: nothing to do.
        var ffprobe = new ToolStatus { Tool = Tool("ffprobe"), Installed = "9.0.2", Latest = "9.0" };
        Assert.IsFalse(ffprobe.UpdateAvailable);
        Assert.IsTrue(ffprobe with { Installed = "8.1.3" } is { UpdateAvailable: true });
    }
}
