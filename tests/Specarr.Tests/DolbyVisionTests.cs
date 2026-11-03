using Specarr.Compare;
using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class DolbyVisionTests
{
    // dovi_tool 2.3.4, info --summary on the first 500 frames of a profile 8.1 file.
    private const string Profile8 = """
        Parsing RPU file...

        Summary:
          Frames: 500
          Profile: 8
          DM version: 2 (CM v4.0)
          Scene/shot count: 2
          RPU mastering display: 0.0001/1000 nits
          RPU content light level (L1): MaxCLL: 1000.60 nits, MaxFALL: 63.99 nits
          L6 metadata: Mastering display: 0.0001/1000 nits. MaxCLL: 1000 nits, MaxFALL: 400 nits
          L5 offsets: top=0, bottom=0, left=0, right=0
          L2 trims: 100 nits
          L9 MDP: DCI-P3 D65
        """;

    private const string Profile7Fel = """
        Summary:
          Frames: 500
          Profile: 7 (FEL)
          DM version: 1 (CM v2.9)
          Scene/shot count: 4
          RPU mastering display: 0.0050/4000 nits
          L6 metadata: Mastering display: 0.0050/4000 nits. MaxCLL: 3500 nits, MaxFALL: 600 nits
          L2 trims: 100 nits, 600 nits,
            1000 nits
        """;

    [TestMethod]
    public void Reads_the_summary_lines()
    {
        var dv = DoviTool.ParseSummary(Profile8, wholeFilm: false)!;

        Assert.AreEqual(500, dv.Frames);
        Assert.IsFalse(dv.WholeFilm);
        Assert.AreEqual("8", dv.Profile);
        Assert.IsNull(dv.Layer);
        Assert.AreEqual("CM v4.0", dv.CmVersion);
        Assert.HasCount(10, dv.Lines);
        Assert.AreEqual("MaxCLL: 1000.60 nits, MaxFALL: 63.99 nits", dv.Get("RPU content light level (L1)"));
        Assert.AreEqual("DCI-P3 D65", dv.Get("L9 MDP"));
    }

    [TestMethod]
    public void Profile_7_layer_and_cm_29()
    {
        var dv = DoviTool.ParseSummary(Profile7Fel, wholeFilm: true)!;

        Assert.AreEqual("7", dv.Profile);
        Assert.AreEqual("FEL", dv.Layer);
        Assert.AreEqual("CM v2.9", dv.CmVersion);
        Assert.IsTrue(dv.WholeFilm);

        // A wrapped line stays with its label.
        Assert.AreEqual("100 nits, 600 nits,\n1000 nits", dv.Get("L2 trims"));
    }

    [TestMethod]
    [DataRow("Profile: 7 (MEL)", "MEL")]
    [DataRow("Profiles: 7 (FEL, MEL)", "FEL")]
    [DataRow("Profile: 8", null)]
    public void Layer_from_the_profile_line(string line, string? layer)
    {
        var dv = DoviTool.ParseSummary($"Summary:\n  Frames: 10\n  {line}\n", false)!;

        Assert.AreEqual(layer, dv.Layer);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("error: no RPU found")]
    public void No_summary_is_null(string? output)
    {
        Assert.IsNull(DoviTool.ParseSummary(output, false));
    }

    [TestMethod]
    [DataRow("/movies/a/Movie (2026).mkv", true)]
    [DataRow("/movies/a/Movie (2026).MKV", true)]
    [DataRow("/movies/a/Movie (2026).mp4", false)]
    public void Needs_a_matroska_name(string path, bool isMatroska)
    {
        Assert.AreEqual(isMatroska, DoviTool.HasMatroskaExtension(path));
    }

    [TestMethod]
    public void Layer_shows_in_the_dynamic_range()
    {
        var track = new TrackInfo
        {
            Type = TrackType.Video,
            DolbyVisionProfile = "7",
            HasEnhancementLayer = true,
            HdrFormat = "HDR10"
        };
        Assert.AreEqual("DV 7 (EL) HDR10", track.DynamicRange);

        track.EnhancementLayer = "FEL";
        Assert.AreEqual("DV 7 FEL HDR10", track.DynamicRange);
    }

    [TestMethod]
    public void Compare_shows_dolby_vision_rows()
    {
        var left = Details("/bin/a.mkv", DoviTool.ParseSummary(Profile7Fel, false));
        var right = Details("/lib/a.mkv", DoviTool.ParseSummary(Profile8, false));

        var section = Comparison.Build(left, right).Single(s => s.Title == "Dolby Vision");
        var rows = section.Rows.ToDictionary(r => r.Label);

        Assert.IsFalse(rows.ContainsKey("Frames"));
        Assert.AreEqual("1 (CM v2.9)", rows["DM version"].Left);
        Assert.AreEqual("2 (CM v4.0)", rows["DM version"].Right);
        Assert.IsTrue(rows["DM version"].Differs);
        Assert.IsNull(rows["L9 MDP"].Left);
    }

    [TestMethod]
    public void No_dolby_vision_no_section()
    {
        var sections = Comparison.Build(Details("/a.mkv", null), Details("/b.mkv", null));

        Assert.IsFalse(sections.Any(s => s.Title == "Dolby Vision"));
    }

    /// <summary>A real profile 8.1 MKV (2 s, 320×180, made with x265, dovi_tool generate and mkvmerge).</summary>
    [TestMethod]
    public async Task Reads_a_real_file()
    {
        var version = await ProcessRunner.RunAsync(DoviTool.Executable, ["--version"], TimeSpan.FromSeconds(10));
        if (!version.Success)
        {
            Assert.Inconclusive("dovi_tool is not installed.");
        }

        var sample = Path.Combine(AppContext.BaseDirectory, "Samples", "dv-p8-cm40.mkv");
        var result = await DoviTool.ReadAsync(sample, DoviTool.SampleFrames, TimeSpan.FromMinutes(1));

        Assert.IsNull(result.Error);
        Assert.AreEqual(48, result.Details!.Frames);
        Assert.AreEqual("8", result.Details.Profile);
        Assert.AreEqual("CM v4.0", result.Details.CmVersion);
        Assert.AreEqual("MaxCLL: 1000 nits, MaxFALL: 400 nits",
            result.Details.Get("L6 metadata")!.Split(". ")[1]);
    }

    /// <summary>dovi_tool reads only *.mkv names as Matroska; Specarr links other names.</summary>
    [TestMethod]
    public async Task Reads_a_matroska_file_with_another_name()
    {
        var version = await ProcessRunner.RunAsync(DoviTool.Executable, ["--version"], TimeSpan.FromSeconds(10));
        if (!version.Success || OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("dovi_tool is not installed, or no symbolic links.");
        }

        var dir = Directory.CreateTempSubdirectory("specarr-dv-");
        try
        {
            var copy = Path.Combine(dir.FullName, "Movie (2026).mkv.part");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "dv-p8-cm40.mkv"), copy);

            var result = await DoviTool.ReadAsync(copy, null, TimeSpan.FromMinutes(1));

            Assert.IsNull(result.Error);
            Assert.IsTrue(result.Details!.WholeFilm);
            Assert.AreEqual(48, result.Details.Frames);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [TestMethod]
    public async Task Missing_tool_says_so()
    {
        var result = await ProcessRunner.RunAsync("specarr-no-such-tool", ["--version"], TimeSpan.FromSeconds(5));

        Assert.IsFalse(result.Success);
        StringAssert.StartsWith(result.Error, "Could not start");
    }

    private static MediaDetails Details(string path, DolbyVisionDetails? dv)
    {
        return new MediaDetails
        {
            Path = path,
            Tracks = [new TrackInfo { Type = TrackType.Video, Codec = "HEVC", DolbyVisionProfile = dv?.Profile }],
            DolbyVision = dv
        };
    }
}
