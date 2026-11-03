using Microsoft.Extensions.Options;
using Specarr.Media;
using Specarr.Options;

namespace Specarr.Tools;

public sealed record ToolStatus
{
    public required ToolDefinition Tool { get; init; }
    public string? Installed { get; init; }
    public bool Missing { get; init; }
    public string? Latest { get; init; }
    public string? UpstreamError { get; init; }

    public string? Pinned => Tool.Pinned;

    public bool UpdateAvailable => Latest != null && (Installed ?? Pinned) is { } current
                                   && VersionComparer.Instance.Compare(Latest, current) > 0;
}

/// <summary>
/// Asks every tool for its version when the app starts and looks for newer versions
/// upstream once a day. Nothing is installed: an update is a new image.
/// </summary>
public sealed class ToolStatusService(IOptions<SpecarrOptions> options, ILogger<ToolStatusService> logger) : BackgroundService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly UpdateCheckOptions _options = options.Value.Updates;
    private readonly SemaphoreSlim _checkNow = new(0);

    public IReadOnlyList<ToolStatus> Statuses { get; private set; } = [];
    public DateTime? InstalledCheckedAt { get; private set; }
    public DateTime? UpstreamCheckedAt { get; private set; }
    public bool Checking { get; private set; }
    public bool UpstreamEnabled => _options.Enabled;

    public int UpdateCount => Statuses.Count(s => s.UpdateAvailable);

    public event Action? Changed;

    /// <summary>Checks again now, upstream included (the Tools page button).</summary>
    public void CheckNow() => _checkNow.Release();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var upstream = _options.Enabled;
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshAsync(upstream, stoppingToken);
            try
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
                // The timer only looks upstream when that is on; the button always does.
                upstream = await _checkNow.WaitAsync(interval, stoppingToken) || _options.Enabled;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RefreshAsync(bool upstream, CancellationToken cancellationToken)
    {
        Checking = true;
        Changed?.Invoke();
        try
        {
            var statuses = new List<ToolStatus>();
            foreach (var tool in ToolCatalog.All)
            {
                statuses.Add(await CheckAsync(tool, upstream, cancellationToken));
            }

            Statuses = statuses;
            InstalledCheckedAt = DateTime.UtcNow;
            if (upstream)
            {
                UpstreamCheckedAt = DateTime.UtcNow;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Tool check failed");
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    private async Task<ToolStatus> CheckAsync(ToolDefinition tool, bool upstream, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(tool.Command, tool.VersionArguments, TimeSpan.FromSeconds(15), cancellationToken);
        // ProcessRunner answers -1 and "Could not start ..." when the tool is not there.
        var missing = result.ExitCode == -1 && result.Error.StartsWith("Could not start", StringComparison.Ordinal);
        var installed = missing ? null : ToolCatalog.InstalledVersion(tool, result.Output + "\n" + result.Error);

        string? latest = null, error = null;
        var previous = Statuses.FirstOrDefault(s => s.Tool.Id == tool.Id);
        if (upstream && tool.Upstream != UpstreamKind.None)
        {
            (latest, error) = await LatestAsync(tool, cancellationToken);
        }
        else if (previous != null)
        {
            (latest, error) = (previous.Latest, previous.UpstreamError);
        }

        return new ToolStatus { Tool = tool, Installed = installed, Missing = missing, Latest = latest, UpstreamError = error };
    }

    private static async Task<(string? Latest, string? Error)> LatestAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        try
        {
            string? latest;
            switch (tool.Upstream)
            {
                case UpstreamKind.GitHubRelease:
                {
                    var json = await Http.GetStringAsync($"https://api.github.com/repos/{tool.UpstreamSource}/releases/latest", cancellationToken);
                    var tag = ToolCatalog.GitHubTag(json);
                    latest = tag != null && tool.UpstreamPattern != null ? ToolCatalog.Highest(tool.UpstreamPattern, tag) : tag;
                    break;
                }
                case UpstreamKind.GitHubAssets:
                {
                    var json = await Http.GetStringAsync($"https://api.github.com/repos/{tool.UpstreamSource}/releases/tags/{tool.UpstreamTag}", cancellationToken);
                    latest = ToolCatalog.Highest(tool.UpstreamPattern!, ToolCatalog.GitHubAssetNames(json));
                    break;
                }
                default:
                    latest = ToolCatalog.Highest(tool.UpstreamPattern!, await Http.GetStringAsync(tool.UpstreamSource, cancellationToken));
                    break;
            }

            return latest == null ? (null, $"no version found at {tool.UpstreamSource}") : (latest, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (null, e.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Specarr/{AppInfo.Version} (+https://github.com/yannb2/Specarr)");
        return client;
    }
}
