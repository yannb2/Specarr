using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class AudioBitRateTests
{
    private static MediaDetails Load(string sample)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", sample));
        var probe = MediaParser.Deserialize(json)!;
        return MediaParser.Parse("/movies/a.mkv", 1234, probe);
    }

    [TestMethod]
    public void Packets_add_up_to_bits_per_second_per_stream()
    {
        // TrueHD: 2400 packets of 1/1200 s (2 s) at 520 bytes = about 5 Mb/s.
        var lines = new List<string>();
        for (var i = 0; i < 2400; i++)
        {
            lines.Add("stream_index=1|duration_time=0.000833|size=520");
        }

        // AC3: 32 ms frames of 1792 bytes = 448 kb/s.
        for (var i = 0; i < 100; i++)
        {
            lines.Add("stream_index=2|duration_time=0.032000|size=1792");
        }

        // Too short to count, and junk lines.
        lines.Add("stream_index=3|duration_time=0.032000|size=1792");
        lines.Add("garbage");
        lines.Add("stream_index=4|duration_time=N/A|size=100");

        var rates = AudioBitRate.Parse(string.Join('\n', lines));

        Assert.HasCount(2, rates);
        Assert.AreEqual(4_993_998L, rates[1], 1000);
        Assert.AreEqual(448_000L, rates[2]);
    }

    [TestMethod]
    public void Samples_spread_over_the_film_and_short_files_are_read_whole()
    {
        Assert.IsNull(AudioBitRate.SampleStarts(null));
        Assert.IsNull(AudioBitRate.SampleStarts(TimeSpan.FromSeconds(100)));

        var starts = AudioBitRate.SampleStarts(TimeSpan.FromHours(2))!;
        Assert.HasCount(AudioBitRate.SampleCount, starts);
        Assert.AreEqual(360, starts[0]);
        Assert.AreEqual(7200 * 0.95 - AudioBitRate.SampleSeconds, starts[^1], 0.1);

        var arguments = AudioBitRate.Arguments("/movies/a.mkv", [360, 1640.5]);
        CollectionAssert.Contains(arguments, "360%+10,1640.5%+10");
        Assert.AreEqual("/movies/a.mkv", arguments[^1]);
        CollectionAssert.DoesNotContain(AudioBitRate.Arguments("/movies/a.mkv", null), "-read_intervals");
    }

    [TestMethod]
    public void Mediainfo_rates_by_stream_order()
    {
        const string json = """
            {"media":{"track":[
              {"@type":"General","OverallBitRate":"65000000"},
              {"@type":"Video","StreamOrder":"0","BitRate":"58123456"},
              {"@type":"Audio","StreamOrder":"1","BitRate_Mode":"VBR","BitRate_Maximum":"9600000"},
              {"@type":"Audio","StreamOrder":"2","BitRate":"640000"},
              {"@type":"Audio","StreamOrder":"3","BitRate":"Unknown"}
            ]}}
            """;

        var rates = AudioBitRate.ParseMediaInfo(json);

        Assert.HasCount(4, rates);
        Assert.AreEqual(new MediaInfoBitRate(1, null, 9_600_000), rates[1]);
        Assert.AreEqual(new MediaInfoBitRate(2, 640_000, null), rates[2]);
        Assert.AreEqual(new MediaInfoBitRate(3, null, null), rates[3]);
        Assert.IsEmpty(AudioBitRate.ParseMediaInfo("not json"));
        Assert.IsEmpty(AudioBitRate.ParseMediaInfo(null));
    }

    [TestMethod]
    public void Stored_rates_win_then_mediainfo_then_the_measure()
    {
        var details = Load("uhd-remux-dv.json");
        var audio = details.Audio.ToList();
        var stored = audio.Where(a => a.BitRate != null).Select(a => a.Index).ToHashSet();
        var missing = audio.Where(a => a.BitRate == null).Select(a => a.Index).ToList();
        Assert.IsNotEmpty(missing, "The sample needs a track without a bit rate");

        var first = audio[0];
        var mediaInfo = new List<MediaInfoBitRate> { new(first.Index, 1, 9_600_000) };
        var measured = audio.ToDictionary(a => a.Index, _ => 3_000_000L);

        Assert.IsTrue(AudioBitRate.NeedsMeasuring(details, mediaInfo));
        AudioBitRate.Apply(details, mediaInfo, measured);

        Assert.AreEqual(4_500_000L, first.BitRate, "The statistics tag is kept");
        Assert.AreEqual(9_600_000L, first.MaxBitRate);
        Assert.AreEqual("4.5 Mb/s", first.BitRateText);
        foreach (var track in audio.Where(a => missing.Contains(a.Index)))
        {
            Assert.AreEqual(3_000_000L, track.BitRate);
            Assert.IsTrue(track.BitRateIsEstimate);
            Assert.AreEqual("≈ 3.0 Mb/s", track.BitRateText);
            StringAssert.EndsWith(track.FormatSummary, "≈ 3.0 Mb/s");
        }

        foreach (var track in audio.Where(a => stored.Contains(a.Index)))
        {
            Assert.IsFalse(track.BitRateIsEstimate);
        }

        Assert.IsFalse(AudioBitRate.NeedsMeasuring(details, mediaInfo));
    }

    [TestMethod]
    public void Mediainfo_average_is_not_an_estimate_and_a_peak_alone_says_up_to()
    {
        var details = Load("uhd-remux-dv.json");
        var missing = details.Audio.Where(a => a.BitRate == null).ToList();
        Assert.IsNotEmpty(missing);

        var known = missing[0];
        var mediaInfo = missing.Select(a => a == known
            ? new MediaInfoBitRate(a.Index, 2_000_000, null)
            : new MediaInfoBitRate(a.Index, null, 9_600_000)).ToList();

        Assert.AreEqual(missing.Count > 1, AudioBitRate.NeedsMeasuring(details, mediaInfo));
        AudioBitRate.Apply(details, mediaInfo, null);

        Assert.AreEqual(2_000_000L, known.BitRate);
        Assert.IsFalse(known.BitRateIsEstimate);
        foreach (var peak in missing.Skip(1))
        {
            Assert.IsNull(peak.BitRate);
            Assert.AreEqual("up to 9.6 Mb/s", peak.BitRateText);
        }
    }
}
