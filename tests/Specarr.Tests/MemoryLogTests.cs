using Microsoft.Extensions.Logging;
using Specarr.Logging;

namespace Specarr.Tests;

[TestClass]
public sealed class MemoryLogTests
{
    [TestMethod]
    public void KeepsTheNewestLinesFirst()
    {
        var log = new MemoryLog();
        for (var i = 0; i < MemoryLog.Capacity + 5; i++)
        {
            log.Add(LogLevel.Information, "Specarr.Library.LibraryIndex", $"line {i}", null);
        }

        var lines = log.Lines();
        Assert.HasCount(MemoryLog.Capacity, lines);
        Assert.AreEqual($"line {MemoryLog.Capacity + 4}", lines[0].Message);
        Assert.AreEqual("line 5", lines[^1].Message);
    }

    [TestMethod]
    public void ProviderFeedsTheLogWithExceptions()
    {
        var log = new MemoryLog();
        var logger = new MemoryLoggerProvider(log).CreateLogger("Specarr.Library.IndexStore");
        logger.LogWarning(new InvalidOperationException("boom"), "Could not read {App}", "Radarr");

        var line = log.Lines().Single();
        Assert.AreEqual(LogLevel.Warning, line.Level);
        Assert.AreEqual("Could not read Radarr", line.Message);
        Assert.Contains("boom", line.Exception!);
        Assert.AreEqual("IndexStore", MemoryLog.ShortSource(line.Source));

        log.Clear();
        Assert.IsEmpty(log.Lines());
    }
}
