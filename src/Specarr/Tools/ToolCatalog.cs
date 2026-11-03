using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Specarr.Media;

namespace Specarr.Tools;

/// <summary>Where a tool's newest version is read.</summary>
public enum UpstreamKind
{
    None,

    /// <summary>A web page; the highest version the regex finds.</summary>
    Page,

    /// <summary>The latest release of a GitHub repository (owner/name).</summary>
    GitHubRelease,

    /// <summary>The asset names of one GitHub release (owner/name, tag); the highest version the regex finds.</summary>
    GitHubAssets
}

/// <summary>A tool in the image: how to ask its version, which version the image pins, where to look for a newer one.</summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public required string Command { get; init; }
    public IReadOnlyList<string> VersionArguments { get; init; } = [];
    public required Regex VersionPattern { get; init; }
    public UpstreamKind Upstream { get; init; }

    /// <summary>The page URL, or the GitHub owner/name.</summary>
    public string? UpstreamSource { get; init; }

    /// <summary>For <see cref="UpstreamKind.GitHubAssets"/>: the release tag.</summary>
    public string? UpstreamTag { get; init; }

    /// <summary>Group 1 is a version: on the page, in the asset names, or in the release tag (skips other tags).</summary>
    public Regex? UpstreamPattern { get; init; }

    public string? ReleaseNotes { get; init; }

    /// <summary>The version the Dockerfile installed (SPECARR_PIN_FFPROBE...). Empty outside the image.</summary>
    public string? Pinned => Environment.GetEnvironmentVariable($"SPECARR_PIN_{Id.ToUpperInvariant()}") is { Length: > 0 } pin ? pin : null;
}

public static partial class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new()
        {
            Id = "ffprobe", Name = "ffprobe", Purpose = "Reads every stream of each library file for the index (BtbN static build).",
            Command = MediaProbe.FfprobeExecutable, VersionArguments = ["-version"],
            VersionPattern = FfprobeVersion(),
            Upstream = UpstreamKind.GitHubAssets,
            UpstreamSource = "BtbN/FFmpeg-Builds", UpstreamTag = "latest",
            UpstreamPattern = BtbNBranch(),
            ReleaseNotes = "https://ffmpeg.org/download.html#releases"
        },
        new()
        {
            Id = "mediainfo", Name = "MediaInfo", Purpose = "Adds the encoder library (x264, x265) and bit rates, used for the source guess.",
            Command = MediaProbe.MediaInfoExecutable, VersionArguments = ["--version"],
            VersionPattern = VPrefixedVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSource = "MediaArea/MediaInfo",
            UpstreamPattern = PlainVersion(),
            ReleaseNotes = "https://mediaarea.net/MediaInfo/ChangeLog"
        },
        new()
        {
            Id = "dovi_tool", Name = "dovi_tool", Purpose = "Reads the Dolby Vision RPU: profile, FEL or MEL, CM version, L1 and L6.",
            Command = DoviTool.Executable, VersionArguments = ["--version"],
            VersionPattern = ThreePartVersion(),
            Upstream = UpstreamKind.GitHubRelease,
            UpstreamSource = "quietvoid/dovi_tool",
            UpstreamPattern = PlainVersion(),
            ReleaseNotes = "https://github.com/quietvoid/dovi_tool/releases"
        }
    ];

    /// <summary>The installed version in a tool's own output.</summary>
    public static string? InstalledVersion(ToolDefinition tool, string output) =>
        tool.VersionPattern.Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The highest version the pattern finds in a text.</summary>
    public static string? Highest(Regex pattern, string text) =>
        pattern.Matches(text)
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();

    /// <summary>The tag of a GitHub latest-release answer, without its "v".</summary>
    public static string? GitHubTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString()?.TrimStart('v', 'V') : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The asset names of a GitHub release answer, one per line.</summary>
    public static string GitHubAssetNames(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return "";
            }

            return string.Join('\n', assets.EnumerateArray()
                .Select(a => a.TryGetProperty("name", out var name) ? name.GetString() : null)
                .OfType<string>());
        }
        catch (JsonException)
        {
            return "";
        }
    }

    // BtbN names its release branch builds ffmpeg-n9.0-latest-linux64-gpl-9.0.tar.xz.
    [GeneratedRegex(@"ffmpeg-n(\d+\.\d+)-latest-linux64-gpl-\d")]
    private static partial Regex BtbNBranch();

    [GeneratedRegex(@"\bv(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VPrefixedVersion();

    [GeneratedRegex(@"^(\d+\.\d+(?:\.\d+)?)$")]
    private static partial Regex PlainVersion();

    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex ThreePartVersion();

    [GeneratedRegex(@"version n?(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex FfprobeVersion();
}

/// <summary>Compares "2.3.4" and "2.3.10" by their numbers, not as text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[] Parts(string? version) =>
        (version ?? "").Split('.', '-', '+')
        .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .ToArray();
}
