using Specarr.Compare;
using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class ComparisonTests
{
    private static MediaDetails Load(string sample, string path)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", sample));
        return MediaParser.Parse(path, 1000, MediaParser.Deserialize(json)!);
    }

    [TestMethod]
    public void Same_file_has_no_differences()
    {
        var a = Load("uhd-remux-dv.json", "/a/x.mkv");
        var b = Load("uhd-remux-dv.json", "/b/x.mkv");

        var sections = Comparison.Build(a, b);

        Assert.AreEqual(0, sections.Sum(s => s.DifferenceCount));
        CollectionAssert.AreEqual(
            new[] { "General", "Video", "Audio", "Subtitles", "Tags", "Chapters" },
            sections.Select(s => s.Title).ToArray());
    }

    [TestMethod]
    public void Different_files_show_where_they_differ()
    {
        var recycled = Load("hevc-hdr10-eac3.json", "/bin/old.mkv");
        var library = Load("uhd-remux-dv.json", "/lib/new.mkv");

        var sections = Comparison.Build(recycled, library).ToDictionary(s => s.Title);

        var video = sections["Video"].Rows.ToDictionary(r => r.Label);
        Assert.AreEqual("HDR10", video["Dynamic range"].Left);
        Assert.AreEqual("DV 7 (EL) HDR10", video["Dynamic range"].Right);
        Assert.IsTrue(video["Dynamic range"].Differs);
        Assert.IsFalse(video["Codec"].Differs);

        // Two audio tracks against three: the third has nothing on the left.
        var audio = sections["Audio"].Rows;
        Assert.HasCount(3, audio);
        Assert.IsNull(audio[2].LeftTrack);
        Assert.IsTrue(audio.All(r => r.Differs));

        var tags = sections["Tags"].Rows.ToDictionary(r => r.Label);
        Assert.AreEqual("BDRemux", tags["SOURCE"].Left);
        Assert.IsNull(tags["SOURCE"].Right);
        Assert.IsFalse(tags["TMDB"].Differs);

        // The left has no chapters, the right has two.
        Assert.HasCount(2, sections["Chapters"].Rows);
    }

    [TestMethod]
    public void Rows_empty_on_both_sides_are_left_out()
    {
        var a = Load("hevc-hdr10-eac3.json", "/a/x.mkv");
        var b = Load("hevc-hdr10-eac3.json", "/b/x.mkv");

        var video = Comparison.Build(a, b).Single(s => s.Title == "Video").Rows.Select(r => r.Label).ToList();

        CollectionAssert.DoesNotContain(video, "Bit rate", "ffmpeg wrote no bit rate in either file");
        CollectionAssert.DoesNotContain(video, "Track name");
    }
}
