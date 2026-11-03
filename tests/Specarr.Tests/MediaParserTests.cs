using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class MediaParserTests
{
    private static MediaDetails Load(string sample)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", sample));
        var probe = MediaParser.Deserialize(json)!;
        return MediaParser.Parse(probe.Format!.Tags!.GetValueOrDefault("title") ?? sample, 1234, probe);
    }

    [TestMethod]
    public void Remux_tracks_codecs_and_flags()
    {
        var details = Load("uhd-remux-dv.json");

        Assert.AreEqual("Matroska", details.Container);
        Assert.AreEqual("Alien (1979)", details.Title);
        Assert.AreEqual(TimeSpan.FromSeconds(7920), details.Duration);

        // Cover art and the font are attachments, not tracks.
        Assert.HasCount(6, details.Tracks);
        Assert.AreEqual(2, details.Attachments);

        var video = details.Video.Single();
        Assert.AreEqual("HEVC", video.Codec);
        Assert.AreEqual("2160p", video.Resolution);
        Assert.AreEqual(10, video.BitDepth);
        Assert.AreEqual("7", video.DolbyVisionProfile);
        Assert.IsTrue(video.HasEnhancementLayer);
        Assert.AreEqual("DV 7 (EL) HDR10", video.DynamicRange);
        Assert.AreEqual(23.976, video.FrameRate);
        Assert.AreEqual(62_000_000L, video.BitRate);

        var audio = details.Audio.ToList();
        Assert.AreEqual("TrueHD Atmos", audio[0].Codec);
        Assert.AreEqual("7.1", audio[0].ChannelLayout);
        Assert.AreEqual("English", audio[0].LanguageName);
        Assert.AreEqual("English - TrueHD Atmos 7.1", audio[0].Title);
        Assert.AreEqual(4_500_000L, audio[0].BitRate, "BPS-eng statistics tag");
        CollectionAssert.AreEqual(new[] { "Default", "Original" }, audio[0].Flags.ToArray());

        Assert.AreEqual("DTS-X", audio[1].Codec);
        Assert.AreEqual("French", audio[1].LanguageName, "fre is the bibliographic code for French");
        Assert.IsTrue(audio[1].IsDub);

        Assert.AreEqual("AC3", audio[2].Codec);
        Assert.AreEqual("2.0", audio[2].ChannelLayout);
        Assert.IsTrue(audio[2].IsCommentary);
        Assert.AreEqual(224_000L, audio[2].BitRate);

        var subtitles = details.Subtitles.ToList();
        Assert.AreEqual("PGS", subtitles[0].Codec);
        Assert.IsTrue(subtitles[0].IsForced);
        Assert.AreEqual("SRT", subtitles[1].Codec);
        Assert.IsTrue(subtitles[1].IsHearingImpaired);
    }

    [TestMethod]
    public void Global_tags_hide_muxer_noise_and_follow_tag_release_order()
    {
        var details = Load("uhd-remux-dv.json");

        CollectionAssert.AreEqual(
            new[] { "TMDB", "IMDB", "RELEASE_NAME", "DV_PROFILE", "EDITION", "IMPORTED", "CUSTOM_THING" },
            details.Tags.Select(t => t.Key).ToArray());
        Assert.AreEqual("movie/348", details.GetTag("tmdb"));
    }

    [TestMethod]
    public void Chapters_have_start_and_title()
    {
        var details = Load("uhd-remux-dv.json");

        Assert.HasCount(2, details.Chapters);
        Assert.AreEqual(new ChapterInfo(TimeSpan.FromMinutes(5), "Chapter 2"), details.Chapters[1]);
    }

    [TestMethod]
    public void Ffmpeg_written_file_with_hdr10()
    {
        // Written by ffmpeg: no BPS statistics, ENCODER per track.
        var details = Load("hevc-hdr10-eac3.json");

        var video = details.Video.Single();
        Assert.AreEqual("HDR10", video.DynamicRange);
        Assert.AreEqual("2160p", video.Resolution, "3840x1600 scope is still 2160p");
        Assert.IsNull(video.BitRate);

        Assert.AreEqual("EAC3", details.Audio.First().Codec);
        Assert.AreEqual("5.1", details.Audio.First().ChannelLayout);
        Assert.AreEqual("French", details.Audio.Last().LanguageName);
        Assert.AreEqual("Alien (1979) - Director's Cut", details.Title);
        Assert.AreEqual("BDRemux", details.GetTag("SOURCE"));
    }

    [TestMethod]
    [DataRow("truehd", "Dolby TrueHD", "TrueHD")]
    [DataRow("eac3", "Dolby Digital Plus + Dolby Atmos", "EAC3 Atmos")]
    [DataRow("dts", "DTS-HD MA", "DTS-HD MA")]
    [DataRow("dts", "DTS-HD MA + DTS:X IMAX", "DTS-X")]
    [DataRow("dts", "DTS-ES", "DTS-ES")]
    [DataRow("dts", null, "DTS")]
    [DataRow("aac", "HE-AAC", "HE-AAC")]
    [DataRow("pcm_s24le", null, "PCM")]
    [DataRow("flac", null, "FLAC")]
    public void Audio_codec_names_use_radarr_words(string codec, string? profile, string expected)
    {
        Assert.AreEqual(expected, MediaParser.AudioCodecName(codec, profile));
    }

    [TestMethod]
    [DataRow(8, "7.1(wide)", "7.1")]
    [DataRow(6, "5.1(side)", "5.1")]
    [DataRow(2, "stereo", "2.0")]
    [DataRow(1, "mono", "1.0")]
    [DataRow(6, null, "5.1")]
    [DataRow(3, "weird", "3ch")]
    public void Channel_layouts(int channels, string? layout, string expected)
    {
        Assert.AreEqual(expected, MediaParser.ChannelLayout(channels, layout));
    }

    [TestMethod]
    [DataRow(3840, 2160, "2160p")]
    [DataRow(3840, 1600, "2160p")]
    [DataRow(1920, 800, "1080p")]
    [DataRow(1280, 720, "720p")]
    [DataRow(720, 576, "576p")]
    [DataRow(720, 480, "480p")]
    public void Resolution_labels(int width, int height, string expected)
    {
        Assert.AreEqual(expected, MediaParser.ResolutionLabel(width, height));
    }

    [TestMethod]
    public void Dolby_vision_profile_8_shows_base_layer()
    {
        var dovi = new FFprobeSideData { DvProfile = 8, DvBlSignalCompatibilityId = 1 };
        Assert.AreEqual("8.1", MediaParser.DolbyVisionProfile(dovi));
        Assert.AreEqual("5", MediaParser.DolbyVisionProfile(new FFprobeSideData { DvProfile = 5 }));
        Assert.IsNull(MediaParser.DolbyVisionProfile(null));
    }

    [TestMethod]
    public void Hdr10_plus_from_frame_side_data()
    {
        var frames = MediaParser.DeserializeFrames(
            """{"frames":[{"side_data_list":[{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]}]}""");
        Assert.IsTrue(MediaParser.HasHdr10Plus(frames));
        Assert.IsFalse(MediaParser.HasHdr10Plus(MediaParser.DeserializeFrames("""{"frames":[{}]}""")));
    }

    [TestMethod]
    [DataRow("eng", "English")]
    [DataRow("ger", "German")]
    [DataRow("jpn", "Japanese")]
    [DataRow("und", "Undetermined")]
    [DataRow("", "Undetermined")]
    public void Language_names(string code, string expected)
    {
        Assert.AreEqual(expected, Languages.Name(code));
    }
}
