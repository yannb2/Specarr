using Specarr.Library;
using Specarr.Media;

namespace Specarr.Tests;

[TestClass]
public sealed class FormatTests
{
    [TestMethod]
    [DataRow(512L, "512 B")]
    [DataRow(1536L, "1.50 KB")]
    [DataRow(80_000_000_000L, "74.5 GB")]
    public void Sizes(long bytes, string expected)
    {
        Assert.AreEqual(expected, Format.Size(bytes));
    }

    [TestMethod]
    public void Bit_rates()
    {
        Assert.AreEqual("62.0 Mb/s", Format.BitRate(62_000_000));
        Assert.AreEqual("640 kb/s", Format.BitRate(640_000));
    }

    [TestMethod]
    public void Time_ago()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual("just now", Format.TimeAgo(now.AddSeconds(-20), now));
        Assert.AreEqual("1 hour ago", Format.TimeAgo(now.AddMinutes(-61), now));
        Assert.AreEqual("3 days ago", Format.TimeAgo(now.AddDays(-3), now));
    }

    [TestMethod]
    [DataRow("/movies/.recycle/live-action/Alien (1979)/Alien (1979) - Director's Cut [2160p].mkv")]
    [DataRow("/tv/.recycle/Show/Season 01/Show S01E01 - Café ½.mkv")]
    public void Path_ids_round_trip_and_are_url_safe(string path)
    {
        var id = PathId.Encode(path);
        Assert.IsFalse(id.Contains('/') || id.Contains('+') || id.Contains('='));
        Assert.AreEqual(path, PathId.Decode(id));
    }

    [TestMethod]
    public void Bad_path_id_decodes_to_null()
    {
        Assert.IsNull(PathId.Decode("not base64!"));
    }
}

[TestClass]
public sealed class TimeUntilTests
{
    [TestMethod]
    public void Time_until()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual("now", Format.TimeUntil(now.AddSeconds(-5), now));
        Assert.AreEqual("in 10 minutes", Format.TimeUntil(now.AddMinutes(10), now));
        Assert.AreEqual("in 5 hours", Format.TimeUntil(now.AddHours(5), now));
        Assert.AreEqual("in 7 days", Format.TimeUntil(now.AddDays(7), now));
    }
}
