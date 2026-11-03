using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Specarr.Library;
using Specarr.Options;

namespace Specarr.Media;

public sealed record ProbeResult(MediaDetails? Details, string? Error, string? RawJson);

/// <summary>What the tools printed for one file: everything needed to build <see cref="MediaDetails"/> again without reading the file.</summary>
public sealed record RawProbe
{
    public required string FfprobeJson { get; init; }

    /// <summary>Video streams with HDR10+ metadata in their first frames.</summary>
    public IReadOnlyList<int> Hdr10PlusStreams { get; init; } = [];

    public string? DoviSummary { get; init; }
    public string? DoviError { get; init; }
    public MediaInfoFacts? MediaInfo { get; init; }

    /// <summary>Audio bit rates measured on samples, by stream index, for tracks the file and mediainfo say nothing about.</summary>
    public IReadOnlyDictionary<int, long> MeasuredAudioBitRates { get; init; } = new Dictionary<int, long>();
}

/// <summary>A file was read; the library index listens to keep its table up to date.</summary>
public sealed record ProbedEventArgs(string Path, long Size, long ModifiedTicks, FileSummary? Summary, string? Error);

/// <summary>
/// Reads files with ffprobe, dovi_tool and mediainfo, and keeps what they
/// printed in the <see cref="IndexStore"/>, keyed by path, size and date.
/// Opening a file the background scan already read costs no disk reads.
/// </summary>
public sealed class MediaProbe(IOptions<SpecarrOptions> options, IndexStore store, ILogger<MediaProbe> logger)
{
    public const string FfprobeExecutable = "ffprobe";
    public const string MediaInfoExecutable = "mediainfo";

    /// <summary>An 80 GB remux takes several minutes to read from disks.</summary>
    private static readonly TimeSpan WholeFilmTimeout = TimeSpan.FromMinutes(60);

    private readonly ConcurrentDictionary<string, Task<ProbeResult>> _inFlight = new();
    private readonly ConcurrentDictionary<string, Task<DolbyVisionResult>> _wholeFilm = new();

    public event Action<ProbedEventArgs>? Probed;

    /// <summary>
    /// The details of a file: from the cache when it has this exact file,
    /// otherwise read now and saved. <paramref name="force"/> reads it again.
    /// </summary>
    public Task<ProbeResult> ProbeAsync(string path, bool force = false, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return Task.FromResult(new ProbeResult(null, "The file no longer exists.", null));
        }

        var size = file.Length;
        var ticks = file.LastWriteTimeUtc.Ticks;
        if (!force && store.Get(path, size, ticks) is { } cached)
        {
            return Task.FromResult(Build(path, size, cached));
        }

        var key = $"{path}|{size}|{ticks}";
        return _inFlight.GetOrAdd(key, _ => ReadAndStoreAsync(key, path, size, ticks, cancellationToken));
    }

    private async Task<ProbeResult> ReadAndStoreAsync(string key, string path, long size, long ticks, CancellationToken cancellationToken)
    {
        try
        {
            var (raw, error, output) = await ReadAsync(path, cancellationToken);
            ProbeResult result;
            FileSummary? summary = null;
            if (raw != null)
            {
                result = Build(path, size, raw);
                if (result.Details != null)
                {
                    summary = FileSummary.From(result.Details);
                }
                else
                {
                    error = result.Error;
                }
            }
            else
            {
                result = new ProbeResult(null, error, output);
            }

            store.Save(path, size, ticks, raw, error, summary);
            Probed?.Invoke(new ProbedEventArgs(path, size, ticks, summary, error));
            return result;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    /// <summary>Runs the tools. Returns what they printed, or why it failed.</summary>
    private async Task<(RawProbe? Raw, string? Error, string? Output)> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(FfprobeExecutable,
            ["-v", "error", "-print_format", "json", "-show_streams", "-show_chapters", "-show_format", path],
            TimeSpan.FromSeconds(60), cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            logger.LogWarning("ffprobe failed on {Path}: {Error}", path, result.Error);
            return (null, $"ffprobe could not read the file: {result.Error.Trim()}", result.Output);
        }

        MediaDetails details;
        try
        {
            details = MediaParser.Parse(path, 0, MediaParser.Deserialize(result.Output) ?? new FFprobeResult());
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read ffprobe output for {Path}", path);
            return (null, $"Could not read ffprobe's output: {e.Message}", result.Output);
        }

        var hdr10Plus = new List<int>();
        if (options.Value.DetectHdr10Plus)
        {
            foreach (var video in details.Video.Where(v => v.HdrFormat == "HDR10"))
            {
                if (await HasHdr10PlusAsync(path, video.Index, cancellationToken) == true)
                {
                    hdr10Plus.Add(video.Index);
                }
            }
        }

        string? doviSummary = null;
        string? doviError = null;
        if (options.Value.ReadDolbyVision && details.HasDolbyVision)
        {
            var dv = await DoviTool.ReadAsync(path, DoviTool.SampleFrames, TimeSpan.FromMinutes(2), cancellationToken);
            doviSummary = dv.RawSummary;
            doviError = dv.Error;
            if (dv.Error != null)
            {
                logger.LogWarning("dovi_tool failed on {Path}: {Error}", path, dv.Error);
            }
        }

        MediaInfoFacts? facts = null;
        if (options.Value.ReadMediaInfo)
        {
            var info = await ProcessRunner.RunAsync(MediaInfoExecutable, ["--Output=JSON", path], TimeSpan.FromSeconds(60), cancellationToken);
            facts = info.Success ? MediaInfoFacts.Parse(info.Output) : null;
        }

        IReadOnlyDictionary<int, long> measured = new Dictionary<int, long>();
        if (options.Value.EstimateAudioBitRate && AudioBitRate.NeedsMeasuring(details, facts?.TrackBitRates))
        {
            measured = await AudioBitRate.MeasureAsync(FfprobeExecutable, path, details.Duration, cancellationToken);
        }

        return (new RawProbe
        {
            FfprobeJson = result.Output,
            Hdr10PlusStreams = hdr10Plus,
            DoviSummary = doviSummary,
            DoviError = doviError,
            MediaInfo = facts,
            MeasuredAudioBitRates = measured
        }, null, null);
    }

    /// <summary>Builds the details from what the tools printed. No I/O.</summary>
    public static ProbeResult Build(string path, long size, RawProbe raw)
    {
        MediaDetails details;
        try
        {
            details = MediaParser.Parse(path, size, MediaParser.Deserialize(raw.FfprobeJson) ?? new FFprobeResult());
        }
        catch (Exception e)
        {
            return new ProbeResult(null, $"Could not read ffprobe's output: {e.Message}", raw.FfprobeJson);
        }

        foreach (var video in details.Video.Where(v => v.HdrFormat == "HDR10" && raw.Hdr10PlusStreams.Contains(v.Index)))
        {
            video.HdrFormat = "HDR10+";
        }

        if (details.HasDolbyVision)
        {
            details.DolbyVision = DoviTool.ParseSummary(raw.DoviSummary, wholeFilm: false);
            details.DolbyVisionError = raw.DoviError
                ?? (details.DolbyVision == null && raw.DoviSummary == null ? "Not read: Specarr__ReadDolbyVision is off." : null);
            if (details.DolbyVision?.Layer is { } layer)
            {
                foreach (var video in details.Video.Where(v => v.HasEnhancementLayer))
                {
                    video.EnhancementLayer = layer;
                }
            }
        }

        if (raw.MediaInfo is { } facts)
        {
            details.WritingApplication = facts.WritingApplication;
            details.EncoderLibrary = facts.EncoderLibrary;
            details.EncoderSettings = facts.EncoderSettings;
            details.MediaInfoVideoBitRate = facts.VideoBitRate;
        }

        AudioBitRate.Apply(details, raw.MediaInfo?.TrackBitRates, raw.MeasuredAudioBitRates);

        details.Source = SourceGuesser.Guess(details);
        return new ProbeResult(details, null, raw.FfprobeJson);
    }

    /// <summary>mediainfo's own text view, for the raw tab. Not cached: it is quick and only shown on request.</summary>
    public async Task<string> MediaInfoTextAsync(string path)
    {
        var result = await ProcessRunner.RunAsync(MediaInfoExecutable, [path], TimeSpan.FromSeconds(60));
        return result.Success ? result.Output : $"mediainfo failed: {result.Error.Trim()}";
    }

    /// <summary>
    /// The Dolby Vision RPU of every frame, for the brightness of the whole film
    /// (L1 MaxCLL and MaxFALL) and the scene count. Reads the whole file.
    /// </summary>
    public Task<DolbyVisionResult> WholeFilmDolbyVisionAsync(string path)
    {
        var key = CacheKey(path);
        var task = _wholeFilm.GetOrAdd(key, _ => DoviTool.ReadAsync(path, null, WholeFilmTimeout));
        if (task.IsCompleted && task.Result.Details == null)
        {
            _wholeFilm.TryRemove(key, out _);
        }

        return task;
    }

    /// <summary>The whole-film result when it was already read, without reading.</summary>
    public DolbyVisionDetails? CachedWholeFilm(string path)
    {
        return _wholeFilm.TryGetValue(CacheKey(path), out var task) && task.IsCompletedSuccessfully
            ? task.Result.Details
            : null;
    }

    /// <summary>
    /// HDR10+ is per-frame metadata, not in the stream header, so this decodes
    /// the first few frames. Null when ffprobe fails.
    /// </summary>
    private static async Task<bool?> HasHdr10PlusAsync(string path, int streamIndex, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(FfprobeExecutable,
        [
            "-v", "error", "-select_streams", streamIndex.ToString(CultureInfo.InvariantCulture),
            "-read_intervals", "%+#5", "-show_frames", "-show_entries", "frame=side_data_list",
            "-print_format", "json", path
        ], TimeSpan.FromSeconds(60), cancellationToken);

        if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        try
        {
            return MediaParser.HasHdr10Plus(MediaParser.DeserializeFrames(result.Output));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CacheKey(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : path;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
