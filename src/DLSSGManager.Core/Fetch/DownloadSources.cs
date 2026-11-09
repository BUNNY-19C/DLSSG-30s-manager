namespace DLSSGManager;

/// <summary>Which payload a download should produce.</summary>
public enum PayloadPackage
{
    /// <summary>The full package first, the frame-generation files as the fallback (first run, command line).</summary>
    Any,

    /// <summary>The release archive, including the INT8 super-resolution kernels.</summary>
    Full,

    /// <summary>Frame generation and XeSS only, fetched file by file from the repository.</summary>
    Base,
}

/// <summary>
/// A download endpoint.
///
/// <paramref name="Id"/> is a language-independent identifier used for selection and filtering; the
/// display name is resolved from the string table on demand, so it follows the interface language.
/// <paramref name="Official"/> marks GitHub-operated endpoints. It only affects ordering and wording:
/// content from every source must match the release manifest.
/// </summary>
internal sealed record DownloadSource(string Id, string NameKey, bool Official, string UrlTemplate, bool IsArchive)
{
    public string Name => Loc.T(NameKey);

    /// <summary>Archives are the release attachment; per-file sources read the repository, which has no SR files.</summary>
    public PayloadPackage Package => IsArchive ? PayloadPackage.Full : PayloadPackage.Base;

    /// <summary>Builds a concrete address from the template.</summary>
    public Uri UrlFor(string repo, string reference, string? path) => new(UrlTemplate
        .Replace("{repo}", repo, StringComparison.Ordinal)
        .Replace("{ref}", reference, StringComparison.Ordinal)
        .Replace("{path}", path ?? "", StringComparison.Ordinal));
}

/// <summary>The endpoints a release can be fetched from, and how a request picks among them.</summary>
internal static class DownloadSources
{
    public const string RepoPath = "sdli1995/dlssg_for_sm86";

    /// <summary>Id meaning "try every source in turn"; see <see cref="ModFetcher.DownloadIntoAsync"/>.</summary>
    public const string AutoSourceId = "auto";

    /// <summary>
    /// Environment variable naming a single source to use, for diagnosing one endpoint in isolation.
    /// An unknown value falls back to trying every source, so a typo cannot silently disable downloading.
    /// </summary>
    public const string SourceFilterVariable = "DLSSGMANAGER_SOURCE";

    public static readonly DownloadSource[] All =
    {
        // Only the Release attachment includes the optional INT8 super-resolution payload.
        new("release", "Fetch.SourceRelease", true,
            "https://github.com/{repo}/releases/download/{ref}/dlssg-release-{ref}-x64.zip", true),
        new("release-proxy", "Fetch.SourceReleaseProxy", false,
            "https://ghfast.top/https://github.com/{repo}/releases/download/{ref}/dlssg-release-{ref}-x64.zip", true),
        // SourceForge's automatic mirror of the repository's releases (project "dlssg-for-sm86.mirror").
        new("sourceforge", "Fetch.SourceSourceForge", false,
            "https://downloads.sourceforge.net/project/dlssg-for-sm86.mirror/{ref}/dlssg-release-{ref}-x64.zip", true),

        // Per-file sources read the repository at the supported tag; they have no SR files. Every source
        // uses the same tag, so mirrors cannot mix a newer INI with older DLLs.
        new("raw", "Fetch.SourceRaw", true,
            "https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),
        // Chinese acceleration proxy; measured fastest of the mirrors here.
        new("ghproxy", "Fetch.SourceGhProxy", false,
            "https://gh-proxy.com/https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),
        // Public CDN, usually reachable where GitHub is not.
        new("jsdelivr", "Fetch.SourceJsDelivr", false,
            "https://cdn.jsdelivr.net/gh/{repo}@{ref}/{path}", false),
        // Another Chinese proxy; raw files only (403 for archives), so it is kept last.
        new("ghfast", "Fetch.SourceGhFast", false,
            "https://ghfast.top/https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),
    };

    /// <summary>An official per-file endpoint, used for probes that fetch a single small file.</summary>
    public static DownloadSource OfficialFile => All.First(s => !s.IsArchive && s.Official);

    /// <summary>The sources able to deliver <paramref name="package"/>, in their usual order.</summary>
    public static IReadOnlyList<DownloadSource> For(PayloadPackage package) =>
        package == PayloadPackage.Any ? All : All.Where(s => s.Package == package).ToArray();

    public static bool IsExplicitChoice(string? sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) &&
        !string.Equals(sourceId, AutoSourceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sources to attempt, in order. An explicit <paramref name="sourceId"/> limits the attempt to that
    /// source — a deliberate choice should not silently fall back to somewhere the user did not pick.
    /// Otherwise <see cref="SourceFilterVariable"/> may narrow it for diagnostics, and failing that every
    /// source able to deliver the package is tried.
    /// </summary>
    public static IReadOnlyList<DownloadSource> Active(string? sourceId, PayloadPackage package)
    {
        if (IsExplicitChoice(sourceId))
        {
            var chosen = All.Where(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count > 0) return chosen;
            AppPaths.Log(Loc.T("Fetch.UnknownFilter", sourceId, string.Join(" | ", All.Select(s => s.Id))));
        }

        var candidates = For(package);
        var filter = Environment.GetEnvironmentVariable(SourceFilterVariable);
        if (string.IsNullOrWhiteSpace(filter)) return candidates;

        var matched = candidates.Where(s => s.Id.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count > 0) return matched;

        AppPaths.Log(Loc.T("Fetch.UnknownFilter", filter, string.Join(" | ", candidates.Select(s => s.Id))));
        return candidates;
    }

    /// <summary>
    /// Orders source ids for automatic mode: sources that answered their speed test, fastest first, then
    /// the rest in their usual order. A failed probe does not drop a source — the probe is one short
    /// sample, and the full download may well get through where it did not.
    /// </summary>
    public static IReadOnlyList<string> RankIds(IReadOnlyList<string> usualOrder, IReadOnlyList<ModFetcher.SourceProbe> probes)
    {
        var byId = new Dictionary<string, ModFetcher.SourceProbe>(StringComparer.OrdinalIgnoreCase);
        foreach (var probe in probes) byId.TryAdd(probe.SourceId, probe);

        return usualOrder
            .Select((id, index) => (Id: id, Index: index, Probe: byId.GetValueOrDefault(id)))
            .OrderBy(x => x.Probe is { Reachable: true } ? 0 : 1)
            .ThenByDescending(x => x.Probe is { Reachable: true } p ? p.BytesPerSecond : 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Id)
            .ToArray();
    }

    /// <summary>
    /// Ranks within each package, the full package first, so automatic mode never settles for the
    /// frame-generation files while the full package is reachable.
    /// </summary>
    public static IReadOnlyList<DownloadSource> Rank(IReadOnlyList<DownloadSource> sources, IReadOnlyList<ModFetcher.SourceProbe> probes) =>
        sources.GroupBy(s => s.Package)
            .OrderBy(g => g.Key == PayloadPackage.Full ? 0 : 1)
            .SelectMany(g => RankIds(g.Select(s => s.Id).ToArray(), probes).Select(id => g.First(s => s.Id == id)))
            .ToArray();
}
