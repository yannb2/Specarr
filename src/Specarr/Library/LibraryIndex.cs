using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Library;

/// <summary>The files and titles the pages show. Replaced as a whole after each walk; the files' summaries fill in as they are read.</summary>
public sealed class IndexSnapshot
{
    public static readonly IndexSnapshot Empty = new();

    public IReadOnlyList<IndexedFile> Files { get; init; } = [];
    public IReadOnlyList<TitleEntry> Titles { get; init; } = [];
    public IReadOnlyDictionary<string, IndexedFile> FilesById { get; init; } = new Dictionary<string, IndexedFile>();
    public IReadOnlyDictionary<string, TitleEntry> TitlesById { get; init; } = new Dictionary<string, TitleEntry>();
    public IReadOnlyDictionary<string, TitleEntry> TitlesByFolder { get; init; } = new Dictionary<string, TitleEntry>();
    public IReadOnlyList<string> Problems { get; init; } = [];
    public DateTime WalkedUtc { get; init; } = DateTime.MinValue;
    public TimeSpan WalkElapsed { get; init; }

    public TitleEntry? TitleOf(IndexedFile file)
    {
        return TitlesByFolder.GetValueOrDefault(file.Place.TitleFolder ?? file.Path);
    }
}

/// <summary>
/// Walks the libraries at start and every <see cref="SpecarrOptions.RescanHours"/>,
/// then reads new and changed files one by one in the background (newest
/// first). What was read is kept in the <see cref="IndexStore"/>.
/// </summary>
public sealed class LibraryIndex : BackgroundService
{
    private readonly SpecarrOptions _options;
    private readonly IndexStore _store;
    private readonly MediaProbe _probe;
    private readonly ILogger<LibraryIndex> _logger;

    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _resume = new(0);
    private readonly ConcurrentDictionary<string, IndexedFile> _byPath = new();
    private Dictionary<string, StoredFile> _stored = new();
    private volatile bool _walkRequested;
    private volatile bool _paused;
    private DateTime _lastChangeNotice = DateTime.MinValue;
    private int _pendingChange;

    public LibraryIndex(IOptions<SpecarrOptions> options, IndexStore store, MediaProbe probe, ILogger<LibraryIndex> logger)
    {
        _options = options.Value;
        _store = store;
        _probe = probe;
        _logger = logger;
        _probe.Probed += OnProbed;
    }

    public SpecarrOptions Options => _options;

    public IndexSnapshot Snapshot { get; private set; } = IndexSnapshot.Empty;

    /// <summary>True until the first walk is done.</summary>
    public bool IsLoading { get; private set; } = true;

    public bool IsWalking { get; private set; }

    public bool IsPaused => _paused;

    /// <summary>The file being read in the background, if any.</summary>
    public string? CurrentFile { get; private set; }

    public DateTime? NextWalkUtc { get; private set; }

    /// <summary>Raised when files were added, removed or read (at most every two seconds while reading).</summary>
    public event Action? Changed;

    public int PendingCount => Snapshot.Files.Count(f => !f.IsProbed);

    public IndexedFile? FindFile(string id) => Snapshot.FilesById.GetValueOrDefault(id);

    public TitleEntry? FindTitle(string id) => Snapshot.TitlesById.GetValueOrDefault(id);

    /// <summary>Walks the folders again now, e.g. after adding files.</summary>
    public void RequestWalk()
    {
        _walkRequested = true;
        _wake.Release();
        _resume.Release();
    }

    public void Pause() => _paused = true;

    public void Resume()
    {
        _paused = false;
        _resume.Release();
    }

    /// <summary>Tries the files that failed again.</summary>
    public void RetryErrors()
    {
        _store.ClearErrors();
        foreach (var file in Snapshot.Files.Where(f => f.Error != null))
        {
            file.SetResult(null, null);
        }

        NotifyChanged(force: true);
        _wake.Release();
    }

    /// <summary>Forgets everything that was read, so every file is read again.</summary>
    public void ReadEverythingAgain()
    {
        _store.Clear();
        foreach (var file in Snapshot.Files)
        {
            file.SetResult(null, null);
        }

        NotifyChanged(force: true);
        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            LoadStore();
            while (!stoppingToken.IsCancellationRequested)
            {
                await WalkAsync(stoppingToken);
                IsLoading = false;
                NotifyChanged(force: true);
                await ProbePendingAsync(stoppingToken);

                var interval = _options.RescanHours > 0 ? TimeSpan.FromHours(_options.RescanHours) : Timeout.InfiniteTimeSpan;
                NextWalkUtc = _options.RescanHours > 0 ? Snapshot.WalkedUtc + interval : null;
                while (!_walkRequested)
                {
                    var wait = NextWalkUtc is { } next ? next - DateTime.UtcNow : Timeout.InfiniteTimeSpan;
                    if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
                    {
                        break;
                    }

                    await _wake.WaitAsync(wait, stoppingToken);

                    // Woken by "retry" or "read again", or a file opened on a page: read what is pending.
                    await ProbePendingAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _logger.LogError(e, "The library index stopped");
            throw;
        }
    }

    private void LoadStore()
    {
        var rows = _store.LoadAll();
        _stored = rows.ToDictionary(r => r.Path);
        _logger.LogInformation("The cache knows {Count} files", rows.Count);

        // Summaries from an older version are built again from what the tools printed, without reading the files.
        var outdated = rows.Where(r => r.SummaryVersion != FileSummary.CurrentVersion && r.Error == null).ToList();
        foreach (var row in outdated)
        {
            if (_store.Get(row.Path, row.Size, row.ModifiedTicks) is not { } raw)
            {
                continue;
            }

            var built = MediaProbe.Build(row.Path, row.Size, raw);
            if (built.Details != null)
            {
                var summary = FileSummary.From(built.Details);
                _store.UpdateSummary(row.Path, summary);
                _stored[row.Path] = row with { SummaryJson = summary.ToJson(), SummaryVersion = FileSummary.CurrentVersion };
            }
        }

        if (outdated.Count > 0)
        {
            _logger.LogInformation("Updated {Count} summaries to version {Version}", outdated.Count, FileSummary.CurrentVersion);
        }
    }

    private async Task WalkAsync(CancellationToken cancellationToken)
    {
        _walkRequested = false;
        IsWalking = true;
        NotifyChanged(force: true);
        try
        {
            var walk = await Task.Run(() => LibraryScanner.Walk(_options, cancellationToken), cancellationToken);
            var seen = new HashSet<string>();
            var files = new List<IndexedFile>(walk.Files.Count);
            foreach (var found in walk.Files)
            {
                seen.Add(found.Path);
                if (_byPath.TryGetValue(found.Path, out var known) && known.SizeBytes == found.Size && known.ModifiedUtc == found.ModifiedUtc)
                {
                    files.Add(known);
                    continue;
                }

                var place = LibraryLayout.Parse(found.Library, found.Path, _options.ExtrasFolderNames);
                var file = new IndexedFile(found.Path, found.Library, place, found.Size, found.ModifiedUtc);
                if (_stored.TryGetValue(found.Path, out var row) && row.Size == found.Size && row.ModifiedTicks == found.ModifiedUtc.Ticks)
                {
                    file.SetResult(FileSummary.FromJson(row.SummaryJson), row.Error);
                }

                _byPath[found.Path] = file;
                files.Add(file);
            }

            var gone = _byPath.Keys.Where(p => !seen.Contains(p)).ToList();
            foreach (var path in gone)
            {
                _byPath.TryRemove(path, out _);
            }

            // Forget the rows of files that are gone. Changed files are saved anew once read.
            _store.Delete(gone.Concat(_stored.Keys.Where(p => !seen.Contains(p))).Distinct().ToList());
            _stored = new Dictionary<string, StoredFile>();

            Snapshot = BuildSnapshot(files, walk);
            _logger.LogInformation("Found {Files} video files in {Titles} titles in {Elapsed:0.0} s; {Pending} to read",
                files.Count, Snapshot.Titles.Count, walk.Elapsed.TotalSeconds, files.Count(f => !f.IsProbed));
            foreach (var problem in walk.Problems)
            {
                _logger.LogWarning("{Problem}", problem);
            }
        }
        finally
        {
            IsWalking = false;
        }
    }

    internal static IndexSnapshot BuildSnapshot(List<IndexedFile> files, WalkResult walk)
    {
        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        var titles = files
            .GroupBy(f => f.Place.TitleFolder ?? f.Path)
            .Select(g =>
            {
                var first = g.First();
                return new TitleEntry
                {
                    Id = PathId.Encode(g.Key),
                    Library = first.Library,
                    Category = first.Place.Category,
                    Folder = first.Place.TitleFolder,
                    Name = first.Place.TitleName,
                    Year = first.Place.Year,
                    Files = g.ToList()
                };
            })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Year)
            .ToList();

        return new IndexSnapshot
        {
            Files = files,
            Titles = titles,
            FilesById = files.ToDictionary(f => f.Id),
            TitlesById = titles.ToDictionary(t => t.Id),
            TitlesByFolder = titles.ToDictionary(t => t.Folder ?? t.Files[0].Path),
            Problems = walk.Problems,
            WalkedUtc = DateTime.UtcNow,
            WalkElapsed = walk.Elapsed
        };
    }

    private async Task ProbePendingAsync(CancellationToken cancellationToken)
    {
        while (!_walkRequested && !cancellationToken.IsCancellationRequested)
        {
            var batch = Snapshot.Files
                .Where(f => !f.IsProbed)
                .OrderByDescending(f => f.ModifiedUtc)
                .Take(50)
                .ToList();
            if (batch.Count == 0)
            {
                CurrentFile = null;
                NotifyChanged(force: true);
                return;
            }

            await Parallel.ForEachAsync(batch,
                new ParallelOptions { MaxDegreeOfParallelism = _options.ProbeConcurrency, CancellationToken = cancellationToken },
                async (file, token) =>
                {
                    while (_paused && !_walkRequested)
                    {
                        CurrentFile = null;
                        NotifyChanged(force: true);
                        await _resume.WaitAsync(token);
                    }

                    if (_walkRequested || file.IsProbed || !File.Exists(file.Path))
                    {
                        if (!File.Exists(file.Path))
                        {
                            file.SetResult(null, "The file is gone.");
                        }

                        return;
                    }

                    CurrentFile = file.Path;
                    NotifyChanged();
                    var result = await _probe.ProbeAsync(file.Path, cancellationToken: token);

                    // Read from the cache (opened on a page before this walk found it): no event came.
                    if (!file.IsProbed)
                    {
                        file.SetResult(result.Details is { } details ? FileSummary.From(details) : null, result.Error ?? "Could not be read.");
                    }
                });
        }
    }

    private void OnProbed(ProbedEventArgs e)
    {
        if (_byPath.TryGetValue(e.Path, out var file) && file.SizeBytes == e.Size && file.ModifiedTicks == e.ModifiedTicks)
        {
            file.SetResult(e.Summary, e.Error);
            NotifyChanged();
        }
    }

    private void NotifyChanged(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - _lastChangeNotice < TimeSpan.FromSeconds(2))
        {
            // Send the last change later, so the pages end up up to date.
            if (Interlocked.Exchange(ref _pendingChange, 1) == 0)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                {
                    Interlocked.Exchange(ref _pendingChange, 0);
                    NotifyChanged(force: true);
                }, TaskScheduler.Default);
            }

            return;
        }

        _lastChangeNotice = now;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "A page failed to refresh");
        }
    }

    public override void Dispose()
    {
        _probe.Probed -= OnProbed;
        base.Dispose();
    }
}
