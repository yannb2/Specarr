using Microsoft.Extensions.Logging.Abstractions;
using Specarr.Library;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Tests;

[TestClass]
public sealed class CacheTests
{
    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    [TestMethod]
    public void Store_keeps_a_read_until_the_file_changes()
    {
        var folder = Directory.CreateTempSubdirectory("specarr-db").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new SpecarrOptions { DatabasePath = Path.Combine(folder, "sub", "specarr.db") });
            var raw = new RawProbe
            {
                FfprobeJson = Sample("uhd-remux-dv.json"),
                Hdr10PlusStreams = [0],
                DoviError = "dovi_tool is not installed",
                MediaInfo = new MediaInfoFacts
                {
                    WritingApplication = "MakeMKV v1.18.1", VideoBitRate = 61_000_000,
                    TrackBitRates = [new MediaInfoBitRate(1, null, 9_600_000)]
                },
                MeasuredAudioBitRates = new Dictionary<int, long> { [2] = 1_510_000 }
            };
            var built = MediaProbe.Build("/movies/a.mkv", 100, raw);
            var summary = FileSummary.From(built.Details!);

            using (var store = new IndexStore(options, NullLogger<IndexStore>.Instance))
            {
                Assert.IsNotNull(store.FilePath);
                store.Save("/movies/a.mkv", 100, 5, raw, null, summary);
                store.Save("/movies/b.mkv", 200, 6, null, "ffprobe could not read the file", null);
            }

            using (var store = new IndexStore(options, NullLogger<IndexStore>.Instance))
            {
                var rows = store.LoadAll();
                Assert.HasCount(2, rows);
                Assert.AreEqual(FileSummary.CurrentVersion, rows.Single(r => r.Path == "/movies/a.mkv").SummaryVersion);
                Assert.AreEqual("ffprobe could not read the file", rows.Single(r => r.Path == "/movies/b.mkv").Error);

                var again = store.Get("/movies/a.mkv", 100, 5);
                Assert.IsNotNull(again);
                Assert.AreEqual("MakeMKV v1.18.1", again.MediaInfo?.WritingApplication);
                CollectionAssert.AreEqual(new[] { 0 }, again.Hdr10PlusStreams.ToArray());
                Assert.AreEqual(9_600_000L, again.MediaInfo?.TrackBitRates.Single().MaxBitRate);
                Assert.AreEqual(1_510_000L, again.MeasuredAudioBitRates[2]);

                // A different size or date is another file.
                Assert.IsNull(store.Get("/movies/a.mkv", 101, 5));
                Assert.IsNull(store.Get("/movies/a.mkv", 100, 6));

                store.ClearErrors();
                Assert.HasCount(1, store.LoadAll());
                store.Delete(["/movies/a.mkv"]);
                Assert.HasCount(0, store.LoadAll());
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Store_adds_the_audio_bit_rate_column_to_an_older_cache()
    {
        var folder = Directory.CreateTempSubdirectory("specarr-db").FullName;
        try
        {
            var path = Path.Combine(folder, "specarr.db");
            using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                old.Open();
                using var command = old.CreateCommand();
                command.CommandText = """
                    CREATE TABLE files (path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, probed_utc INTEGER NOT NULL,
                        ffprobe TEXT, hdr10plus TEXT, dovi TEXT, dovi_error TEXT, mediainfo TEXT, error TEXT, summary TEXT, summary_version INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO files (path, size, mtime, probed_utc, ffprobe, summary, summary_version) VALUES ('/movies/old.mkv', 1, 1, 0, '{}', '{}', 1);
                    """;
                command.ExecuteNonQuery();
            }

            var options = Microsoft.Extensions.Options.Options.Create(new SpecarrOptions { DatabasePath = path });
            using var store = new IndexStore(options, NullLogger<IndexStore>.Instance);
            var row = store.LoadAll().Single();
            Assert.IsLessThan(FileSummary.ReadVersion, row.SummaryVersion, "Read again in the background");
            Assert.IsEmpty(store.Get("/movies/old.mkv", 1, 1)!.MeasuredAudioBitRates);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [TestMethod]
    public void Store_falls_back_to_memory()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new SpecarrOptions { DatabasePath = "/proc/specarr/specarr.db" });
        using var store = new IndexStore(options, NullLogger<IndexStore>.Instance);

        Assert.IsNull(store.FilePath);
        store.Save("/x.mkv", 1, 1, null, "error", null);
        Assert.HasCount(1, store.LoadAll());
    }

    [TestMethod]
    public void Summary_round_trip()
    {
        var details = MediaProbe.Build("/movies/a.mkv", 100, new RawProbe { FfprobeJson = Sample("uhd-remux-dv.json") }).Details!;
        var summary = FileSummary.From(details);
        var back = FileSummary.FromJson(summary.ToJson())!;

        Assert.AreEqual("2160p", back.Resolution);
        Assert.AreEqual("7", back.DvProfile);
        Assert.AreEqual(SourceKind.BDRemux, back.Source);
        Assert.AreEqual("TrueHD Atmos 7.1", back.MainAudio);
        Assert.HasCount(3, back.Audio);
        Assert.AreEqual(back.Tag("TMDB"), back.Tag("tmdb"));
    }

    [TestMethod]
    public void Build_applies_hdr10plus_and_mediainfo()
    {
        var raw = new RawProbe
        {
            FfprobeJson = Sample("hevc-hdr10-eac3.json"),
            Hdr10PlusStreams = [0],
            MediaInfo = new MediaInfoFacts { EncoderLibrary = "x265", EncoderSettings = "crf=18" }
        };
        var details = MediaProbe.Build("/movies/b.mkv", 100, raw).Details!;

        Assert.AreEqual("HDR10+", details.Video.Single().HdrFormat);
        Assert.AreEqual("x265", details.EncoderLibrary);
        Assert.AreNotEqual(SourceKind.BDRemux, details.Source?.Kind);
    }

    [TestMethod]
    public void MediaInfo_json()
    {
        const string json = """
            {"creatingLibrary":{"name":"MediaInfoLib","version":"24.12"},"media":{"@ref":"/m/a.mkv","track":[
              {"@type":"General","Format":"Matroska","Encoded_Application":"MakeMKV v1.18.1 linux(x64-release)","Encoded_Library":"libmakemkv v1.18.1"},
              {"@type":"Video","Format":"HEVC","BitRate":"58123456","Encoded_Library":"x265 - 3.5+1:[Linux][GCC 11.2.0][64 bit] 10bit","Encoded_Library_Settings":"cpuid=1111039 / crf=18.0"},
              {"@type":"Audio","Format":"MLP FBA"}]}}
            """;
        var facts = MediaInfoFacts.Parse(json)!;

        Assert.AreEqual("MakeMKV v1.18.1 linux(x64-release)", facts.WritingApplication);
        Assert.AreEqual("x265", facts.EncoderLibrary);
        Assert.AreEqual("cpuid=1111039 / crf=18.0", facts.EncoderSettings);
        Assert.AreEqual(58_123_456L, facts.VideoBitRate);
        Assert.IsNull(MediaInfoFacts.Parse("not json"));
    }

    [TestMethod]
    public void Filters()
    {
        var movies = new LibraryOptions { Name = "Movies", Path = "/movies" };
        IndexedFile Make(string path, FileSummary? summary)
        {
            var file = new IndexedFile(path, movies, LibraryLayout.Parse(movies, path, SpecarrOptions.DefaultExtrasFolderNames), 1, DateTime.UtcNow);
            file.SetResult(summary, null);
            return file;
        }

        var fel = Make("/movies/live-action/A (2000)/A (2000).mkv", new FileSummary
        {
            Resolution = "2160p", VideoCodec = "HEVC", DvProfile = "7", DvLayer = "FEL", HdrFormat = "HDR10",
            Audio = [new AudioSummary("TrueHD Atmos", "7.1", "eng", true, false)],
            Subtitles = [new SubtitleSummary("PGS", "fre", false, false, false)]
        });
        var web = Make("/movies/anime/B (2010)/B (2010).mkv", new FileSummary
        {
            Resolution = "1080p", VideoCodec = "AVC",
            Audio = [new AudioSummary("EAC3", "5.1", "jpn", true, false)],
            Subtitles = [new SubtitleSummary("SRT", "eng", false, false, true)]
        });
        var pending = Make("/movies/anime/C (2020)/C (2020).mkv", null);
        var all = new[] { fel, web, pending };

        CollectionAssert.AreEqual(new[] { fel }, new FileFilter { Range = "dv7fel" }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { web }, new FileFilter { Range = "sdr" }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { fel }, new FileFilter { Audio = "lossless" }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { fel }, new FileFilter { SubtitleLanguage = "eng", SubtitlesMissing = true }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { web, pending }, new FileFilter { Category = "anime" }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { pending }, new FileFilter { State = "pending" }.Apply(all).ToArray());
        CollectionAssert.AreEqual(new[] { web }, new FileFilter { Resolution = "1080p", AudioLanguage = "jpn" }.Apply(all).ToArray());
    }
}
