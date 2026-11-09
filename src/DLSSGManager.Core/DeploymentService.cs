using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DLSSGManager;

public sealed class OpResult
{
    public bool Ok { get; set; } = true;
    public string Message { get; set; } = "";
    public List<string> Lines { get; } = new();

    public void Note(string text)
    {
        Lines.Add(text);
        AppPaths.Log(text);
    }

    public void Fail(string text)
    {
        Ok = false;
        Message = text;
        Lines.Add("错误: " + text);
        AppPaths.Log("错误: " + text);
    }
}

/// <summary>
/// Ownership checks, status evaluation and the deploy/restore entry points.
///
/// Writing into game folders is split in two: <see cref="DeployPlanner"/> decides every step without
/// touching anything, and <see cref="PlanExecutor"/> performs them. Every write is preceded by a backup
/// of anything that is not ours, and every removal is gated on the file being provably ours (pinned
/// project signature or a matching recorded hash), so a restore can never eat a ReShade dxgi.dll.
/// </summary>
public static partial class DeploymentService
{
    public const string AutoProxy = GameEntry.AutoProxy;

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// True when the file's Authenticode signature is intact and was made by a pinned project
    /// certificate (<see cref="ProjectSigning"/>).
    ///
    /// <see cref="X509Certificate.CreateFromSignedFile"/> only extracts the certificate — it does not
    /// check that the signature still matches the bytes. A tampered DLL would therefore look
    /// "signed" if that were the only check, which matters because downloads may come from a mirror.
    /// The signature is validated through WinVerifyTrust and the result is interpreted fail-closed.
    /// The signer is identified by thumbprint: a subject name can be chosen by anyone.
    /// </summary>
    public static bool IsProjectSigned(string path)
    {
        using var cert = ReadSignerCertificate(path, out var signatureIntact);
        return ProjectSigning.IsProjectSignature(cert, signatureIntact);
    }

    /// <summary>
    /// Reads the signer certificate and reports whether the file's signature still matches its
    /// contents.
    ///
    /// A self-signed certificate cannot chain to a trusted root, so chain validation necessarily
    /// fails even for an untouched file. Only the two outcomes that mean "the bytes are as signed"
    /// are accepted; every other result, including a bad digest, is treated as unsigned.
    /// </summary>
    public static X509Certificate2? ReadSignerCertificate(string path, out bool signatureIntact, bool requireTrusted = false)
    {
        signatureIntact = false;

        X509Certificate2 cert;
        try
        {
            // X509CertificateLoader has no equivalent for reading the signer out of a PE file; the
            // signature itself is validated separately by WinVerifyTrust below.
#pragma warning disable SYSLIB0057
            cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch
        {
            // No signature block at all, or not a PE file.
            return null;
        }

        signatureIntact = IsSignatureIntact(path, requireTrusted);
        return cert;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>Generic verify; accepts any certificate including a self-signed one.</summary>
    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    private const int S_OK = 0;
    /// <summary>Chain terminates in an untrusted root — expected for the project's self-signed cert.</summary>
    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    /// <summary>No signature present, or the hash in it does not match the file.</summary>
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);

    /// <summary>
    /// Verifies that the file's signature matches its contents. Fails closed: anything other than
    /// "verified" or "verified but self-signed" counts as not intact.
    /// </summary>
    private static bool IsSignatureIntact(string path, bool requireTrusted = false)
    {
        IntPtr filePtr = IntPtr.Zero;
        IntPtr dataPtr = IntPtr.Zero;

        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = path,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };

            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, filePtr, false);

            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = 0,
            };

            dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            Marshal.StructureToPtr(data, dataPtr, false);

            var result = WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, dataPtr);

            // Close the state or the handle stays open until the process exits.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            Marshal.StructureToPtr(data, dataPtr, false);
            WinVerifyTrust(IntPtr.Zero, WinTrustActionGenericVerifyV2, dataPtr);

            if (result == S_OK) return true;

            if (result == CERT_E_UNTRUSTEDROOT)
            {
                // The bytes are as signed; only the chain is untrusted, which is inherent to a
                // self-signed certificate. Callers confirm identity via the pinned thumbprint.
                return !requireTrusted;
            }

            if (result == TRUST_E_NOSIGNATURE || result == TRUST_E_BAD_DIGEST)
            {
                AppPaths.Log($"签名校验未通过（{(result == TRUST_E_BAD_DIGEST ? "摘要不匹配，文件可能被篡改" : "无签名")}）：{path}");
                return false;
            }

            AppPaths.Log($"签名校验返回未知结果 0x{result:X8}，按未签名处理：{path}");
            return false;
        }
        catch (Exception ex)
        {
            AppPaths.Log("验证签名失败: " + ex.Message);
            return false;
        }
        finally
        {
            if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
        }
    }

    public static List<Process> ProcessesRunningIn(string directory)
    {
        var result = new List<Process>();
        string full;
        try { full = Path.GetFullPath(directory).TrimEnd('\\') + "\\"; }
        catch { return result; }

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = Native.GetProcessPath(p.Id);
                if (string.IsNullOrEmpty(path)) continue;
                var dir = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(dir)) continue;
                var dirFull = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                if (dirFull.StartsWith(full, StringComparison.OrdinalIgnoreCase)) result.Add(p);
            }
            catch
            {
                // A process that exits mid-enumeration is not a reason to fail the check.
            }
        }

        return result;
    }

    public static bool IsWritable(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, ".dlssg_write_probe_" + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Unblock(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); }
        catch { /* No mark-of-the-web, or a filesystem without alternate streams. */ }
    }

    /// <summary>
    /// True when the file is provably this project's, by its bytes first:
    /// <list type="number">
    /// <item>it matches the hash recorded when we wrote it, or</item>
    /// <item>it is byte-for-byte a project build listed in a known release manifest
    /// (<see cref="PayloadCatalog.IsProjectBuild"/>), or</item>
    /// <item>it carries an intact pinned project signature — kept for releases older than the
    /// manifests, whose hashes the manager never recorded.</item>
    /// </list>
    /// NVIDIA's runtime files are never matched by catalog or signature: games ship them too, so only a
    /// recorded hash proves the manager put one there.
    /// </summary>
    public static bool IsOurs(string path, string? recordedHash)
    {
        if (!File.Exists(path)) return false;

        string hash;
        try { hash = Sha256(path); }
        catch { return false; }

        if (!string.IsNullOrEmpty(recordedHash) && string.Equals(hash, recordedHash, StringComparison.OrdinalIgnoreCase))
            return true;
        if (PayloadCatalog.IsProjectBuild(hash)) return true;
        return IsProjectSigned(path);
    }

    /// <summary>
    /// Every entry-name slot a proxy of ours could occupy: the known names, plus any file a
    /// deployment record claims. A proxy the user imported keeps its own file name — that name is
    /// the entry name the game resolves — so it appears in no fixed list and only the record vouches
    /// for it. The INI is excluded: it is a record of ours too, but it is not a proxy, and counting
    /// it would report a false "multiple proxies" fault.
    /// </summary>
    internal static IEnumerable<string> OwnedEntryNames(DeploymentInfo? prev)
    {
        var names = new List<string>(ModSource.KnownProxyNames);
        if (prev is not null)
            names.AddRange(prev.Files.Where(f => f.FileName == Path.GetFileName(f.FileName)).Select(f => f.FileName));

        return names.Where(n => !string.Equals(n, ModSource.IniName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The hash the deployment record claims for an entry name, or null when it claims none.</summary>
    internal static string? RecordedHashFor(DeploymentInfo? prev, string name)
    {
        if (prev is null) return null;

        var file = prev.Files.FirstOrDefault(f =>
            string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));
        if (file is not null) return file.Sha256;

        return string.Equals(prev.ProxyName, name, StringComparison.OrdinalIgnoreCase)
            ? prev.ProxySha256
            : null;
    }

    /// <summary>
    /// First entry name in the game directory that already holds something of ours: a DLL carrying this
    /// project's signature, a published extra (the community d3d12.dll, by hash), or a file the given
    /// record claims whose bytes still match.
    ///
    /// Scanning covers the record's own entries as well as <see cref="ModSource.KnownProxyNames"/>, so
    /// a proxy the user imported under its own name is recognised on the next status check instead of
    /// the game looking undeployed.
    /// </summary>
    public static string? FindInstalledProxy(string renderDir, DeploymentInfo? prev = null)
    {
        foreach (var name in OwnedEntryNames(prev))
        {
            var path = Path.Combine(renderDir, name);
            if (File.Exists(path) && IsOurProxyAt(renderDir, name, prev)) return name;
        }

        return null;
    }

    /// <summary>
    /// Chooses which proxy entry name to use, from the names this source can actually provide.
    ///
    /// An existing proxy of ours is reused in preference to a free name, because the game loads every
    /// entry name it recognises: installing under a second name would leave two proxies live at once.
    /// Switching names is only for the case where another product already occupies the current one.
    ///
    /// An explicit choice is honoured only when the source has a DLL for it; otherwise the caller is
    /// told, rather than a different entry being installed behind the user's back.
    /// </summary>
    internal static string? PickFreeProxy(GameEntry game, ModSource source)
    {
        var available = source.AvailableProxies;

        var wanted = game.PreferredProxy;
        if (!string.Equals(wanted, AutoProxy, StringComparison.OrdinalIgnoreCase))
        {
            return available.Contains(wanted, StringComparer.OrdinalIgnoreCase)
                ? available.First(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        // Reuse our own installation rather than picking a second, unoccupied name.
        var installed = FindInstalledProxy(game.RenderDir, game.Deployment);
        if (installed is not null) return installed;

        foreach (var name in available)
        {
            var path = Path.Combine(game.RenderDir, name);
            if (!File.Exists(path)) return name;
        }

        return available.FirstOrDefault();
    }

    /// <summary>
    /// Recognises the project's INI by its banner or its own file name. Deliberately stricter than a
    /// bare keyword search so an unrelated mod's INI sharing the slot is left alone.
    /// </summary>
    internal static bool LooksLikeProjectIni(string path)
    {
        try
        {
            var head = File.ReadLines(path).Take(4).ToList();
            if (head.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"Native\s+[0-9]+\.[0-9]+")))
                return true;

            return File.ReadAllText(path).Contains("dlssg_sm86", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Records recognized manual files without changing the game directory.</summary>
    private static DeploymentInfo ReadManualInstallation(string root, string proxy)
    {
        var proxyPath = Path.Combine(root, proxy);
        var iniPath = Path.Combine(root, ModSource.IniName);
        var iniExists = File.Exists(iniPath);

        var deployment = new DeploymentInfo
        {
            ProxyName = proxy,
            ModVersion = (iniExists ? ModSource.ReadVersion(iniPath) : null) ?? Loc.T("ModSource.UnknownVersion"),
            DeployedAt = File.GetLastWriteTime(proxyPath).ToString("yyyy-MM-dd HH:mm:ss") + Loc.T("Adopt.AdoptedSuffix"),
            ProxySha256 = Sha256(proxyPath),
            IniSha256 = iniExists ? Sha256(iniPath) : "",
            RestoreFolder = "",
            Backups = new List<BackupItem>(),
        };
        foreach (var name in ModSource.RuntimeFiles)
        {
            var path = PlanExecutor.Resolve(root, name);
            if (!File.Exists(path)) continue;
            // Adopting records a file as the manager's, so a later restore deletes it. Only bytes a known
            // release ships qualify: a genuine NVIDIA signature alone would also match a copy the game
            // installed itself. Project files from releases older than the manifests still count by
            // pinned signature.
            var hash = Sha256(path);
            var recognised = PayloadCatalog.IsReleaseFile(hash);
            if (!recognised && !name.Contains("nvngx", StringComparison.OrdinalIgnoreCase))
            {
                using var cert = ReadSignerCertificate(path, out var intact);
                recognised = ProjectSigning.IsProjectSignature(cert, intact);
            }
            if (recognised)
                deployment.Files.Add(new DeployedFile { FileName = name, Sha256 = Sha256(path), Size = new FileInfo(path).Length });
        }

        return deployment;
    }

    /// <summary>
    /// Outcome of inspecting a game folder. Produced by <see cref="Evaluate"/> and applied by
    /// <see cref="Apply"/>, which lets the expensive filesystem work run off the UI thread while the
    /// property changes land on it.
    /// </summary>
    public sealed record GameCheck(GameStatus Status, LocalizedText Detail, ProtectionReport? Protection)
    {
        public DeploymentInfo? Adoption { get; init; }
    }

    /// <summary>
    /// Inspects a game folder and reports its state. Reads the filesystem but modifies nothing, so it
    /// is safe to call from a background thread.
    ///
    /// Split from <see cref="Apply"/> because it is genuinely expensive: each candidate entry name is
    /// checked with WinVerifyTrust over a ~15 MB DLL, and matching against the record hashes the file
    /// again. Running that for several games on the UI thread froze the window for seconds.
    /// </summary>
    public static GameCheck Evaluate(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.RenderDir))
            return new GameCheck(GameStatus.Unknown, new LocalizedText("Status.NoRenderDir"), null);

        if (!Directory.Exists(game.RenderDir))
            return new GameCheck(GameStatus.Missing, new LocalizedText("Status.RenderDirMissing"), null);

        // Drives the warning banner, so it is refreshed on every check.
        var protection = AntiCheat.Scan(game.RenderDir);

        var root = game.RenderDir;
        var prev = game.Deployment;

        if (prev is null)
        {
            var found = FindInstalledProxy(root);
            if (found is null)
                return new GameCheck(GameStatus.NotDeployed, new LocalizedText("Status.NotDeployedDetail"), protection);

            var adoption = ReadManualInstallation(root, found);
            return EvaluateInstallation(root, adoption, protection) with { Adoption = adoption };
        }

        return EvaluateInstallation(root, prev, protection);
    }

    private static GameCheck EvaluateInstallation(string root, DeploymentInfo prev, ProtectionReport protection)
    {
        // Since 0.3.3 several proxies of the project may share a folder by design: the first one the
        // game loads becomes active and the rest forward their exports without installing hooks, so
        // there is no second pipeline and no crash. Extra proxies are therefore reported as a note
        // on the normal status, not as a fault. (The manager itself still installs exactly one.)
        var liveProxies = OwnedEntryNames(prev)
            .Where(n => File.Exists(Path.Combine(root, n)) && IsOurProxyAt(root, n, prev))
            .ToList();
        var standbyCount = liveProxies.Count - 1;

        var proxyPath = Path.Combine(root, prev.ProxyName);
        var iniPath = Path.Combine(root, ModSource.IniName);
        var proxyOk = File.Exists(proxyPath);
        var iniOk = File.Exists(iniPath);

        var runtimeFiles = prev.Files.Where(f => ModSource.RuntimeFiles.Contains(f.FileName)).ToArray();
        var modern = runtimeFiles.Length > 0 || new ModSource(root).UsesRuntimeDirectory;
        if (liveProxies.Count > 1 && modern)
            return new GameCheck(GameStatus.Modified, new LocalizedText("Status.Multiple040"), protection);
        if (modern)
            foreach (var name in ModSource.RequiredRuntimeFiles)
                if (!File.Exists(Path.Combine(root, name)))
                    return new GameCheck(GameStatus.Missing, new LocalizedText("Status.MissingProxy", name), protection);
        foreach (var file in runtimeFiles)
        {
            var path = Path.Combine(root, file.FileName);
            if (!File.Exists(path)) return new GameCheck(GameStatus.Missing, new LocalizedText("Status.MissingProxy", file.FileName), protection);
            if (!Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return new GameCheck(GameStatus.Modified, new LocalizedText("Status.RuntimeModified", file.FileName), protection);
        }

        if (!proxyOk || !iniOk)
        {
            // A kernel anti-cheat renames the proxy rather than deleting it, so a vanished DLL with
            // quarantined copies left behind is reported distinctly from a plain missing file.
            var quarantined = AntiCheat.FindQuarantinedCopies(root, prev.ProxyName, prev.ProxySha256);

            var detail = quarantined.Count > 0
                ? new LocalizedText("Status.Quarantined", prev.ProxyName, quarantined.Count,
                    LocalizedText.Computed(() => protection.Summary))
                : !proxyOk ? new LocalizedText("Status.MissingProxy", prev.ProxyName)
                           : new LocalizedText("Status.MissingIni", ModSource.IniName);

            return new GameCheck(GameStatus.Missing, detail, protection);
        }

        try
        {
            var proxyMatch = string.Equals(Sha256(proxyPath), prev.ProxySha256, StringComparison.OrdinalIgnoreCase);
            var iniMatch = string.Equals(Sha256(iniPath), prev.IniSha256, StringComparison.OrdinalIgnoreCase);

            var (version, proxy, deployedAt) = (prev.ModVersion, prev.ProxyName, prev.DeployedAt);
            return proxyMatch && iniMatch
                ? new GameCheck(GameStatus.Deployed, LocalizedText.Computed(() =>
                    $"Mod {version} · {proxy} · {deployedAt}" +
                    (standbyCount > 0 ? " " + Loc.T("Status.StandbyNote", standbyCount) : "")), protection)
                : new GameCheck(GameStatus.Modified,
                    new LocalizedText(proxyMatch ? "Status.IniModified" : "Status.ProxyMismatch"),
                    protection);
        }
        catch (Exception ex)
        {
            return new GameCheck(GameStatus.Unknown, new LocalizedText("Status.VerifyFailed", ex.Message), protection);
        }
    }

    /// <summary>Applies an evaluation's results. Call on the UI thread.</summary>
    public static void Apply(GameEntry game, GameCheck check)
    {
        if (game.Deployment is null && check.Adoption is not null) game.Deployment = check.Adoption;
        if (check.Protection is not null) game.Protection = check.Protection;
        game.Status = check.Status;
        game.StatusMessage = check.Detail;
    }

    /// <summary>
    /// True when an entry-name slot holds something this project put there: a DLL carrying a pinned
    /// project signature, a known community build (by hash), or a file a deployment record claims whose
    /// bytes still match.
    ///
    /// The record half exists for entries the user added themselves, and the community half for the
    /// hand-installed d3d12.dll, recognised by its pinned hash rather than by a signature it does not
    /// carry. The signature check stays as a fallback, so a deployment made before records carried
    /// hashes is recognised exactly as it was before.
    /// </summary>
    internal static bool IsOurProxyAt(string root, string name, DeploymentInfo? prev)
    {
        var recorded = prev?.Files.FirstOrDefault(f =>
            string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase));

        var path = Path.Combine(root, name);

        if (recorded is not null)
        {
            try
            {
                if (string.Equals(Sha256(path), recorded.Sha256, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch
            {
                // An unreadable file falls through to the checks below, which will also fail.
            }
        }

        try
        {
            if (File.Exists(path) && PayloadCatalog.IsProjectBuild(Sha256(path))) return true;
        }
        catch
        {
            // Unreadable: the signature check below fails the same way.
        }

        return ModFetcher.IsKnownCommunityBuild(path) || IsProjectSigned(path);
    }

    /// <summary>Evaluates and applies in one call, for callers already off the UI thread.</summary>
    public static void Check(GameEntry game) => Apply(game, Evaluate(game));

    public static string? LatestLogFile(string renderDir)
    {
        var dir = Path.Combine(renderDir, ModSource.LogDirName, "logs");
        if (!Directory.Exists(dir)) return null;
        var unifiedLog = Path.Combine(dir, "dlssg_sm86.log");
        if (File.Exists(unifiedLog)) return unifiedLog;
        return new DirectoryInfo(dir).GetFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}
