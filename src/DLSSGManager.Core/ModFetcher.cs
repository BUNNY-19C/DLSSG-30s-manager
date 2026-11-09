using System.Diagnostics;
using System.Globalization;

namespace DLSSGManager;

/// <summary>
/// Obtains the dlssg_for_sm86 payload from any of several sources and installs it as a release folder.
///
/// This class only orchestrates; the parts live beside it:
/// <list type="bullet">
/// <item><see cref="DownloadSources"/> — endpoints, selection and ranking.</item>
/// <item><see cref="PayloadHttp"/> — HTTP clients from <see cref="IHttpClientFactory"/>, with a
/// resilience pipeline for transient failures.</item>
/// <item><see cref="DownloadPolicy"/> — allow-listed hosts and the connect-time address check.</item>
/// <item><see cref="HttpTransfer"/> — streaming with redirect checks, caps, stall detection and resume.</item>
/// <item><see cref="ArchiveExtractor"/> — hostile-archive extraction.</item>
/// <item><see cref="PayloadVerifier"/> and <see cref="ModStore"/> — byte-exact verification against the
/// release manifest, then an all-or-nothing install into <c>versions/&lt;release&gt;</c>.</item>
/// </list>
/// No source is trusted for content: whatever served the bytes, they must match the manifest.
/// </summary>
public static partial class ModFetcher
{
    public const string SupportedRelease = "0.4.1";
    private const string RepoRef = SupportedRelease;

    /// <summary>
    /// File the downloader leaves in a release folder to name the release. The shipped INI stopped
    /// carrying a version banner in 0.3.0, so this is what the interface badge reads.
    /// </summary>
    public const string VersionMarkerName = ".manager-version";

    /// <summary>Id used to mean "try every source in turn"; see <see cref="DownloadIntoAsync"/>.</summary>
    public const string AutoSourceId = DownloadSources.AutoSourceId;

    /// <summary>Environment variable narrowing downloads to one source, for diagnostics.</summary>
    public const string SourceFilterVariable = DownloadSources.SourceFilterVariable;

    /// <summary>
    /// Speed-test sample: a ranged read of this many bytes, bounded by <see cref="ProbeTimeout"/>.
    /// Enough to get past connection setup, small enough that testing every source costs a few MB.
    /// </summary>
    private const int ProbeBytes = 512 * 1024;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);

    /// <summary>File a per-file source is timed with: large, and present in every release of the layout.</summary>
    private const string ProbeFile = "dlssg_sm86/kernels/fg_kernels.dll";

    /// <summary>A source attempt is repeated once before moving on; transient request failures are already retried by the HTTP pipeline.</summary>
    private const int AttemptsPerSource = 2;

    /// <summary>
    /// Community builds this manager recognises by hash but no longer distributes.
    ///
    /// The community <c>d3d12.dll</c> predates upstream shipping its own d3d12 entry, and people who
    /// installed it by hand still have it in a game folder. A hash is the only proof available for a file
    /// carrying no certificate this project can verify; without it such an install looks undeployed and
    /// can never be adopted or cleaned up.
    /// </summary>
    public static IReadOnlyList<string> KnownCommunityBuildHashes { get; } = new[]
    {
        "65E6F912F5D485DC56BC6B48430DF046FF06D38B8A69595B42E316E9644F7C2B",
    };

    /// <summary>True when the file's bytes match a community build this manager still recognises.</summary>
    public static bool IsKnownCommunityBuild(string path) =>
        KnownCommunityBuildHashes.Any(hash => MatchesPin(path, hash));

    /// <summary>True when the file's bytes have the given SHA-256.</summary>
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

    // ---- progress and display ------------------------------------------------------------------

    /// <summary>
    /// A byte-level progress tick for the interface: which source, which file of the payload, how far
    /// through it, and the smoothed transfer rate. <see cref="BytesTotal"/> is -1 when the endpoint did
    /// not declare a length, so the bar can go indeterminate.
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
    /// One source's speed test: whether it answered, how fast the sample arrived, and how large the
    /// probed file is (the whole package for archive sources, -1 when the server did not say).
    /// </summary>
    public sealed record SourceProbe(string SourceId, bool Reachable, double BytesPerSecond, TimeSpan Latency,
                                     long TotalBytes, string Error);

    /// <summary>A source as presented to the user: a stable id to pass back, plus text in the active language.</summary>
    public sealed record SourceOption(string Id, string Name, string Note, bool Official, PayloadPackage Package);

    /// <summary>The sources a user may choose from, in the order they would be tried.</summary>
    public static IReadOnlyList<SourceOption> AvailableSources => DownloadSources.All
        .Select(s => new SourceOption(s.Id, s.Name, Loc.T($"Fetch.Note.{s.Id}"), s.Official, s.Package))
        .ToArray();

    /// <inheritdoc cref="DownloadSources.RankIds"/>
    public static IReadOnlyList<string> RankSourceIds(IReadOnlyList<string> usualOrder, IReadOnlyList<SourceProbe> probes) =>
        DownloadSources.RankIds(usualOrder, probes);

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
        var name = DownloadSources.All.FirstOrDefault(s => s.Id == probe.SourceId)?.Name ?? probe.SourceId;
        return probe.Reachable
            ? Loc.T("Fetch.ProbeResult", name, FormatSpeed(probe.BytesPerSecond), (int)probe.Latency.TotalMilliseconds)
            : Loc.T("Fetch.ProbeFailed", name, probe.Error);
    }

    // ---- version probe -------------------------------------------------------------------------

    /// <summary>
    /// Reads which release upstream is publishing, without downloading the payload.
    ///
    /// Two probes, cheapest first: the INI's own <c>; Native x.y.z.</c> banner while that exists, and
    /// otherwise the version in the README's title. Advisory only: a failed probe returns null, because
    /// a blocked endpoint must never be the reason a first run cannot get its files.
    /// </summary>
    public static async Task<string?> DetectLatestVersionAsync(CancellationToken ct)
    {
        var official = DownloadSources.OfficialFile;
        foreach (var probe in new[] { ModSource.IniName, "README.md" }.Select(p => official.UrlFor(DownloadSources.RepoPath, RepoRef, p)))
        {
            try
            {
                using var client = PayloadHttp.Payload();
                using var response = await HttpTransfer.GetCheckedAsync(client, probe, ct).ConfigureAwait(false);

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
    /// Only the first headings are searched: dotted numbers further down are about the bundled runtime
    /// generations (310.9, 310.1) and are not the release number.
    /// </summary>
    internal static string? ReadVersionFromReadme(string text)
    {
        foreach (var line in text.Split('\n').Take(10))
        {
            if (!line.StartsWith('#')) continue;

            var m = System.Text.RegularExpressions.Regex.Match(line, @"([0-9]+\.[0-9]+(?:\.[0-9]+)+)");
            if (m.Success) return m.Groups[1].Value;
        }

        return null;
    }

    // ---- speed tests ---------------------------------------------------------------------------

    /// <summary>
    /// Times every source able to deliver <paramref name="package"/>, all at once: a ranged read of the
    /// release archive, or of one large payload file for per-file sources. Never throws for a source that
    /// fails; the result says so instead.
    /// </summary>
    public static async Task<IReadOnlyList<SourceProbe>> ProbeSourcesAsync(PayloadPackage package, CancellationToken ct) =>
        await Task.WhenAll(DownloadSources.For(package).Select(s => ProbeAsync(s, ct))).ConfigureAwait(false);

    private static async Task<SourceProbe> ProbeAsync(DownloadSource source, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        var url = source.UrlFor(DownloadSources.RepoPath, RepoRef, source.IsArchive ? null : ProbeFile);
        var watch = Stopwatch.StartNew();
        var latency = TimeSpan.Zero;

        try
        {
            using var client = PayloadHttp.Probe();
            using var response = await HttpTransfer.GetCheckedAsync(client, url, timeout.Token, 0, ProbeBytes - 1).ConfigureAwait(false);
            latency = watch.Elapsed;
            var total = HttpTransfer.TotalLength(response) ?? -1;

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

    // ---- download ------------------------------------------------------------------------------

    /// <summary>
    /// Downloads the supported release into the mod root <paramref name="destination"/>.
    ///
    /// With <paramref name="sourceId"/> null or <see cref="AutoSourceId"/>, the sources for the package
    /// are speed-tested and tried fastest first until one yields a verified result; a source that fails,
    /// stalls or is skipped hands over to the next. With a specific id, only that source is used: the
    /// user chose it deliberately, and silently downloading from elsewhere would misreport where the
    /// files came from.
    /// </summary>
    /// <param name="byteProgress">Per-chunk transfer progress for the interface; may be null.</param>
    /// <param name="gate">Pause and skip switches; may be null (never pauses).</param>
    /// <param name="package">Which payload to fetch; decides which sources take part.</param>
    /// <param name="probes">Speed tests already run (the picker runs them while the user decides).</param>
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

        var sources = DownloadSources.Active(sourceId, package);
        var singleSource = sources.Count == 1 && DownloadSources.IsExplicitChoice(sourceId);

        if (!singleSource && sources.Count > 1)
        {
            if (probes is null)
            {
                progress?.Report(Loc.T("Fetch.Probing"));
                byteProgress?.Report(new FetchProgress("", 0, 0, 0, -1, 0, FetchPhase.Probing));
                probes = await ProbeSourcesAsync(package, ct).ConfigureAwait(false);
                foreach (var probe in probes) AppPaths.Log(DescribeProbe(probe));
            }
            sources = DownloadSources.Rank(sources, probes);
            progress?.Report(Loc.T("Fetch.ProbeOrder", string.Join(" → ", sources.Select(s => s.Name))));
        }

        var failures = new List<string>();
        foreach (var source in sources)
        {
            for (var attempt = 1; attempt <= AttemptsPerSource; attempt++)
            {
                await (gate?.WaitAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                progress?.Report(attempt > 1 ? Loc.T("Fetch.Retrying", source.Name, attempt) : Loc.T("Fetch.Starting", source.Name));

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
                    // Not the caller's cancellation: the user skipped this source, or the HTTP client gave
                    // up on it. Either way it is finished; a skipped one is not retried.
                    skipped = attemptCt.IsCancellationRequested;
                    result = new OpResult();
                    result.Fail(Loc.T(skipped ? "Fetch.SkippedByUser" : "Fetch.TimedOut"));
                }
                finally
                {
                    gate?.EndAttempt();
                }

                if (result.Ok) return result;

                AppPaths.Log(Loc.T("Fetch.AttemptFailed", source.Name, attempt, AttemptsPerSource, result.Message));
                if (skipped || attempt == AttemptsPerSource)
                {
                    failures.Add(Loc.T("Fetch.FailureItem", source.Name, result.Message));
                    break;
                }
            }
        }

        var r = new OpResult();
        r.Fail(Loc.T(singleSource ? "Fetch.SingleSourceFailed" : "Fetch.AllFailed", string.Join("\n     ", failures)));
        return r;
    }

    private static async Task<OpResult> AttemptAsync(DownloadSource source, string destination, IProgress<string>? progress,
        CancellationToken ct, IProgress<FetchProgress>? byteProgress, DownloadGate? gate, string runtimeModel)
    {
        var r = new OpResult();
        var staging = NewStagingFolder();

        try
        {
            if (source.IsArchive)
            {
                await FetchArchiveAsync(source, staging, r, progress, ct, byteProgress, gate).ConfigureAwait(false);
                if (!ModSource.SuperResolutionFiles.All(p => File.Exists(Path.Combine(staging, p))))
                    throw new InvalidOperationException(Loc.T("Manage.NeedFullPackage"));
            }
            else
            {
                await FetchIndividualFilesAsync(source, staging, r, progress, ct, byteProgress, gate, runtimeModel).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T("Fetch.Verifying"));
            byteProgress?.Report(new FetchProgress(source.Name, 1, 1, 0, -1, 0, FetchPhase.Verifying));

            // Every source serves the supported tag, so its manifest is what the bytes must match —
            // official endpoint or mirror alike.
            var manifest = PayloadCatalog.Supported;
            if (!InstallStaged(staging, destination, manifest, r)) return r;

            r.Message = Loc.T("Fetch.UpdatedVersion", manifest.Version, source.Name);
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

    private static async Task FetchArchiveAsync(DownloadSource source, string staging, OpResult r, IProgress<string>? progress,
        CancellationToken ct, IProgress<FetchProgress>? byteProgress, DownloadGate? gate)
    {
        var archive = staging + ".zip";
        try
        {
            using var client = PayloadHttp.Payload();
            var total = await HttpTransfer.TransferAsync(client, source.UrlFor(DownloadSources.RepoPath, RepoRef, null), archive,
                ArchiveExtractor.MaxArchiveBytes,
                (done, size, speed) => byteProgress?.Report(new FetchProgress(source.Name, 1, 1, done, size, speed)),
                gate, ct).ConfigureAwait(false);
            r.Note(Loc.T("Fetch.Downloaded", $"{total / 1024 / 1024.0:F1}"));

            progress?.Report(Loc.T("Fetch.Extracting"));
            byteProgress?.Report(new FetchProgress(source.Name, 1, 1, total, total, 0, FetchPhase.Extracting));
            ArchiveExtractor.Extract(archive, staging, ct);
        }
        finally
        {
            TryDelete(archive);
        }
    }

    private static async Task FetchIndividualFilesAsync(DownloadSource source, string staging, OpResult r, IProgress<string>? progress,
        CancellationToken ct, IProgress<FetchProgress>? byteProgress, DownloadGate? gate, string runtimeModel)
    {
        using var client = PayloadHttp.Payload();
        Directory.CreateDirectory(staging);

        long total = 0;
        var done = 0;

        // The repository holds no SR files; those come only with the release archive.
        var files = PayloadCatalog.Supported.Files.Where(f => f.Package == PayloadPackageKind.Base).ToArray();
        foreach (var file in files)
        {
            await (gate?.WaitAsync(ct) ?? Task.CompletedTask).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(Loc.T("Fetch.Downloading", file.Source, done, files.Length));

            var url = source.UrlFor(DownloadSources.RepoPath, RepoRef, BuildCatalog.RemotePath(file.Source, runtimeModel));
            var target = Path.Combine(staging, file.Source.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                var index = done;
                // A file larger than the manifest says cannot be the release; stop reading there.
                // Documentation is not pinned, so it only gets the overall cap.
                var cap = ArchiveExtractor.MaxArchiveBytes - total;
                if (file.Kind != PayloadFileKind.Doc) cap = Math.Min(file.Size, cap);
                total += await HttpTransfer.TransferAsync(client, url, target, cap,
                    (fileBytes, size, speed) => byteProgress?.Report(new FetchProgress(source.Name, index, files.Length, fileBytes, size, speed)),
                    gate, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!file.Required && ex is not OperationCanceledException)
            {
                if (File.Exists(target)) File.Delete(target);
                done--;
                // Optional files (readme, notices) may legitimately be absent; note and move on.
                r.Note(Loc.T("Fetch.SkippedOptional", file.Source, ex.Message));
            }
        }

        r.Note(Loc.T("Fetch.DownloadedCount", $"{total / 1024 / 1024.0:F1}", done, files.Length));
    }

    // ---- offline import ------------------------------------------------------------------------

    /// <summary>
    /// Installs a release archive the user already has — the offline route for a machine that cannot
    /// reach any source. The release is identified by its INI template's hash, and every file must then
    /// match that release's manifest; nothing reaches the mod folder unless the whole payload passes.
    /// Both the full package and a zip of the base files are accepted.
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
        if (length > ArchiveExtractor.MaxArchiveBytes)
        {
            r.Fail(Loc.T("Fetch.ArchiveTooLarge", length / 1024 / 1024));
            return r;
        }

        var staging = NewStagingFolder();
        try
        {
            progress?.Report(Loc.T("Fetch.ImportExtracting", name));
            ArchiveExtractor.Extract(archivePath, staging, ct);

            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.T("Fetch.Verifying"));

            if (!InstallStaged(staging, destination, expected: null, r)) return r;

            var label = ModStore.ActiveVersion(destination);
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

    // ---- install -------------------------------------------------------------------------------

    /// <summary>
    /// Verifies a staged payload against the release manifest and only then installs it as a new release
    /// folder under the mod root (<see cref="ModStore"/>). With <paramref name="expected"/> null the
    /// release is identified from the payload itself.
    /// </summary>
    private static bool InstallStaged(string staging, string destination, PayloadManifest? expected, OpResult r)
    {
        var verified = PayloadVerifier.Verify(staging, expected);
        r.Note(verified.Message);
        if (!verified.Accepted)
        {
            r.Fail(verified.Message);
            return false;
        }

        var manifest = verified.Manifest!;
        var copied = 0;
        var active = ModStore.Activate(destination, manifest.Version,
            folder => copied = PayloadVerifier.Publish(staging, folder, manifest), r);
        if (!verified.HasSuperResolution) r.Note(Loc.T("Manage.NeedFullPackage"));

        r.Note(Loc.T("Fetch.Updated", copied, active));
        return true;
    }

    private static string NewStagingFolder() =>
        Path.Combine(Path.GetTempPath(), "dlssg_" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { /* temp file; the OS cleans the rest */ }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* temp folder; the OS cleans the rest */ }
    }
}
