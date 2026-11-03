using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class SourceGuesserTests
{
    private static MediaDetails Load(string sample, string? path = null)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", sample));
        var probe = MediaParser.Deserialize(json)!;
        return MediaParser.Parse(path ?? "/movies/live-action/Film (2020)/Film (2020).mkv", 1234, probe);
    }

    /// <summary>A made-up file: one video track, the given audio and subtitle codecs.</summary>
    private static MediaDetails Make(string videoCodec, int width, int height, double videoMbps,
        string[] audio, string[] subtitles, int chapters = 12, string extension = ".mkv", int? dvProfile = null,
        string? audioProfile = null, string? fieldOrder = null, string[]? audioLanguages = null)
    {
        var streams = new List<FFprobeStream>
        {
            new()
            {
                Index = 0, CodecType = "video", CodecName = videoCodec, Width = width, Height = height,
                PixelFormat = "yuv420p10le", AverageFrameRate = "24000/1001", FieldOrder = fieldOrder,
                Tags = new Dictionary<string, string> { ["BPS"] = ((long)(videoMbps * 1_000_000)).ToString() },
                SideDataList = dvProfile == null
                    ? null
                    : [new FFprobeSideData { SideDataType = FFprobeSideData.DoviConfigurationRecord, DvProfile = dvProfile, ElPresentFlag = dvProfile == 7 ? 1 : 0 }]
            }
        };
        for (var i = 0; i < audio.Length; i++)
        {
            streams.Add(new FFprobeStream
            {
                Index = streams.Count, CodecType = "audio", CodecName = audio[i], Channels = 6, Profile = i == 0 ? audioProfile : null,
                Tags = new Dictionary<string, string> { ["language"] = audioLanguages?[i] ?? "eng" }
            });
        }

        foreach (var subtitle in subtitles)
        {
            streams.Add(new FFprobeStream { Index = streams.Count, CodecType = "subtitle", CodecName = subtitle, Tags = new() { ["language"] = "eng" } });
        }

        var probe = new FFprobeResult
        {
            Streams = streams,
            Chapters = Enumerable.Range(0, chapters).Select(i => new FFprobeChapter { StartTime = (i * 600).ToString() }).ToList(),
            Format = new FFprobeFormat { FormatName = "matroska,webm", Duration = "7200" }
        };
        return MediaParser.Parse($"/movies/live-action/Film (2020)/Film (2020){extension}", 1234, probe);
    }

    [TestMethod]
    public void Uhd_remux_with_dv_profile_7_and_truehd()
    {
        var guess = SourceGuesser.Guess(Load("uhd-remux-dv.json"));

        Assert.AreEqual(SourceKind.BDRemux, guess.Kind);
        Assert.AreEqual(Confidence.High, guess.Confidence);
        Assert.IsTrue(guess.Clues.Any(c => c.Text.Contains("profile 7")));
        Assert.IsTrue(guess.Clues.Any(c => c.Text.Contains("Lossless")));
    }

    [TestMethod]
    public void Blu_ray_remux_1080p()
    {
        var details = Make("h264", 1920, 1080, 30, ["truehd", "ac3"], ["hdmv_pgs_subtitle"]);
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.BDRemux, guess.Kind);
        Assert.AreNotEqual(Confidence.Low, guess.Confidence);
    }

    [TestMethod]
    public void Vc1_is_a_disc()
    {
        var guess = SourceGuesser.Guess(Make("vc1", 1920, 1080, 22, ["dts"], ["hdmv_pgs_subtitle"], audioProfile: "DTS-HD MA"));

        Assert.AreEqual(SourceKind.BDRemux, guess.Kind);
    }

    [TestMethod]
    public void Web_dl_with_eac3_and_dv_profile_5()
    {
        var details = Make("hevc", 3840, 2160, 16, ["eac3"], ["subrip"], chapters: 0, dvProfile: 5);
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.WebDl, guess.Kind);
        Assert.AreEqual(Confidence.High, guess.Confidence);
    }

    [TestMethod]
    public void Web_dl_1080p_avc()
    {
        var guess = SourceGuesser.Guess(Make("h264", 1920, 1080, 7, ["eac3"], ["subrip"], chapters: 0));

        Assert.AreEqual(SourceKind.WebDl, guess.Kind);
    }

    [TestMethod]
    public void X265_encode_of_a_blu_ray()
    {
        var details = Make("hevc", 1920, 1080, 9, ["dts", "ac3"], ["hdmv_pgs_subtitle"]);
        details.EncoderLibrary = "x265";
        details.WritingApplication = "mkvmerge v92.0";
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.BDRip, guess.Kind);
        Assert.IsTrue(guess.Clues.Any(c => c.Text.Contains("x265")));
    }

    [TestMethod]
    public void Encoded_web_stream_is_a_webrip()
    {
        var details = Make("h264", 1920, 1080, 4, ["aac"], ["subrip"], chapters: 0);
        details.EncoderLibrary = "x264";
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.WebRip, guess.Kind);
    }

    [TestMethod]
    public void Dvd_remux_is_mpeg2_sd()
    {
        var details = Make("mpeg2video", 720, 480, 6.5, ["ac3"], ["dvd_subtitle"], fieldOrder: "tt");
        details.WritingApplication = "MakeMKV v1.18.1 linux(x64-release)";
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.DVDRemux, guess.Kind);
        Assert.AreEqual(Confidence.High, guess.Confidence);
    }

    [TestMethod]
    public void Dvd_rip_is_avc_at_dvd_size()
    {
        var details = Make("h264", 720, 576, 1.8, ["ac3"], ["dvd_subtitle"]);
        details.EncoderLibrary = "x264";
        var guess = SourceGuesser.Guess(details);

        Assert.AreEqual(SourceKind.DVDRip, guess.Kind);
    }

    [TestMethod]
    public void Interlaced_mpeg2_hd_ts_is_hdtv()
    {
        var guess = SourceGuesser.Guess(Make("mpeg2video", 1920, 1080, 15, ["ac3"], ["dvb_subtitle"], chapters: 0, extension: ".ts", fieldOrder: "tt"));

        Assert.AreEqual(SourceKind.Hdtv, guess.Kind);
    }

    [TestMethod]
    public void Hevc_at_1080p_is_never_a_remux()
    {
        // Even with lossless audio and PGS: a 1080p Blu-ray is never HEVC.
        var guess = SourceGuesser.Guess(Make("hevc", 1920, 1080, 12, ["truehd"], ["hdmv_pgs_subtitle"]));

        Assert.AreNotEqual(SourceKind.BDRemux, guess.Kind);
    }

    [TestMethod]
    public void Without_video_nothing_is_guessed()
    {
        var probe = new FFprobeResult { Streams = [new FFprobeStream { CodecType = "audio", CodecName = "flac" }] };
        var guess = SourceGuesser.Guess(MediaParser.Parse("/x.mkv", 1, probe));

        Assert.AreEqual(SourceKind.Unknown, guess.Kind);
    }

    [TestMethod]
    [DataRow("Alien (1979) [2160p][HEVC][DV HDR10][TrueHD Atmos-7.1][BDRemux].mkv", SourceKind.BDRemux)]
    [DataRow("Alien (1979) [1080p][AVC][DTS-HD MA-5.1][BDRip].mkv", SourceKind.BDRip)]
    [DataRow("Show S01E02 [1080p][AVC][EAC3-5.1][WEB-DL] - Pilot.mkv", SourceKind.WebDl)]
    [DataRow("Show.S01E02.1080p.WEBRip.x264-GRP.mkv", SourceKind.WebRip)]
    [DataRow("Film.2001.1080p.BluRay.x264-GRP.mkv", SourceKind.BDRip)]
    [DataRow("Film.2001.UHD.BluRay.2160p.REMUX.HEVC-GRP.mkv", SourceKind.BDRemux)]
    [DataRow("Film (2001) [480p][MPEG2][AC3-5.1][DVDRemux].mkv", SourceKind.DVDRemux)]
    [DataRow("Film (2001) [576p][AVC][AC3-2.0][DVDRip].mkv", SourceKind.DVDRip)]
    [DataRow("Show.S01E01.720p.HDTV.x264-GRP.mkv", SourceKind.Hdtv)]
    [DataRow("Show.S01E01.1080p.AMZN.WEB-DL.DDP5.1.H.264-GRP.mkv", SourceKind.WebDl)]
    public void Source_word_in_the_file_name(string fileName, SourceKind expected)
    {
        Assert.AreEqual(expected, SourceGuesser.Named(fileName, null)?.Kind);
    }

    [TestMethod]
    public void Source_tag_wins_over_the_file_name()
    {
        var named = SourceGuesser.Named("Film (2001) [1080p][BDRip].mkv", "BDRemux");

        Assert.AreEqual(SourceKind.BDRemux, named?.Kind);
        Assert.AreEqual("SOURCE tag", named?.From);
    }

    [TestMethod]
    public void No_source_word()
    {
        Assert.IsNull(SourceGuesser.Named("Webster (1983).mkv", null));
        Assert.IsNull(SourceGuesser.Named("Film (2001) [1080p][AVC].mkv", null));
    }
}
