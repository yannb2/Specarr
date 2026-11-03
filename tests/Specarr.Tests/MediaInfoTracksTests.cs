using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public class MediaInfoTracksTests
{
    private static MediaDetails Load(string sample)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", sample));
        return MediaParser.Parse("/movies/Sample (2020)/Sample.mkv", 1, MediaParser.Deserialize(json)!);
    }

    [TestMethod]
    public void Reads_bit_depth_stream_size_and_count_of_elements()
    {
        const string json = """
            {"media":{"track":[
              {"@type":"General","StreamSize":"123"},
              {"@type":"Video","StreamOrder":"0","BitDepth":"10","StreamSize":"60000000000"},
              {"@type":"Audio","StreamOrder":"1","BitDepth":"24","StreamSize":"5000000000"},
              {"@type":"Audio","StreamOrder":"3","StreamSize":"400000000"},
              {"@type":"Text","StreamOrder":"4","StreamSize":"30000000","ElementCount":"1520"}
            ]}}
            """;

        var facts = MediaInfoTracks.Parse(json);

        Assert.HasCount(4, facts);
        Assert.AreEqual(new MediaInfoTrackFacts(1, 24, 5_000_000_000, null), facts[1]);
        Assert.AreEqual(new MediaInfoTrackFacts(4, null, 30_000_000, 1520), facts[3]);
        Assert.IsEmpty(MediaInfoTracks.Parse("not json"));

        var details = Load("uhd-remux-dv.json");
        MediaInfoTracks.Apply(details, facts);
        var truehd = details.Tracks.Single(t => t.Index == 1);
        var pgs = details.Tracks.Single(t => t.Index == 4);
        var ac3 = details.Tracks.Single(t => t.Index == 3);

        Assert.AreEqual(24, truehd.BitDepth);
        StringAssert.Contains(truehd.FormatDetails, "24-bit");
        StringAssert.EndsWith(truehd.FormatDetails, "4.66 GB");
        Assert.IsNull(ac3.BitDepth);
        Assert.AreEqual(1520L, pgs.ElementCount);
        StringAssert.Contains(pgs.FormatDetails, "1,520 elements");
        Assert.AreEqual(10, details.Tracks.Single(t => t.Index == 0).BitDepth, "ffprobe's video bit depth stays");
    }

    [TestMethod]
    public void Mkv_statistics_tags_give_size_and_count_without_mediainfo()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_type":"subtitle","codec_name":"subrip","tags":{"NUMBER_OF_FRAMES-eng":"812","NUMBER_OF_BYTES-eng":"41234"}},
              {"index":1,"codec_type":"audio","codec_name":"flac","channels":2,"sample_rate":"48000","bits_per_raw_sample":"24","tags":{"NUMBER_OF_BYTES":"2000000"}}
            ],"format":{"format_name":"matroska,webm"}}
            """;
        var details = MediaParser.Parse("/movies/a.mkv", 1, MediaParser.Deserialize(json)!);

        var subtitle = details.Tracks.Single(t => t.Index == 0);
        var flac = details.Tracks.Single(t => t.Index == 1);
        Assert.AreEqual(812L, subtitle.ElementCount);
        Assert.AreEqual(41234L, subtitle.StreamSize);
        Assert.AreEqual(24, flac.BitDepth);
        Assert.AreEqual(2_000_000L, flac.StreamSize);
        StringAssert.Contains(subtitle.FormatDetails, "812 elements");
    }
}
