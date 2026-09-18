using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace DLSSGManager;

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
        // Third-party mirrors. Accepted only with a matching certificate pin, because a mirror is not
        // the authority for this content — see Verify().
        "cdn.jsdelivr.net",
        "gh-proxy.com",
        "ghfast.top",
    };

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

    private const string RepoPath = "sdli1995/dlssg_for_sm86";
    private const string RepoRef = "main";

    /// <summary>
    /// File the downloader leaves in the mod folder to name the release it fetched. The shipped INI
    /// stopped carrying a version banner in 0.3.0, so this is what the interface badge reads.
    /// </summary>
    public const string VersionMarkerName = ".manager-version";

    private sealed record Artifact(string SourcePath, string DestinationPath, bool Required, bool NeedsSignature);

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
    }

    /// <summary>
    /// Address of the version probe: the published INI on the default branch, through an official
    /// per-file endpoint. Exposed so the URL policy test covers it like every other address this
    /// downloader can contact.
    /// </summary>
    public static string VersionProbeUrl => UrlFor(OfficialFileSource(), RepoPath, RepoRef, ModSource.IniName);

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
    /// Extracted so the rule that matters — a mismatch means the bytes are discarded, from whichever
    /// endpoint they came — is exercised by a test rather than only by a live download.
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
        // Templates carry {repo}/{ref}/{path} rather than a baked-in repository, so the list can serve
        // more than one repository without duplicating every endpoint.
        //
        // Per-file sources come first since 0.3.3: the repository grew a second build variant
        // (310.1/) and an archive/ of old releases, so a whole-branch zip now carries ~470 MB where
        // the payload needs ~210 MB. Fetching only the listed files is the smaller request. The two
        // archive endpoints stay as fallbacks — one request each, useful when per-file endpoints are
        // rate-limited.

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

        // One request, the whole branch (now much larger than the payload; see above).
        new("codeload", "Fetch.SourceCodeload", true,
            "https://codeload.github.com/{repo}/zip/refs/heads/{ref}", true),

        // Same content through a different entry point; useful when codeload is throttled.
        new("zipball", "Fetch.SourceZipball", true,
            "https://api.github.com/repos/{repo}/zipball/{ref}", true),
    };

    /// <summary>Builds a concrete address from a source template.</summary>
    private static string UrlFor(Source source, string repo, string reference, string? path) =>
        source.UrlTemplate
            .Replace("{repo}", repo, StringComparison.Ordinal)
            .Replace("{ref}", reference, StringComparison.Ordinal)
            .Replace("{path}", path ?? "", StringComparison.Ordinal);

    /// <summary>
    /// The address a source would use for the upstream archive, for tests that assert the policy holds
    /// for every endpoint the downloader can ever contact.
    /// </summary>
    public static string ArchiveUrlFor(string sourceId)
    {
        var source = Sources.First(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        return UrlFor(source, RepoPath, RepoRef, null);
    }

    /// <summary>
    /// A source as presented to the user: a stable id to pass back, plus text in the active language.
    /// </summary>
    public sealed record SourceOption(string Id, string Name, string Note, bool Official);

    /// <summary>
    /// The sources a user may choose from, in the order they would be tried. Notes explain what each
    /// one is for, since the difference between them is not obvious from the name.
    /// </summary>
    public static IReadOnlyList<SourceOption> AvailableSources => Sources
        .Select(s => new SourceOption(s.Id, s.Name, Loc.T($"Fetch.Note.{s.Id}"), s.Official))
        .ToArray();

    /// <summary>Id used to mean "try every source in turn"; see <see cref="DownloadIntoAsync"/>.</summary>
    public const string AutoSourceId = "auto";

    public static IReadOnlyList<string> SourceNames => Sources.Select(s => s.Name).ToArray();

    /// <summary>Stable source identifiers, for code and tests that need to address a source.</summary>
    public static IReadOnlyList<string> SourceIds => Sources.Select(s => s.Id).ToArray();

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
    private static IReadOnlyList<Source> ActiveSources(string? sourceId = null)
    {
        if (!string.IsNullOrWhiteSpace(sourceId) &&
            !string.Equals(sourceId, AutoSourceId, StringComparison.OrdinalIgnoreCase))
        {
            var chosen = Sources.Where(s => string.Equals(s.Id, sourceId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count > 0) return chosen;

            AppPaths.Log(Loc.T("Fetch.UnknownFilter", sourceId,
                string.Join(" | ", Sources.Select(s => s.Id))));
        }

        var filter = Environment.GetEnvironmentVariable(SourceFilterVariable);
        if (string.IsNullOrWhiteSpace(filter)) return Sources;

        var matched = Sources.Where(s => s.Id.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (matched.Count == 0)
        {
            AppPaths.Log(Loc.T("Fetch.UnknownFilter", filter,
                string.Join(" | ", Sources.Select(s => s.Id))));
            return Sources;
        }

        return matched;
    }

    public static bool IsAllowedAddress(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        if (!AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return false;

        IPAddress[] addresses;
        try { addresses = Dns.GetHostAddresses(uri.Host); }
        catch { return false; }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
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

    /// <summary>Follows at most a handful of hops, re-validating every target.</summary>
    private static async Task<HttpResponseMessage> GetCheckedAsync(HttpClient client, Uri uri, CancellationToken ct)
    {
        if (!IsAllowedAddress(uri)) throw new InvalidOperationException(Loc.T("Fetch.UrlRejected", uri));

        var current = uri;
        for (var hop = 0; hop < 5; hop++)
        {
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Downloads the payload.
    ///
    /// With <paramref name="sourceId"/> null or <see cref="AutoSourceId"/>, each source is tried in
    /// turn until one yields a verified result. With a specific id, only that source is used: the user
    /// chose it deliberately, and silently downloading from elsewhere would misreport where the files
    /// came from and defeat the point of choosing.
    /// </summary>
    /// <param name="versionLabel">
    /// What <see cref="DetectLatestVersionAsync"/> reported before the download, recorded beside the
    /// payload so the interface can name the installed release. Optional: a download must not depend on
    /// the probe having succeeded.
    /// </param>
    public static async Task<OpResult> DownloadIntoAsync(
        string destination,
        IProgress<string>? progress,
        CancellationToken ct,
        string? sourceId = null,
        string? versionLabel = null)
    {
        var sources = ActiveSources(sourceId);
        var singleSource = sources.Count == 1 && IsExplicitChoice(sourceId);

        // GitHub's endpoints reset connections fairly often on some networks (observed ~25% of
        // attempts here), so a transient failure is retried before giving up or moving on.
        const int attemptsPerSource = 2;
        var failures = new List<string>();

        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();

            for (var attempt = 1; attempt <= attemptsPerSource; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                progress?.Report(attempt > 1 ? Loc.T("Fetch.Retrying", source.Name, attempt) : Loc.T("Fetch.Starting", source.Name));

                if (attempt > 1)
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                var result = await AttemptAsync(source, destination, progress, ct, versionLabel).ConfigureAwait(false);
                if (result.Ok) return result;

                AppPaths.Log(Loc.T("Fetch.AttemptFailed", source.Name, attempt, attemptsPerSource, result.Message));
                if (attempt == attemptsPerSource) failures.Add(Loc.T("Fetch.FailureItem", source.Name, result.Message));
            }
        }

        var r = new OpResult();
        r.Fail(Loc.T(singleSource ? "Fetch.SingleSourceFailed" : "Fetch.AllFailed",
                     string.Join("\n     ", failures)));
        return r;
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
        string? versionLabel)
    {
        var r = new OpResult();
        var staging = Path.Combine(Path.GetTempPath(), "dlssg_" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(destination);

            if (source.IsArchive)
                await FetchArchiveAsync(source, staging, r, progress, ct).ConfigureAwait(false);
            else
                await FetchIndividualFilesAsync(source, staging, r, progress, ct).ConfigureAwait(false);

            // Verify before touching the destination: a mirror must not be able to write a DLL that
            // is not the project's build.
            var (accepted, message) = Verify(staging, source.Official);
            r.Note(message);
            if (!accepted)
            {
                r.Fail(message);
                return r;
            }

            var copied = Publish(staging, destination);
            PruneSupersededEntries(destination, r);
            var version = ModSource.ReadVersion(Path.Combine(destination, ModSource.IniName)) ?? versionLabel;

            // The shipped INI stopped carrying a version banner in 0.3.0, so the label detected before
            // the download is remembered on disk — otherwise the badge would have nothing to show and
            // the next start could not tell which release is installed. Written even when unknown
            // (then empty): a payload that replaced the old files must not keep the old marker.
            TryWriteVersionMarker(destination, versionLabel ?? "");

            r.Note(Loc.T("Fetch.Updated", copied, destination));
            r.Message = version is null
                ? Loc.T("Fetch.UpdatedGeneric", copied, source.Name)
                : Loc.T("Fetch.UpdatedVersion", version, source.Name);
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

    private static async Task FetchArchiveAsync(Source source, string staging, OpResult r, IProgress<string>? progress, CancellationToken ct)
    {
        var archive = staging + ".zip";
        try
        {
            using var client = CreateClient();
            using var response = await GetCheckedAsync(client, new Uri(UrlFor(source, RepoPath, RepoRef, null)), ct).ConfigureAwait(false);

            if (response.Content.Headers.ContentLength is long declared && declared > MaxArchiveBytes)
                throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge", declared / 1024 / 1024));

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(archive))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes) throw new InvalidOperationException(Loc.T("Fetch.ArchiveTooLarge2"));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                r.Note(Loc.T("Fetch.Downloaded", $"{total / 1024 / 1024.0:F1}"));
            }

            progress?.Report(Loc.T("Fetch.Extracting"));
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(archive, staging, overwriteFiles: true);

            // The archive wraps everything in a single top-level folder whose name varies by source.
            var inner = Directory.EnumerateDirectories(staging).FirstOrDefault();
            if (inner is null) throw new InvalidOperationException(Loc.T("Fetch.BadArchive"));

            // Flatten that wrapper so staging looks like the payload root.
            foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
            {
                var target = Path.Combine(staging, Path.GetFileName(entry));
                if (Directory.Exists(entry)) Directory.Move(entry, target);
                else File.Move(entry, target, overwrite: true);
            }

            TryDeleteDirectory(inner);
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private static async Task FetchIndividualFilesAsync(Source source, string staging, OpResult r, IProgress<string>? progress, CancellationToken ct)
    {
        using var client = CreateClient();
        Directory.CreateDirectory(staging);

        long total = 0;
        var done = 0;

        foreach (var artifact in Payload)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T("Fetch.Downloading", artifact.SourcePath, done + 1, Payload.Length));

            var url = new Uri(UrlFor(source, RepoPath, RepoRef, artifact.SourcePath));
            var target = Path.Combine(staging, artifact.SourcePath.Replace('/', Path.DirectorySeparatorChar));

            // Nested entries such as altnative/winmm.dll need their folder to exist before writing.
            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);

            try
            {
                using var response = await GetCheckedAsync(client, url, ct).ConfigureAwait(false);
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = File.Create(target);

                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes) throw new InvalidOperationException(Loc.T("Fetch.ContentTooLarge"));
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }

                done++;
            }
            catch (Exception ex) when (!artifact.Required)
            {
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

        var pinMismatch = new List<string>();
        var unsigned = new List<string>();

        foreach (var artifact in Payload.Where(a => a.NeedsSignature))
        {
            var path = Path.Combine(staging, artifact.SourcePath);

            // X509Certificate2 is needed for Thumbprint; the static loader returns the base type.
            X509Certificate2? cert;
            try
            {
                cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            }
            catch
            {
                unsigned.Add(Path.GetFileName(artifact.SourcePath));
                continue;
            }

            using (cert)
            {
                var subject = cert.Subject ?? "";
                if (!subject.Contains(ExpectedSignerSubject, StringComparison.OrdinalIgnoreCase))
                    unsigned.Add(Path.GetFileName(artifact.SourcePath));
                else if (!string.Equals(cert.Thumbprint, PinnedCertThumbprint, StringComparison.OrdinalIgnoreCase))
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
            if (!File.Exists(source)) continue;

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
