using System.Text.Json;
using System.Text.RegularExpressions;
using MeetJoe.Contracts;
using Microsoft.Extensions.Options;
using NuGet.Versioning;

namespace MeetJoe.Api.Updates;

public sealed class DesktopUpdatesOptions
{
    public const string SectionName = "DesktopUpdates";

    /// <summary>Folder containing per-RID Velopack release directories (e.g. releases/desktop).</summary>
    public string RootPath { get; set; } = "releases/desktop";

    /// <summary>Public URL path prefix mapped to RootPath (must match static file mount).</summary>
    public string UrlPath { get; set; } = "/updates/desktop";

    public string Channel { get; set; } = "stable";
}

public interface IDesktopUpdateFeed
{
    DesktopUpdateInfoDto? GetLatest(string rid, HttpRequest request);
    IReadOnlyList<string> ListRids();
}

public sealed partial class DesktopUpdateFeed(IOptions<DesktopUpdatesOptions> options, IHostEnvironment env) : IDesktopUpdateFeed
{
    public IReadOnlyList<string> ListRids()
    {
        var root = ResolveRoot();
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public DesktopUpdateInfoDto? GetLatest(string rid, HttpRequest request)
    {
        if (string.IsNullOrWhiteSpace(rid) || !ValidRid().IsMatch(rid)) return null;

        var root = ResolveRoot();
        var folder = Path.Combine(root, rid);
        if (!Directory.Exists(folder)) return null;

        var version = FindLatestSemVer(folder);
        if (version is null) return null;

        var opts = options.Value;
        var baseUri = $"{request.Scheme}://{request.Host}{opts.UrlPath.TrimEnd('/')}/{rid}/";
        var notesPath = Path.Combine(folder, "RELEASE_NOTES.md");
        string? notes = File.Exists(notesPath) ? File.ReadAllText(notesPath) : null;
        var published = Directory.EnumerateFiles(folder, "*.nupkg")
            .Select(f => (DateTimeOffset?)File.GetLastWriteTimeUtc(f))
            .DefaultIfEmpty()
            .Max();

        return new DesktopUpdateInfoDto(
            version.ToNormalizedString(),
            opts.Channel,
            rid,
            baseUri,
            notes,
            Mandatory: false,
            published);
    }

    private string ResolveRoot()
    {
        var configured = options.Value.RootPath;
        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
    }

    private static NuGetVersion? FindLatestSemVer(string folder)
    {
        foreach (var json in Directory.EnumerateFiles(folder, "releases*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                var versions = new List<NuGetVersion>();
                CollectVersions(doc.RootElement, versions);
                if (versions.Count > 0) return versions.Max();
            }
            catch (JsonException)
            {
                // Fall through to nupkg file names.
            }
        }

        NuGetVersion? best = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*.nupkg"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var match = NupkgVersion().Match(name);
            if (!match.Success) continue;
            if (!NuGetVersion.TryParse(match.Groups[1].Value, out var ver)) continue;
            if (best is null || ver > best) best = ver;
        }

        return best;
    }

    private static void CollectVersions(JsonElement element, List<NuGetVersion> versions)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if ((prop.NameEquals("Version") || prop.NameEquals("PackageVersion") || prop.NameEquals("version"))
                        && prop.Value.ValueKind == JsonValueKind.String
                        && NuGetVersion.TryParse(prop.Value.GetString(), out var ver))
                    {
                        versions.Add(ver);
                    }
                    else
                    {
                        CollectVersions(prop.Value, versions);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectVersions(item, versions);
                break;
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ValidRid();

    // MeetJoe-1.2.3-full / MeetJoe-1.2.3-delta
    [GeneratedRegex(@"-(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)-(?:full|delta)$", RegexOptions.CultureInvariant)]
    private static partial Regex NupkgVersion();
}
