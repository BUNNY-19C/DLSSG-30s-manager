using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;

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
/// Obtains the dlssg_for_sm86 payload from any of several mirrors.
///
/// Security model
/// ---------------
/// The files are native DLLs that end up in game directories, so the download path is treated as
/// hostile:
///
/// · Every request is HTTPS, on an allow-listed host whose resolved addresses must all be public.
///   Redirects are followed manually with the same checks at each hop.
/// · Response size is capped, and archive entries may not escape the destination folder.
/// · The payload is verified after download: every proxy DLL must carry a valid Authenticode
///   signature. For third-party mirrors the signer must also match a pinned certificate
///   thumbprint, because the mirror is not the authority for the content. For GitHub's own
///   endpoints the certificate is logged but not required to match, so a future certificate
///   rotation upstream does not break downloads.
///
/// Sources are tried in order of authority and efficiency, so a blocked or flaky endpoint only
/// costs a fallback rather than a failed download.
/// </summary>
public static class ModFetcher
{
    /// <summary>Hosts the downloader may ever contact.</summary>
    private static readonly string[] AllowedHosts =
    {
        // GitHub-operated
        "github.com",
        "codeload.github.com",
        "raw.githubusercontent.com",
        "api.github.com",
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        // Third-party mirrors. Accepted only with a matching certificate pin, because a mirror is not
        // the authority for this content — see Verify().
        "cdn.jsdelivr.net",
        "gh-proxy.com",
        "ghfast.top",
        "downloads.sourceforge.net",
    };

    /// <summary>
    /// Host families accepted by suffix. SourceForge's redirector hands each request to one of its
    /// regional mirrors (<c>master.dl.sourceforge.net</c>, <c>netix.dl.sourceforge.net</c>, …), so the
    /// final host cannot be listed in advance. The match is on a whole-label boundary.
    /// </summary>
    private static readonly string[] AllowedHostSuffixes = { ".dl.sourceforge.net" };

    private static bool IsAllowedHost(Uri uri) =>
        uri.HostNameType == UriHostNameType.Dns &&
        (AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) ||
         AllowedHostSuffixes.Any(suffix => uri.Host.Length > suffix.Length &&
                                           uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Certificate the project signs every proxy DLL with, as 0.3.0 publishes it in its own README:
    /// <c>CN=DLSSG for SM86 (self-signed)</c>, SHA-1 85BA66762F851E49148D706915D09026281418E6. The
    /// previous pin (A994735E…) belonged to the 0.2.x release line, whose files are no longer part of the
    /// payload.
    ///
    /// A mismatch on a mirror means the file is not the project's build, so the download is rejected. A
    /// mismatch on GitHub's own endpoints is logged as a warning instead, so upstream rotating its
    /// self-signed certificate does not brick the updater.
    /// </summary>
    private const string PinnedCertThumbprint = "85BA66762F851E49148D706915D09026281418E6";

    /// <summary>Signer subject fragment used as a secondary sanity check on any source.</summary>
    private const string ExpectedSignerSubject = "DLSSG";

    private const long MaxArchiveBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Ceiling on what an archive may expand to, counted from the bytes actually decompressed. The
    /// full 0.4.0 package expands to a few hundred MB; a zip bomb would not stop there.
    /// </summary>
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;

    private const int MaxArchiveEntries = 4096;

    /// <summary>A source that sends nothing for this long is abandoned, so the next one gets its turn.</summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);

    /// <summary>How many times one file is continued from where a dropped connection left it.</summary>
    private const int MaxResumes = 3;

    /// <summary>
    /// Speed-test sample: a ranged read of this many bytes, bounded by <see cref="ProbeTimeout"/>.
    /// Enough to get past connection setup, small enough that testing every source costs a few MB.
    /// </summary>
    private const int ProbeBytes = 512 * 1024;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>File a per-file source is timed with: large, and present in every release of the layout.</summary>
    private const string ProbeFile = "dlssg_sm86/kernels/fg_kernels.dll";

    private const string RepoPath = "sdli1995/dlssg_for_sm86";
    public const string SupportedRelease = "0.4.0";
    private const string RepoRef = SupportedRelease;

    /// <summary>
    /// File the downloader leaves in the mod folder to name the release it fetched. The shipped INI
    /// stopped carrying a version banner in 0.3.0, so this is what the interface badge reads.
    /// </summary>
    public const string VersionMarkerName = ".manager-version";

    private sealed record Artifact(string SourcePath, string DestinationPath, bool Required, bool NeedsSignature, bool Nvidia = false);

    /// <summary>
    /// The payload the manager consumes, with the checks each file needs.
    ///
    /// Source and destination are separate because upstream reorganises between releases: 0.3.0 moved the
    /// alternate entry points from <c>altnative/</c> to <c>alternatives/</c>, dropped <c>winhttp.dll</c>
    /// and added <c>d3d12.dll</c> and <c>dbghelp.dll</c>. Keeping the local layout fixed means an existing
    /// install keeps working and only the download paths have to follow upstream.
    /// </summary>
    private static readonly Artifact[] Payload =
    {
        new("version.dll", "version.dll", true, true),
        new("dlssg_sm86.ini", "dlssg_sm86.ini", true, false),
        new("alternatives/winmm.dll", "altnative/winmm.dll", true, true),
        new("alternatives/dinput8.dll", "altnative/dinput8.dll", true, true),
        new("alternatives/dbghelp.dll", "altnative/dbghelp.dll", true, true),
        new("alternatives/dxgi.dll", "altnative/dxgi.dll", true, true),
        new("alternatives/d3d12.dll", "altnative/d3d12.dll", true, true),
        new("dlssg_sm86/backend/sm86_backend.dll", "dlssg_sm86/backend/sm86_backend.dll", true, true),
        new("dlssg_sm86/runtime/nvngx_dlssg.dll", "dlssg_sm86/runtime/nvngx_dlssg.dll", true, true, true),
        new("dlssg_sm86/kernels/fg_kernels.dll", "dlssg_sm86/kernels/fg_kernels.dll", true, true),
        new("dlssg_sm86/xefg/xefg_kernels.dll", "dlssg_sm86/xefg/xefg_kernels.dll", true, true),
        new("dlssg_sm86/sr/nvngx_dlss.dll", "dlssg_sm86/sr/nvngx_dlss.dll", false, true, true),
        new("dlssg_sm86/sr/sr_kernels.dll", "dlssg_sm86/sr/sr_kernels.dll", false, true),
        new("README.md", "README.md", false, false),
        new("THIRD_PARTY_NOTICES.txt", "THIRD_PARTY_NOTICES.txt", false, false),
    };

    /// <summary>
    /// Community builds this manager recognises by hash but no longer distributes.
    ///
    /// The community <c>d3d12.dll</c> predates upstream shipping its own d3d12 entry, and people who
    /// installed it by hand still have it in a game folder. A hash is the only proof available for a file
    /// carrying no certificate this project can verify, and without it such an install looks undeployed
    /// and can never be adopted or cleaned up. Recognising it costs nothing; distributing it would mix a
    /// pre-0.3.0 build with a 0.3.0 INI, which is why the download is gone.
    /// </summary>
    public static IReadOnlyList<string> KnownCommunityBuildHashes { get; } = new[]
    {
        "65E6F912F5D485DC56BC6B48430DF046FF06D38B8A69595B42E316E9644F7C2B",
    };

    /// <summary>True when the file's bytes match a community build this manager still recognises.</summary>
    public static bool IsKnownCommunityBuild(string path) =>
        KnownCommunityBuildHashes.Any(hash => MatchesPin(path, hash));

    /// <summary>
    /// A download endpoint.
    ///
    /// <paramref name="Id"/> is a language-independent identifier used for selection and filtering;
    /// the display name is resolved from the string table on demand, so it follows the interface
    /// language. Matching a filter against a localised name would stop working the moment the user
    /// switched language.
    ///
    /// <paramref name="Official"/> marks GitHub-operated sources, where TLS to the repository is
    /// itself the authority and the certificate pin is advisory.
    /// </summary>
    private sealed record Source(string Id, string NameKey, bool Official, string UrlTemplate, bool IsArchive)
    {
        public string Name => Loc.T(NameKey);

        /// <summary>Archives are the release attachment; per-file sources read the repository, which has no SR files.</summary>
        public PayloadPackage Package => IsArchive ? PayloadPackage.Full : PayloadPackage.Base;
    }

    /// <summary>
    /// Address of the version probe: the published INI on the default branch, through an official
    /// per-file endpoint.
    /// </summary>
    private static string VersionProbeUrl => UrlFor(OfficialFileSource(), RepoPath, RepoRef, ModSource.IniName);

    /// <summary>An official per-file endpoint, used for probes that fetch a single small file.</summary>
    private static Source OfficialFileSource() => Sources.First(s => !s.IsArchive && s.Official);

    /// <summary>The upstream README, the second and last version probe.</summary>
    private static string ReadmeUrl => UrlFor(OfficialFileSource(), RepoPath, RepoRef, "README.md");

    /// <summary>
    /// Reads which release upstream is publishing, without downloading the payload.
    ///
    /// Two probes, cheapest first: the INI's own <c>; Native x.y.z.</c> banner while that exists, and
    /// otherwise the version in the README's title (0.3.0 dropped the banner and moved the number there).
    /// Advisory only: the download goes through the normal source chain, and a failed probe returns null,
    /// because a blocked endpoint must never be the reason a first run cannot get its files.
    /// </summary>
    public static async Task<string?> DetectLatestVersionAsync(CancellationToken ct)
    {
        foreach (var probe in new[] { VersionProbeUrl, ReadmeUrl })
        {
            try
            {
                using var client = CreateClient();
                using var response = await GetCheckedAsync(client, new Uri(probe), ct).ConfigureAwait(false);

                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var version = ModSource.ReadVersionFromText(text) ?? ReadVersionFromReadme(text);
                if (version is not null) return version;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppPaths.Log("探测上游版本失败（" + probe + "）: " + ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// Pulls the release number out of the README's title, e.g. "# DLSSG for SM86（Proxy）- 0.3.0 版本".
    /// Only the first heading is searched: the dotted numbers further down are about the bundled runtime
    /// generations (310.9, 310.1) and are not the release number.
    /// </summary>
    private static string? ReadVersionFromReadme(string text)
    {
        foreach (var line in text.Split('\n').Take(10))
        {
            if (!line.StartsWith('#')) continue;

            var m = System.Text.RegularExpressions.Regex.Match(line, @"([0-9]+\.[0-9]+(?:\.[0-9]+)+)");
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// True when the file's bytes match a published community build.
    ///
    /// A mismatch means the bytes do not identify the published build.
    /// </summary>
    public static bool MatchesPin(string path, string sha256)
    {
        try
        {
            return string.Equals(DeploymentService.Sha256(path), sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static readonly Source[] Sources =
    {
        // Only the Release attachment includes the optional INT8 super-resolution payload.
        new("release", "Fetch.SourceRelease", true,
            "https://github.com/{repo}/releases/download/{ref}/dlssg-release-{ref}-x64.zip", true),
        new("release-proxy", "Fetch.SourceReleaseProxy", false,
            "https://ghfast.top/https://github.com/{repo}/releases/download/{ref}/dlssg-release-{ref}-x64.zip", true),
        // SourceForge's automatic mirror of the repository's releases (project "dlssg-for-sm86.mirror"):
        // the same attachment, served from its own CDN. A mirror, so the certificate pin is enforced.
        new("sourceforge", "Fetch.SourceSourceForge", false,
            "https://downloads.sourceforge.net/project/dlssg-for-sm86.mirror/{ref}/dlssg-release-{ref}-x64.zip", true),
        // Templates carry {repo}/{ref}/{path} rather than a baked-in repository, so the list can serve
        // more than one repository without duplicating every endpoint.
        //
        // Per-file fallbacks omit SR, whose kernel pack is too large for a repository blob.
        // All sources use the same supported tag, so mirrors cannot mix newer INI and older DLLs.

        // Per-file raw access: the authority itself, exact bytes, no cache between us and the repo.
        new("raw", "Fetch.SourceRaw", true,
            "https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),

        // Chinese acceleration proxy. It forwards both raw files and codeload archives, and measured
        // fastest of the mirrors here (a 15 MB file in under a second), so it is tried next.
        new("ghproxy", "Fetch.SourceGhProxy", false,
            "https://gh-proxy.com/https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),

        // Public CDN, usually reachable where GitHub is not.
        new("jsdelivr", "Fetch.SourceJsDelivr", false,
            "https://cdn.jsdelivr.net/gh/{repo}@{ref}/{path}", false),

        // Another Chinese proxy. Verified for raw files only — it returns 403 for codeload archives,
        // so it is per-file like the two above and kept last of the mirrors.
        new("ghfast", "Fetch.SourceGhFast", false,
            "https://ghfast.top/https://raw.githubusercontent.com/{repo}/{ref}/{path}", false),

    };

    /// <summary>Builds a concrete address from a source template.</summary>
    private static string UrlFor(Source source, string repo, string reference, string? path) =>
        source.UrlTemplate
            .Replace("{repo}", repo, StringComparison.Ordinal)
            .Replace("{ref}", reference, StringComparison.Ordinal)
            .Replace("{path}", path ?? "", StringComparison.Ordinal);

    /// <summary>
    /// A source as presented to the user: a stable id to pass back, plus text in the active language.
    /// </summary>
    public sealed record SourceOption(string Id, string Name, string Note, bool Official, PayloadPackage Package);

    /// <summary>
    /// The sources a user may choose from, in the order they would be tried. Notes explain what each
    /// one is for, since the difference between them is not obvious from the name.
    /// </summary>
    public static IReadOnlyList<SourceOption> AvailableSources => Sources
        .Select(s => new SourceOption(s.Id, s.Name, Loc.T($"Fetch.Note.{s.Id}"), s.Official, s.Package))
        .ToArray();

    /// <summary>The sources able to deliver <paramref name="package"/>, in their usual order.</summary>
    private static IReadOnlyList<Source> SourcesFor(PayloadPackage package) =>
        package == PayloadPackage.Any ? Sources : Sources.Where(s => s.Package == package).ToArray();

    /// <summary>
    /// Times every source able to deliver <paramref name="package"/>, all at once: a ranged read of
    /// the release archive, or of one large payload file for per-file sources. Never throws for a
    /// source that fails; the result says so instead.
    /// </summary>
    public static async Task<IReadOnlyList<SourceProbe>> ProbeSourcesAsync(PayloadPackage package, CancellationToken ct) =>
        await Task.WhenAll(SourcesFor(package).Select(s => ProbeAsync(s, ct))).ConfigureAwait(false);

    private static async Task<SourceProbe> ProbeAsync(Source source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var url = new Uri(UrlFor(source, RepoPath, RepoRef, source.IsArchive ? null : ProbeFile));
        var watch = Stopwatch.StartNew();
        var latency = TimeSpan.Zero;

        try
        {
            using var client = CreateClient();
            using var response = await GetCheckedAsync(client, url, timeout.Token, 0, ProbeBytes - 1).ConfigureAwait(false);
            latency = watch.Elapsed;
            var total = TotalLength(response) ?? -1;

            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            long read = 0;
            try
            {
                int n;
                while (read < ProbeBytes && (n = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    read += n;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && read > 0)
            {
                // Out of time mid-sample: what did arrive still measures the rate.
            }

            var seconds = Math.Max((watch.Elapsed - latency).TotalSeconds, 0.001);
            return new SourceProbe(source.Id, true, read / seconds, latency, total, "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SourceProbe(source.Id, false, 0, latency, -1, Loc.T("Fetch.ProbeTimeout", (int)ProbeTimeout.TotalSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SourceProbe(source.Id, false, 0, latency, -1, ex.Message);
        }
    }

    /// <summary>
    /// Orders source ids for automatic mode: sources that answered their speed test, fastest first,
    /// then the rest in their usual order. A failed probe does not drop a source — the probe is one
    /// short sample, and the full download may well get through where it did not.
    /// </summary>
    public static IReadOnlyList<string> RankSourceIds(IReadOnlyList<string> usualOrder, IReadOnlyList<SourceProbe> probes)
    {
        var byId = new Dictionary<string, SourceProbe>(StringComparer.OrdinalIgnoreCase);
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
    private static IReadOnlyList<Source> Rank(IReadOnlyList<Source> sources, IReadOnlyList<SourceProbe> probes) =>
        sources.GroupBy(s => s.Package)
            .OrderBy(g => g.Key == PayloadPackage.Full ? 0 : 1)
            .SelectMany(g => RankSourceIds(g.Select(s => s.Id).ToArray(), probes)
                .Select(id => g.First(s => s.Id == id)))
            .ToArray();

    /// <summary>Id used to mean "try every source in turn"; see <see cref="DownloadIntoAsync"/>.</summary>
    public const string AutoSourceId = "auto";

    /// <summary>
    /// Environment variable naming a single source to use, for diagnosing one endpoint in isolation.
    /// Matched against <see cref="Source.Id"/>; an unknown value falls back to trying every source, so
    /// a typo cannot silently disable downloading.
    /// </summary>
    public const string SourceFilterVariable = "DLSSGMANAGER_SOURCE";

    /// <summary>
    /// Sources to attempt, in order. An explicit <paramref name="sourceId"/> (from the picker) limits
    /// the attempt to that one source — a deliberate choice should not silently fall back to somewhere
    /// the user did not pick. Otherwise <see cref="SourceFilterVariable"/> may narrow it for
    /// diagnostics, and failing that every source is tried in turn.
    /// </summary>
    private static IReadOnlyList<Source> ActiveSources(string? sourceId, PayloadPackage package)
    {
        if (!string.IsNullOrWhiteSpace(sourceId) &&
            !string.Equals(sourceId, AutoSourceId, StringComparison.OrdinalIgnoreCase))
        {
            var chosen = Sources.Where(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count > 0) return chosen;

            AppPaths.Log(Loc.T("Fetch.UnknownFilter", sourceId,
                string.Join(" | ", Sources.Select(s => s.Id))));
        }

        var candidates = SourcesFor(package);
        var filter = Environment.GetEnvironmentVariable(SourceFilterVariable);
        if (string.IsNullOrWhiteSpace(filter)) return candidates;

        var matched = candidates.Where(s => s.Id.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            AppPaths.Log(Loc.T("Fetch.UnknownFilter", filter,
                string.Join(" | ", candidates.Select(s => s.Id))));
            return candidates;
        }

        return matched;
    }

    /// <summary>
    /// Whether a request may be sent: HTTPS only, on an allow-listed host whose resolved addresses
    /// must all be public. See the two-argument overload for the proxy exception.
    /// </summary>
    public static bool IsAllowedAddress(Uri uri) => IsAllowedAddress(uri, ResolveProxyRouted(uri));

    /// <summary>
    /// The policy with the proxy state made explicit.
    /// </summary>
    /// <param name="proxyRouted">
    /// True when a system proxy will carry the request. The resolved-address check guards against
    /// being steered at an internal network; with a proxy the connection goes to the proxy, not to
    /// whatever this machine's resolver answers — and on networks where GitHub's domains resolve to
    /// loopback or 0.0.0.0 (poisoned DNS, hosts-file accelerators), the strict check rejected every
    /// source even though the download itself would have worked. The scheme check, the host
    /// allow-list and the payload signature checks all still apply.
    /// </param>
    public static bool IsAllowedAddress(Uri uri, bool proxyRouted)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsAllowedHost(uri)) return false;

        if (proxyRouted) return true;

        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses(uri.Host); }
        catch { return false; }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    /// <summary>
    /// True when the system proxy will carry this request — the same proxy <c>HttpClientHandler</c>
    /// uses by default, which is what makes the check meaningful. If the proxy API is unavailable,
    /// the strict path is kept.
    /// </summary>
    private static bool ResolveProxyRouted(Uri uri)
    {
        try
        {
            var target = WebRequest.DefaultWebProxy?.GetProxy(uri);
            return target is not null &&
                   !string.Equals(target.GetLeftPart(UriPartial.Authority),
                                  uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPublicAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return IsPublicAddress(ip.MapToIPv4());
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Loopback)) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;                 // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return false;  // fe80::/10 link local
            if (b[0] == 0xFF) return false;                           // ff00::/8 multicast
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false; // 2001:db8::/32 doc
            return true;
        }

        var o = ip.GetAddressBytes();
        return o[0] switch
        {
            0 => false,                                     // 0.0.0.0/8
            10 => false,                                    // private
            127 => false,                                   // loopback
            169 when o[1] == 254 => false,                  // link local
            172 when o[1] >= 16 && o[1] <= 31 => false,     // private
            192 when o[1] == 168 => false,                  // private
            192 when o[1] == 0 && o[2] == 0 => false,       // 192.0.0.0/24
            192 when o[1] == 0 && o[2] == 2 => false,       // TEST-NET-1
            198 when o[1] == 18 || o[1] == 19 => false,     // benchmarking
            198 when o[1] == 51 && o[2] == 100 => false,    // TEST-NET-2
            203 when o[1] == 0 && o[2] == 113 => false,     // TEST-NET-3
            100 when o[1] >= 64 && o[1] <= 127 => false,    // CGNAT
            >= 224 => false,                                // multicast + reserved
            _ => true,
        };
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DLSSGManager/1.0");
        return client;
    }

    /// <summary>
    /// Follows at most a handful of hops, re-validating every target. A range, when given, is sent
    /// on every hop: the redirect target is where the bytes come from.
    /// </summary>
    private static async Task<HttpResponseMessage> GetCheckedAsync(HttpClient client, Uri uri, CancellationToken ct,
        long? rangeFrom = null, long? rangeTo = null)
    {
        if (!IsAllowedAddress(uri)) throw new InvalidOperationException(Loc.T("Fetch.UrlRejected", uri));

        var current = uri;
        for (var hop = 0; hop < 5; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            if (rangeFrom is not null) request.Headers.Range = new RangeHeaderValue(rangeFrom, rangeTo);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidOperationException(Loc.T("Fetch.RedirectNoTarget"));
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!IsAllowedAddress(current)) throw new InvalidOperationException(Loc.T("Fetch.RedirectRejected", current));
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                // Dispose before throwing: EnsureSuccessStatusCode would leak the response, and the
                // status code in the message tells the retry log which endpoint answered how.
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new InvalidOperationException(Loc.T("Fetch.HttpFailed", current, code));
            }

            return response;
        }

        throw new InvalidOperationException(Loc.T("Fetch.RedirectTooMany"));
    }

    /// <summary>Size of the whole file: from Content-Range for a partial answer, Content-Length otherwise.</summary>
    private static long? TotalLength(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.PartialContent
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;

    /// <summary>
    /// Streams one address into a file. Nothing arriving for <see cref="StallTimeout"/> fails the
    /// transfer, so a source that has gone quiet hands over to the next one. A connection dropped
    /// part-way — including during a long pause — is continued with a ranged request from the byte it
    /// reached, up to <see cref="MaxResumes"/> times; a server that ignores the range is read again
    /// from the start rather than spliced.
    /// </summary>
    /// <param name="report">Bytes written, total size (-1 when unknown), smoothed rate.</param>
    /// <returns>The number of bytes written.</returns>
    private static async Task<long> TransferAsync(HttpClient client, Uri url, string target, long maxBytes,
        Action<long, long, double>? report, DownloadGate? gate, CancellationToken ct)
    {
        long written = 0;
        long total = -1;
        var resumes = 0;
        var meter = new SpeedMeter();
        var buffer = new byte[81920];

        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        while (true)
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                stall.CancelAfter(StallTimeout);
                using var response = await GetCheckedAsync(client, url, stall.Token, written > 0 ? written : null)
                    .ConfigureAwait(false);

                if (written > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        written = 0;
                        output.SetLength(0);
                        output.Position = 0;
                    }
                    else if (range?.From != written)
                    {
                        throw new InvalidOperationException(Loc.T("Fetch.RangeMismatch", url.Host));
                    }
                }

                total = TotalLength(response) ?? total;
                if (total > maxBytes) throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge", total / 1024 / 1024));

                await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                var lastReport = Stopwatch.GetTimestamp();
                while (true)
                {
                    if (gate?.Paused == true)
                    {
                        stall.CancelAfter(Timeout.InfiniteTimeSpan);
                        report?.Invoke(written, total, 0);
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        stall.CancelAfter(StallTimeout);
                    }

                    var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    stall.CancelAfter(StallTimeout);

                    written += read;
                    if (written > maxBytes) throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge2"));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    if (report is not null && Stopwatch.GetElapsedTime(lastReport).TotalMilliseconds >= 150)
                    {
                        lastReport = Stopwatch.GetTimestamp();
                        report(written, total, meter.Report(written));
                    }
                }

                // A connection closed early ends the read like a finished one; only the length tells them apart.
                if (total > 0 && written < total) throw new IOException(Loc.T("Fetch.Truncated", written, total));

                report?.Invoke(written, total > 0 ? total : written, meter.Report(written));
                return written;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && stall.IsCancellationRequested)
            {
                throw new TimeoutException(Loc.T("Fetch.Stalled", url.Host, (int)StallTimeout.TotalSeconds));
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException &&
                                       !ct.IsCancellationRequested && written > 0 && resumes < MaxResumes)
            {
                resumes++;
                AppPaths.Log(Loc.T("Fetch.Resuming", url.Host, written, ex.Message));
            }
        }
    }

    /// <summary>
    /// Expands an archive into <paramref name="staging"/>. Entries that would land outside it are
    /// refused, and extraction stops once the bytes actually decompressed pass
    /// <see cref="MaxExtractedBytes"/>, whatever sizes the archive declares. A single folder wrapping
    /// the payload (a zipped folder, a source archive) is flattened so staging looks like the payload root.
    /// </summary>
    private static void ExtractArchive(string archivePath, string staging, CancellationToken ct)
    {
        Directory.CreateDirectory(staging);
        var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(archivePath);
        if (zip.Entries.Count > MaxArchiveEntries)
            throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooManyEntries", MaxArchiveEntries));

        long extracted = 0;
        var buffer = new byte[81920];
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // A colon would address an alternate data stream; nothing in a payload needs one.
            if (entry.FullName.Contains(':')) throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));
            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                extracted += read;
                if (extracted > MaxExtractedBytes)
                    throw new InvalidOperationException(Loc.T("Fetch.ExtractTooLarge", MaxExtractedBytes / 1024 / 1024));
                output.Write(buffer, 0, read);
            }
        }

        FlattenWrapper(staging);
    }

    /// <summary>
    /// Lifts the payload out of the one folder that wraps it. Folders without the INI — such as the
    /// <c>__MACOSX</c> metadata macOS adds to a zip — are not candidates.
    /// </summary>
    private static void FlattenWrapper(string staging)
    {
        if (File.Exists(Path.Combine(staging, ModSource.IniName))) return;

        var wrappers = Directory.GetDirectories(staging)
            .Where(d => File.Exists(Path.Combine(d, ModSource.IniName)))
            .ToArray();
        if (wrappers.Length != 1) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));

        var inner = wrappers[0];
        foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
        {
            var target = Path.Combine(staging, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                if (Directory.Exists(target)) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));
                Directory.Move(entry, target);
            }
            else File.Move(entry, target, overwrite: true);
        }

        TryDeleteDirectory(inner);
    }

    /// <summary>
    /// A byte-level progress tick for the interface: which source, which file of the payload, how
    /// far through it, and the smoothed transfer rate. <see cref="BytesTotal"/> is -1 when the
    /// endpoint did not declare a length, so the bar can go indeterminate.
    /// </summary>
    public sealed record FetchProgress(string SourceName, int FileIndex, int FileTotal,
                                       long BytesDone, long BytesTotal, double SpeedBps,
                                       FetchPhase Phase = FetchPhase.Downloading);

    /// <summary>What a download is doing; anything past <see cref="Downloading"/> has no byte count.</summary>
    public enum FetchPhase
    {
        Probing,
        Downloading,
        Extracting,
        Verifying,
    }

    /// <summary>
    /// Pause and skip switches for a running download.
    ///
    /// Pausing stops reading inside the transfer loop. The connection is left open; if the server
    /// drops it during a long pause, the transfer continues with a ranged request from the byte it
    /// reached. Skipping abandons only the source in use, so automatic mode moves on to the next one.
    /// Cancellation stays with the caller's token and is immediate.
    /// </summary>
    public sealed class DownloadGate
    {
        private readonly object _sync = new();
        private TaskCompletionSource _resumed = Released();
        private CancellationTokenSource? _attempt;
        private bool _paused;

        public bool Paused
        {
            get { lock (_sync) return _paused; }
        }

        public void Pause()
        {
            lock (_sync)
            {
                if (_paused) return;
                _paused = true;
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource released;
            lock (_sync)
            {
                if (!_paused) return;
                _paused = false;
                released = _resumed;
            }
            released.TrySetResult();
        }

        /// <summary>Gives up on the source being used. Also releases a pause, so the next source starts at once.</summary>
        public void SkipCurrentSource()
        {
            CancellationTokenSource? attempt;
            lock (_sync) attempt = _attempt;
            try { attempt?.Cancel(); }
            catch (ObjectDisposedException) { /* the attempt finished in the meantime */ }
            Resume();
        }

        /// <summary>Completes at once while running; waits while paused.</summary>
        public Task WaitAsync(CancellationToken ct)
        {
            Task resumed;
            lock (_sync) resumed = _resumed.Task;
            return resumed.IsCompleted ? Task.CompletedTask : resumed.WaitAsync(ct);
        }

        /// <summary>A token for one source attempt: cancelled by the caller's token or by <see cref="SkipCurrentSource"/>.</summary>
        internal CancellationToken BeginAttempt(CancellationToken outer)
        {
            lock (_sync)
            {
                _attempt?.Dispose();
                _attempt = CancellationTokenSource.CreateLinkedTokenSource(outer);
                return _attempt.Token;
            }
        }

        internal void EndAttempt()
        {
            lock (_sync)
            {
                _attempt?.Dispose();
                _attempt = null;
            }
        }

        private static TaskCompletionSource Released()
        {
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            released.SetResult();
            return released;
        }
    }

    /// <summary>
    /// One source's speed test: whether it answered, how fast the sample arrived, and how large the
    /// probed file is (the whole package for archive sources, -1 when the server did not say).
    /// </summary>
    public sealed record SourceProbe(string SourceId, bool Reachable, double BytesPerSecond, TimeSpan Latency,
                                     long TotalBytes, string Error);

    /// <summary>
    /// Tracks a transfer rate over a half-second window, smoothed so the read-loop bursts do not make
    /// the displayed number jump.
    /// </summary>
    private sealed class SpeedMeter
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private long _lastBytes;
        private TimeSpan _lastTime;
        private double _bps;

        public double Report(long totalBytes)
        {
            var now = _watch.Elapsed;
            var dt = (now - _lastTime).TotalSeconds;
            if (dt >= 0.5)
            {
                var instant = (totalBytes - _lastBytes) / dt;
                _bps = _bps <= 0 ? instant : _bps * 0.4 + instant * 0.6;
                _lastBytes = totalBytes;
                _lastTime = now;
            }
            return _bps;
        }
    }

    /// <summary>Formats a transfer rate for display, e.g. "12.3 MB/s".</summary>
    public static string FormatSpeed(double bytesPerSecond) => FormatSize(bytesPerSecond) + "/s";

    /// <summary>Formats a byte count for display, e.g. "76.8 MB".</summary>
    public static string FormatSize(double bytes)
    {
        if (bytes <= 0) return "0 B";

        string[] units = { "B", "KB", "MB", "GB" };
        var value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = unit == 0 ? value.ToString("F0", CultureInfo.InvariantCulture)
                             : value.ToString("F1", CultureInfo.InvariantCulture);
        return text + " " + units[unit];
    }

    /// <summary>
    /// Downloads the payload.
    ///
    /// With <paramref name="sourceId"/> null or <see cref="AutoSourceId"/>, the sources for the package
    /// are speed-tested and tried fastest first until one yields a verified result; a source that
    /// fails, stalls or is skipped hands over to the next. With a specific id, only that source is
    /// used: the user chose it deliberately, and silently downloading from elsewhere would misreport
    /// where the files came from and defeat the point of choosing.
    /// </summary>
    /// <param name="byteProgress">Per-chunk transfer progress for the interface; may be null.</param>
    /// <param name="gate">Pause and skip switches; may be null (never pauses).</param>
    /// <param name="package">Which payload to fetch; decides which sources take part.</param>
    /// <param name="probes">
    /// Speed tests already run (the picker runs them while the user decides). In automatic mode without
    /// them, the sources are tested here first, which costs at most <see cref="ProbeTimeout"/>.
    /// </param>
    public static async Task<OpResult> DownloadIntoAsync(
        string destination,
        IProgress<string>? progress,
        CancellationToken ct,
        string? sourceId = null,
        IProgress<FetchProgress>? byteProgress = null,
        DownloadGate? gate = null,
        string runtimeModel = BuildCatalog.Default,
        PayloadPackage package = PayloadPackage.Any,
        IReadOnlyList<SourceProbe>? probes = null)
    {
        ct.ThrowIfCancellationRequested();
        if (BuildCatalog.Normalize(runtimeModel) != BuildCatalog.Default)
        {
            var unavailable = new OpResult();
            unavailable.Fail(Loc.T("Manage.RetiredBuild"));
            return unavailable;
        }
        var sources = ActiveSources(sourceId, package);
        var singleSource = sources.Count == 1 && IsExplicitChoice(sourceId);

        if (!singleSource && sources.Count > 1)
        {
            if (probes is null)
            {
                progress?.Report(Loc.T("Fetch.Probing"));
                byteProgress?.Report(new FetchProgress("", 0, 0, 0, -1, 0, FetchPhase.Probing));
                probes = await ProbeSourcesAsync(package, ct).ConfigureAwait(false);
                foreach (var probe in probes) AppPaths.Log(DescribeProbe(probe));
            }
            sources = Rank(sources, probes);
            progress?.Report(Loc.T("Fetch.ProbeOrder", string.Join(" → ", sources.Select(s => s.Name))));
        }

        // GitHub's endpoints reset connections fairly often on some networks (observed ~25% of
        // attempts here), so a transient failure is retried before giving up or moving on.
        const int attemptsPerSource = 2;
        var failures = new List<string>();

        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= attemptsPerSource; attempt++)
            {
                await (gate?.WaitAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                progress?.Report(attempt > 1 ? Loc.T("Fetch.Retrying", source.Name, attempt) : Loc.T("Fetch.Starting", source.Name));

                if (attempt > 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                var attemptCt = gate?.BeginAttempt(ct) ?? ct;
                OpResult result;
                var skipped = false;
                try
                {
                    result = await AttemptAsync(source, destination, progress, attemptCt, byteProgress, gate, runtimeModel)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Not the caller's cancellation: the user skipped this source, or the HTTP client
                    // gave up on it. Either way it is finished; a skipped one is not retried.
                    skipped = attemptCt.IsCancellationRequested;
                    result = new OpResult();
                    result.Fail(Loc.T(skipped ? "Fetch.SkippedByUser" : "Fetch.TimedOut"));
                }
                finally
                {
                    gate?.EndAttempt();
                }

                if (result.Ok) return result;

                AppPaths.Log(Loc.T("Fetch.AttemptFailed", source.Name, attempt, attemptsPerSource, result.Message));
                if (skipped || attempt == attemptsPerSource)
                {
                    failures.Add(Loc.T("Fetch.FailureItem", source.Name, result.Message));
                    break;
                }
            }
        }

        var r = new OpResult();
        r.Fail(Loc.T(singleSource ? "Fetch.SingleSourceFailed" : "Fetch.AllFailed",
                     string.Join("\n     ", failures)));
        return r;
    }

    /// <summary>The status line for a progress tick that carries no byte count.</summary>
    public static string DescribePhase(FetchProgress p) => p.Phase switch
    {
        FetchPhase.Probing => Loc.T("Fetch.Probing"),
        FetchPhase.Extracting => Loc.T("Fetch.PhaseExtracting", p.SourceName),
        FetchPhase.Verifying => Loc.T("Fetch.PhaseVerifying", p.SourceName),
        _ => "",
    };

    /// <summary>One line for the log or the picker: a source's measured rate and latency, or why it did not answer.</summary>
    public static string DescribeProbe(SourceProbe probe)
    {
        var name = Sources.FirstOrDefault(s => s.Id == probe.SourceId)?.Name ?? probe.SourceId;
        return probe.Reachable
            ? Loc.T("Fetch.ProbeResult", name, FormatSpeed(probe.BytesPerSecond), (int)probe.Latency.TotalMilliseconds)
            : Loc.T("Fetch.ProbeFailed", name, probe.Error);
    }

    /// <summary>True when the caller named a source rather than leaving it on automatic.</summary>
    private static bool IsExplicitChoice(string? sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) &&
        !string.Equals(sourceId, AutoSourceId, StringComparison.OrdinalIgnoreCase);

    private static async Task<OpResult> AttemptAsync(
        Source source,
        string destination,
        IProgress<string>? progress,
        CancellationToken ct,
        IProgress<FetchProgress>? byteProgress,
        DownloadGate? gate,
        string runtimeModel)
    {
        var r = new OpResult();
        var staging = Path.Combine(Path.GetTempPath(), "dlssg_" + Guid.NewGuid().ToString("N"));

        try
        {
            if (source.IsArchive)
            {
                await FetchArchiveAsync(source, staging, r, progress, ct, byteProgress, gate).ConfigureAwait(false);
                if (!ModSource.SuperResolutionFiles.All(p => File.Exists(Path.Combine(staging, p))))
                    throw new InvalidOperationException(Loc.T("Manage.NeedFullPackage"));
            }
            else
                await FetchIndividualFilesAsync(source, staging, r, progress, ct, byteProgress, gate, runtimeModel).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T("Fetch.Verifying"));
            byteProgress?.Report(new FetchProgress(source.Name, 1, 1, 0, -1, 0, FetchPhase.Verifying));

            // The shipped INI stopped carrying a version banner in 0.3.0; every source serves the
            // supported tag, so that is the release this payload is.
            var version = ModSource.ReadVersion(Path.Combine(staging, ModSource.IniName)) ?? SupportedRelease;
            if (!InstallStaged(staging, destination, source.Official, runtimeModel, version, r, out var copied)) return r;

            r.Message = Loc.T("Fetch.UpdatedVersion", version, source.Name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Fetch.Failed", ex.Message));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        return r;
    }

    /// <summary>
    /// Verifies a staged payload and only then copies it into the mod folder: a mirror, or a zip of
    /// unknown origin, must not be able to put a DLL there that is not the project's build.
    /// </summary>
    private static bool InstallStaged(string staging, string destination, bool officialSource, string runtimeModel,
        string releaseLabel, OpResult r, out int copied)
    {
        copied = 0;
        var (accepted, message) = Verify(staging, officialSource);
        r.Note(message);
        if (!accepted)
        {
            r.Fail(message);
            return false;
        }

        Directory.CreateDirectory(destination);
        copied = Publish(staging, destination);
        File.WriteAllText(Path.Combine(destination, BuildCatalog.Marker), BuildCatalog.Normalize(runtimeModel));
        PruneSupersededEntries(destination, r);

        // Written even when empty: a payload that replaced the old files must not keep the old marker.
        TryWriteVersionMarker(destination, releaseLabel);
        if (!new ModSource(destination).SupportsSuperResolution) r.Note(Loc.T("Manage.NeedFullPackage"));

        r.Note(Loc.T("Fetch.Updated", copied, destination));
        return true;
    }

    /// <summary>
    /// Installs a release archive the user already has — the offline route for a machine that cannot
    /// reach any source. The file is handled like a mirror download: every DLL must carry the project's
    /// pinned signature (NVIDIA's for the runtime it bundles), and nothing reaches the mod folder
    /// unless the whole payload passes. Both the full package and a zip of the base files are accepted.
    /// </summary>
    public static Task<OpResult> ImportArchiveAsync(string archivePath, string destination,
        IProgress<string>? progress, CancellationToken ct, string runtimeModel = BuildCatalog.Default) =>
        Task.Run(() => ImportArchive(archivePath, destination, progress, ct, runtimeModel), ct);

    private static OpResult ImportArchive(string archivePath, string destination, IProgress<string>? progress,
        CancellationToken ct, string runtimeModel)
    {
        var r = new OpResult();
        var name = Path.GetFileName(archivePath);
        if (BuildCatalog.Normalize(runtimeModel) != BuildCatalog.Default)
        {
            r.Fail(Loc.T("Manage.RetiredBuild"));
            return r;
        }
        if (!File.Exists(archivePath) || !string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            r.Fail(Loc.T("Fetch.ImportNotZip", name));
            return r;
        }
        var length = new FileInfo(archivePath).Length;
        if (length > MaxArchiveBytes)
        {
            r.Fail(Loc.T("Fetch.ArchiveTooLarge", length / 1024 / 1024));
            return r;
        }

        var staging = Path.Combine(Path.GetTempPath(), "dlssg_" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(Loc.T("Fetch.ImportExtracting", name));
            ExtractArchive(archivePath, staging, ct);

            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T("Fetch.Verifying"));
            var label = ReleaseLabelOf(staging);
            if (!InstallStaged(staging, destination, officialSource: false, runtimeModel, label ?? "", r, out _)) return r;

            if (label is not null && label != SupportedRelease)
                r.Note(Loc.T("Fetch.ImportVersionMismatch", label, SupportedRelease));
            var full = ModSource.SuperResolutionFiles.All(p => File.Exists(Path.Combine(staging, p)));
            r.Message = Loc.T(full ? "Fetch.ImportedFull" : "Fetch.ImportedBase",
                label ?? Loc.T("ModSource.UnknownVersion"), name);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            r.Fail(Loc.T("Fetch.ImportBadZip", name, ex.Message));
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Fetch.ImportFailed", ex.Message));
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        return r;
    }

    /// <summary>The release an archive holds: the INI banner of the 0.2.x line, otherwise the README title.</summary>
    private static string? ReleaseLabelOf(string staging)
    {
        var fromIni = ModSource.ReadVersion(Path.Combine(staging, ModSource.IniName));
        if (fromIni is not null) return fromIni;

        try
        {
            var readme = Path.Combine(staging, "README.md");
            return File.Exists(readme) ? ReadVersionFromReadme(File.ReadAllText(readme)) : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task FetchArchiveAsync(Source source, string staging, OpResult r, IProgress<string>? progress,
        CancellationToken ct, IProgress<FetchProgress>? byteProgress, DownloadGate? gate)
    {
        var archive = staging + ".zip";
        try
        {
            using var client = CreateClient();
            var total = await TransferAsync(client, new Uri(UrlFor(source, RepoPath, RepoRef, null)), archive, MaxArchiveBytes,
                (done, size, speed) => byteProgress?.Report(new FetchProgress(source.Name, 1, 1, done, size, speed)),
                gate, ct).ConfigureAwait(false);
            r.Note(Loc.T("Fetch.Downloaded", $"{total / 1024 / 1024.0:F1}"));

            progress?.Report(Loc.T("Fetch.Extracting"));
            byteProgress?.Report(new FetchProgress(source.Name, 1, 1, total, total, 0, FetchPhase.Extracting));
            ExtractArchive(archive, staging, ct);
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private static async Task FetchIndividualFilesAsync(Source source, string staging, OpResult r, IProgress<string>? progress,
        CancellationToken ct, IProgress<FetchProgress>? byteProgress, DownloadGate? gate, string runtimeModel)
    {
        using var client = CreateClient();
        Directory.CreateDirectory(staging);

        long total = 0;
        var done = 0;

        foreach (var artifact in Payload)
        {
            await (gate?.WaitAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(Loc.T("Fetch.Downloading", artifact.SourcePath, done, Payload.Length));

            var url = new Uri(UrlFor(source, RepoPath, RepoRef, BuildCatalog.RemotePath(artifact.SourcePath, runtimeModel)));
            var target = Path.Combine(staging, artifact.SourcePath.Replace('/', Path.DirectorySeparatorChar));

            // Nested entries such as altnative/winmm.dll need their folder to exist before writing.
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            try
            {
                var index = done;
                total += await TransferAsync(client, url, target, MaxArchiveBytes - total,
                    (fileBytes, size, speed) => byteProgress?.Report(new FetchProgress(source.Name, index, Payload.Length, fileBytes, size, speed)),
                    gate, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!artifact.Required && ex is not OperationCanceledException)
            {
                if (File.Exists(target)) File.Delete(target);
                done--;
                // Optional files (readme, presets) may legitimately be absent; note and move on.
                r.Note(Loc.T("Fetch.SkippedOptional", artifact.SourcePath, ex.Message));
            }
        }

        r.Note(Loc.T("Fetch.DownloadedCount", $"{total / 1024 / 1024.0:F1}", done, Payload.Length));
    }

    /// <summary>
    /// Checks a staged payload. Required files must exist; every DLL must be signed by the project,
    /// and on a non-official source the signer must match the pinned certificate.
    /// </summary>
    private static (bool Accepted, string Message) Verify(string staging, bool officialSource)
    {
        var missing = Payload.Where(a => a.Required && !File.Exists(Path.Combine(staging, a.SourcePath)))
                             .Select(a => a.SourcePath)
                             .ToList();
        if (missing.Count > 0)
            return (false, Loc.T("Fetch.Incomplete", Loc.Join(missing)));
        if (ModSource.SuperResolutionFiles.Count(p => File.Exists(Path.Combine(staging, p))) == 1)
            return (false, Loc.T("Manage.NeedFullPackage"));

        var pinMismatch = new List<string>();
        var unsigned = new List<string>();

        foreach (var artifact in Payload.Where(a => a.NeedsSignature))
        {
            var path = Path.Combine(staging, artifact.SourcePath);
            if (!File.Exists(path)) continue;

            // X509Certificate2 is needed for Thumbprint; the static loader returns the base type.
            var cert = DeploymentService.ReadSignerCertificate(path, out var intact, requireTrusted: artifact.Nvidia);
            using (cert)
            {
                var subject = cert?.Subject ?? "";
                var expected = artifact.Nvidia ? "NVIDIA Corporation" : ExpectedSignerSubject;
                if (!intact || !subject.Contains(expected, StringComparison.OrdinalIgnoreCase))
                    unsigned.Add(Path.GetFileName(artifact.SourcePath));
                else if (!artifact.Nvidia && !string.Equals(cert!.Thumbprint, PinnedCertThumbprint, StringComparison.OrdinalIgnoreCase))
                    pinMismatch.Add(Path.GetFileName(artifact.SourcePath));
            }
        }

        if (unsigned.Count > 0)
            return (false, Loc.T("Fetch.Unsigned", Loc.Join(unsigned)));

        if (pinMismatch.Count > 0)
        {
            var detail = Loc.T("Fetch.PinDetail", Loc.Join(pinMismatch));

            // A mirror is not the authority for this content, so an unexpected signer is rejected.
            if (!officialSource)
                return (false, Loc.T("Fetch.PinMismatch", detail));

            // GitHub itself is trusted; a different certificate most likely means upstream re-signed.
            AppPaths.Log(Loc.T("Fetch.PinWarning", detail));
            return (true, Loc.T("Fetch.PinAccepted", Loc.Join(pinMismatch)));
        }

        return (true, Loc.T("Fetch.SignatureOk"));
    }

    /// <summary>
    /// Copies the verified payload from the staging folder into the mod folder.
    ///
    /// Staging mirrors the repository layout; the destination maps each file to the manager's own layout,
    /// which is why the two paths are configured separately on <see cref="Artifact"/>.
    /// </summary>
    private static int Publish(string staging, string destination)
    {
        var copied = 0;

        foreach (var artifact in Payload)
        {
            var source = Path.Combine(staging, artifact.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                // Do not mix old SR files with a new frame-generation-only download.
                if (ModSource.SuperResolutionFiles.Contains(artifact.DestinationPath))
                {
                    var stale = Path.Combine(destination, artifact.DestinationPath);
                    if (File.Exists(stale)) File.Delete(stale);
                }
                continue;
            }

            CopyInto(source, Path.Combine(destination, artifact.DestinationPath.Replace('/', Path.DirectorySeparatorChar)), destination);
            copied++;
        }

        return copied;
    }

    /// <summary>
    /// Removes proxy DLLs left over from an earlier release that the current payload no longer ships.
    ///
    /// 0.3.0 dropped winhttp.dll, and a stale file would sit in the mod folder looking like an entry the
    /// user added themselves — while pairing a previous generation's build with the new INI, which is a
    /// combination neither generation supports. Only files carrying the project's own signature are
    /// removed, so anything the user brought along survives whatever its name.
    /// </summary>
    private static void PruneSupersededEntries(string destination, OpResult r)
    {
        var shipped = Payload
            .Select(a => Path.GetFileName(a.DestinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ModSource.KnownProxyNames)
        {
            if (shipped.Contains(name)) continue;

            var path = ModSource.ResolveDllPath(destination, name);
            if (!File.Exists(path) || !DeploymentService.IsProjectSigned(path)) continue;

            try
            {
                File.Delete(path);
                r.Note(Loc.T("Fetch.PrunedStale", name));
            }
            catch (Exception ex)
            {
                AppPaths.Log("清理旧入口失败（" + name + "）: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Records which release the payload came from, for the interface badge and the log.
    ///
    /// The shipped INI stopped carrying a version banner in 0.3.0, so a release label cannot be read off
    /// the files themselves any more. Best effort: failing to write the label must never fail a download
    /// that already succeeded.
    /// </summary>
    private static void TryWriteVersionMarker(string destination, string label)
    {
        try
        {
            File.WriteAllText(Path.Combine(destination, VersionMarkerName), label, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppPaths.Log("写入版本标记失败: " + ex.Message);
        }
    }

    /// <summary>Rejects any path that would escape the destination folder.</summary>
    private static void CopyInto(string sourceFile, string destinationFile, string destinationRoot)
    {
        var rootFull = Path.GetFullPath(destinationRoot).TrimEnd('\\') + "\\";
        var destFull = Path.GetFullPath(destinationFile);
        if (!destFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));

        var dir = Path.GetDirectoryName(destFull);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(sourceFile, destFull, overwrite: true);
        DeploymentService.Unblock(destFull);
    }

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
