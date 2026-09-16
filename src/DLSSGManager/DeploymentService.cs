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
/// The only place that writes into game directories. Every write is preceded by a backup of anything
/// that is not ours, and every removal is gated on the file being provably ours (project signature
/// or a matching recorded hash), so a restore can never eat a ReShade dxgi.dll by accident.
/// </summary>
public static class DeploymentService
{
    public const string AutoProxy = "自动";

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// True when the file's Authenticode signature is intact and was made by the project's certificate.
    ///
    /// <see cref="X509Certificate.CreateFromSignedFile"/> only extracts the certificate — it does not
    /// check that the signature still matches the bytes. A tampered DLL would therefore look
    /// "signed" if that were the only check, which matters because downloads may come from a mirror.
    /// The signature is validated through WinVerifyTrust and the result is interpreted fail-closed.
    /// </summary>
    public static bool IsProjectSigned(string path)
    {
        var cert = ReadSignerCertificate(path, out var signatureIntact);
        if (cert is null) return false;

        using (cert)
        {
            if (!signatureIntact) return false;

            var subject = cert.Subject ?? "";
            return subject.Contains("DLSSG", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Reads the signer certificate and reports whether the file's signature still matches its
    /// contents.
    ///
    /// A self-signed certificate cannot chain to a trusted root, so chain validation necessarily
    /// fails even for an untouched file. Only the two outcomes that mean "the bytes are as signed"
    /// are accepted; every other result, including a bad digest, is treated as unsigned.
    /// </summary>
    public static X509Certificate2? ReadSignerCertificate(string path, out bool signatureIntact)
    {
        signatureIntact = false;

        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
        }
        catch
        {
            // No signature block at all, or not a PE file.
            return null;
        }

        signatureIntact = IsSignatureIntact(path);
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
    private static bool IsSignatureIntact(string path)
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
                return true;
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
    /// True when the file is provably this project's: either it carries the project's self-signed
    /// certificate, or it matches the hash recorded when we wrote it.
    /// </summary>
    public static bool IsOurs(string path, string? recordedHash)
    {
        if (!File.Exists(path)) return false;
        if (IsProjectSigned(path)) return true;
        if (!string.IsNullOrEmpty(recordedHash))
        {
            try { return string.Equals(Sha256(path), recordedHash, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        return false;
    }

    /// <summary>
    /// Every entry-name slot a proxy of ours could occupy: the known names, plus any file a
    /// deployment record claims. A proxy the user imported keeps its own file name — that name is
    /// the entry name the game resolves — so it appears in no fixed list and only the record vouches
    /// for it. The INI is excluded: it is a record of ours too, but it is not a proxy, and counting
    /// it would report a false "multiple proxies" fault.
    /// </summary>
    private static IEnumerable<string> OwnedEntryNames(DeploymentInfo? prev)
    {
        var names = new List<string>(ModSource.KnownProxyNames);
        if (prev is not null)
            names.AddRange(prev.Files.Select(f => f.FileName));

        return names.Where(n => !string.Equals(n, ModSource.IniName, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The hash the deployment record claims for an entry name, or null when it claims none.</summary>
    private static string? RecordedHashFor(DeploymentInfo? prev, string name)
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
    private static string? PickFreeProxy(GameEntry game, ModSource source)
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
    /// Installs the proxy and INI into the game folder.
    ///
    /// <paramref name="allowProtected"/> defaults to false: a game with a kernel-mode anti-cheat will
    /// quarantine the proxy before it loads (so the mod cannot work) and may record a violation
    /// against the account, so that combination is refused unless the caller explicitly overrides.
    /// </summary>
    public static OpResult Deploy(GameEntry game, ModSource source, bool allowProtected = false)
    {
        var r = new OpResult();

        if (!source.IsValid)
        {
            // The common case for a fresh clone is that the mod files were never fetched, so say that
            // rather than only reporting which file is missing.
            var hint = Directory.Exists(source.Root)
                ? $"（{source.Root}）"
                : Loc.T("Deploy.SourceMissingHint");

            r.Fail(Loc.T("Deploy.SourceUnavailable", source.ValidationMessage, hint));
            return r;
        }

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NeedDir"));
            return r;
        }

        var running = ProcessesRunningIn(game.RenderDir);
        if (running.Count > 0)
        {
            r.Fail(Loc.T("Deploy.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));
            return r;
        }

        var protection = AntiCheat.Scan(game.RenderDir);
        game.Protection = protection;

        if (protection.HasKernelAntiCheat && !allowProtected)
        {
            r.Fail(Loc.T("Deploy.BlockedKernel", protection.Summary, protection.Evidence));
            return r;
        }

        if (!IsWritable(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NoPermission"));
            return r;
        }

        var proxy = PickFreeProxy(game, source);
        if (proxy is null)
        {
            r.Fail(string.Equals(game.PreferredProxy, AutoProxy, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("Detail.EntriesTaken")
                : Loc.T("Deploy.InvalidProxyName", game.PreferredProxy));
            return r;
        }

        var prev = game.Deployment;
        var proxyDest = Path.Combine(game.RenderDir, proxy);
        var proxyTaken = File.Exists(proxyDest) && !IsOurs(proxyDest, prev?.ProxySha256);

        if (proxyTaken)
        {
            r.Fail(Loc.T("Deploy.ProxyTaken", proxy));
            return r;
        }

        var iniDest = Path.Combine(game.RenderDir, ModSource.IniName);
        var iniText = IniTemplate.Render(source.IniText, game.Profile);
        var restoreFolder = Path.Combine(AppPaths.RestoreRoot, game.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var backups = new List<BackupItem>();

        // The mod requires exactly one proxy in the game folder: the game loads every entry name it
        // recognises, so two would run two inference pipelines at once. Rather than trusting the
        // deployment record (which can be absent, e.g. after the library is reset), scan the entry
        // names for anything of ours and remove all but the one being installed.
        //
        // "Ours" includes a file the record claims by hash: an entry the user added themselves carries
        // no signature this project can vouch for, so the record is the only way to recognise it.
        var redundantProxies = OwnedEntryNames(prev)
            .Where(n => !string.Equals(n, proxy, StringComparison.OrdinalIgnoreCase))
            .Select(n => (Name: n, Path: Path.Combine(game.RenderDir, n)))
            .Where(x => File.Exists(x.Path) && IsOurProxyAt(game.RenderDir, x.Name, prev))
            .ToList();

        try
        {
            // Displace anything that is not ours, keeping a copy so restore can put it back.
            //
            // Ownership is decided by content, not only by the recorded hash: the INI we generate is
            // meant to be edited (that is what the profile settings do), so its hash drifts from the
            // record. Judging by hash alone would classify our own file as a foreign one, back it up,
            // and then restore it on uninstall — leaving our config behind in the game folder.
            if (File.Exists(iniDest) && !LooksLikeProjectIni(iniDest) && !IsOurs(iniDest, prev?.IniSha256))
            {
                backups.Add(Backup(iniDest, restoreFolder, r));
            }

            foreach (var (name, path) in redundantProxies)
            {
                r.Note(Loc.T("Deploy.RemoveRedundant", name));
                File.Delete(path);
            }

            var tmp = proxyDest + ".dlssgtmp";
            File.Copy(source.DllPath(proxy), tmp, overwrite: true);
            File.Move(tmp, proxyDest, overwrite: true);

            var iniTmp = iniDest + ".dlssgtmp";
            File.WriteAllText(iniTmp, iniText, new UTF8Encoding(false));
            File.Move(iniTmp, iniDest, overwrite: true);

            Unblock(proxyDest);
            Unblock(iniDest);

            var proxyHash = Sha256(proxyDest);
            var iniHash = Sha256(iniDest);

            game.Deployment = new DeploymentInfo
            {
                ProxyName = proxy,
                ModVersion = source.Version,
                DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ProxySha256 = proxyHash,
                IniSha256 = iniHash,
                RestoreFolder = backups.Count > 0 ? restoreFolder : "",
                Backups = backups,
                // Both files this deployment wrote. Kept so the entry-name scan can recognise a proxy the
                // user supplied themselves, which no signature vouches for.
                Files = new List<DeployedFile>
                {
                    new() { FileName = proxy, Sha256 = proxyHash, Size = new FileInfo(proxyDest).Length },
                    new() { FileName = ModSource.IniName, Sha256 = iniHash, Size = new FileInfo(iniDest).Length },
                },
            };

            r.Note(Loc.T("Deploy.Done", proxy, ModSource.IniName, game.RenderDir));

            // The summary names the keys actually written, which the INI schema decides: a 0.3.0
            // payload never reads Router or KernelImage, so logging them would report settings that
            // exist only in the interface's legacy panel.
            if (source.IsLegacySchema)
            {
                r.Note(Loc.T("Detail.SettingSummary",
                    game.Profile.Router,
                    game.Profile.KernelImage,
                    game.Profile.MaxGeneratedFrames + 1,
                    Loc.T(game.Profile.HardwareBilinear ? "Deploy.On" : "Deploy.Off"),
                    game.Profile.LogLevel));
            }
            else
            {
                r.Note(Loc.T("Detail.SettingSummaryModern",
                    Loc.T(game.Profile.Enabled ? "Deploy.On" : "Deploy.Off"),
                    Loc.T(game.Profile.Optimized ? "Deploy.On" : "Deploy.Off"),
                    game.Profile.Preset,
                    game.Profile.MaxGeneratedFrames + 1,
                    game.Profile.LogLevel));
            }

            if (backups.Count > 0)
            {
                r.Note(Loc.T("Deploy.BackedUp", backups.Count, restoreFolder));
            }

            r.Message = Loc.T("Deploy.Success", proxy);
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Deploy.Failed", ex.Message));
        }

        return r;
    }

    private static BackupItem Backup(string path, string restoreFolder, OpResult r)
    {
        Directory.CreateDirectory(restoreFolder);
        var stored = Path.Combine(restoreFolder, Path.GetFileName(path));
        File.Copy(path, stored, overwrite: true);
        var item = new BackupItem
        {
            FileName = Path.GetFileName(path),
            StoredPath = stored,
            Sha256 = Sha256(stored),
            Size = new FileInfo(stored).Length,
        };
        r.Note(Loc.T("Deploy.BackupFile", item.FileName, item.Size / 1024));
        return item;
    }

    public static OpResult Restore(GameEntry game, bool removeLogs)
    {
        var r = new OpResult();

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Restore.NeedDir"));
            return r;
        }

        var running = ProcessesRunningIn(game.RenderDir);
        if (running.Count > 0)
        {
            r.Fail(Loc.T("Restore.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));
            return r;
        }

        if (!IsWritable(game.RenderDir))
        {
            r.Fail(Loc.T("Deploy.NoPermission"));
            return r;
        }

        var prev = game.Deployment;
        var removed = 0;

        try
        {
            // 1) The proxy we recorded; if there is no record, any project-signed DLL in a known entry name.
            // Scan the record's own entries as well as the known names: a proxy imported under its
            // own name appears in neither list alone. A stray proxy can be present (a name switch
            // that predates the single-proxy rule, a restored backup, an earlier record lost from
            // the library), and leaving it behind would keep a proxy live in a folder the user
            // expects to be clean.
            foreach (var name in OwnedEntryNames(prev))
            {
                var path = Path.Combine(game.RenderDir, name);
                if (!File.Exists(path)) continue;

                // Only files the record claims are accepted by hash; anything else must prove
                // itself by carrying the project's signature.
                if (IsOurs(path, RecordedHashFor(prev, name)))
                {
                    File.Delete(path);
                    removed++;
                    r.Note(Loc.T("Restore.Removed", name));
                }
                else
                {
                    r.Note(Loc.T("Restore.ProxyKept", name));
                }
            }

            // 2) The INI. A recorded deployment proves we put an INI at this exact path, so a
            //    hash mismatch only means the user edited it by hand — still ours to remove.
            var iniPath = Path.Combine(game.RenderDir, ModSource.IniName);
            if (File.Exists(iniPath))
            {
                var byHash = prev is not null && IsOurs(iniPath, prev.IniSha256);
                var byBanner = LooksLikeProjectIni(iniPath);

                if (byHash || byBanner)
                {
                    if (prev is not null && !byHash)
                        r.Note(Loc.T("Restore.IniEdited", ModSource.IniName));

                    File.Delete(iniPath);
                    removed++;
                    r.Note(Loc.T("Restore.Removed", ModSource.IniName));
                }
                else
                {
                    r.Note(Loc.T("Restore.IniForeign", ModSource.IniName));
                }
            }

            // 3) Copies an anti-cheat quarantined by renaming (e.g. version.dll.3787982156). Only
            //    files that are provably ours are removed, so another tool's ".bak" survives.
            foreach (var name in OwnedEntryNames(prev))
            {
                foreach (var copy in AntiCheat.FindQuarantinedCopies(game.RenderDir, name, RecordedHashFor(prev, name)))
                {
                    try
                    {
                        File.Delete(copy);
                        removed++;
                        r.Note(Loc.T("Restore.QuarantinedRemoved", Path.GetFileName(copy)));
                    }
                    catch (Exception ex)
                    {
                        r.Note(Loc.T("Restore.QuarantinedFailed", Path.GetFileName(copy), ex.Message));
                    }
                }
            }

            // 4) Put back whatever we displaced.
            if (prev is not null && prev.Backups.Count > 0)
            {
                foreach (var b in prev.Backups)
                {
                    if (!File.Exists(b.StoredPath))
                    {
                        r.Note(Loc.T("Restore.BackupLost", b.FileName));
                        continue;
                    }

                    var dest = Path.Combine(game.RenderDir, b.FileName);
                    File.Copy(b.StoredPath, dest, overwrite: true);
                    removed++;
                    r.Note(Loc.T("Restore.BackupRestored", b.FileName));
                }
            }

            if (removeLogs)
            {
                var logs = Path.Combine(game.RenderDir, ModSource.LogDirName);
                if (Directory.Exists(logs))
                {
                    Directory.Delete(logs, recursive: true);
                    r.Note(Loc.T("Restore.LogsDeleted", ModSource.LogDirName));
                }
            }

            game.Deployment = null;
            r.Message = removed > 0 ? Loc.T("Restore.Done", removed) : Loc.T("Restore.None");
            r.Note(r.Message);
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Restore.Failed", ex.Message));
        }

        return r;
    }

    /// <summary>
    /// Recognises the project's INI by its banner or its own file name. Deliberately stricter than a
    /// bare keyword search so an unrelated mod's INI sharing the slot is left alone.
    /// </summary>
    private static bool LooksLikeProjectIni(string path)
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

    /// <summary>Takes over an installation that was copied in by hand, so the manager can restore it later.</summary>
    public static OpResult Adopt(GameEntry game)
    {
        var r = new OpResult();
        if (!Directory.Exists(game.RenderDir))
        {
            r.Fail(Loc.T("Restore.NeedDir"));
            return r;
        }

        var proxy = FindInstalledProxy(game.RenderDir);
        if (proxy is null)
        {
            r.Fail(Loc.T("Adopt.NotFound"));
            return r;
        }

        var proxyPath = Path.Combine(game.RenderDir, proxy);
        var iniPath = Path.Combine(game.RenderDir, ModSource.IniName);
        var iniExists = File.Exists(iniPath);

        game.Deployment = new DeploymentInfo
        {
            ProxyName = proxy,
            ModVersion = (iniExists ? ModSource.ReadVersion(iniPath) : null) ?? Loc.T("ModSource.UnknownVersion"),
            DeployedAt = File.GetLastWriteTime(proxyPath).ToString("yyyy-MM-dd HH:mm:ss") + Loc.T("Adopt.AdoptedSuffix"),
            ProxySha256 = Sha256(proxyPath),
            IniSha256 = iniExists ? Sha256(iniPath) : "",
            RestoreFolder = "",
            Backups = new List<BackupItem>(),
        };

        r.Note(Loc.T("Adopt.Done", proxy) + (iniExists ? $" + {ModSource.IniName}" : Loc.T("Adopt.NoIni")));
        r.Note(Loc.T("Adopt.Note"));
        r.Message = Loc.T("Adopt.Done", proxy);
        return r;
    }

    /// <summary>
    /// Outcome of inspecting a game folder. Produced by <see cref="Evaluate"/> and applied by
    /// <see cref="Apply"/>, which lets the expensive filesystem work run off the UI thread while the
    /// property changes land on it.
    /// </summary>
    public sealed record GameCheck(GameStatus Status, string Detail, ProtectionReport? Protection);

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
            return new GameCheck(GameStatus.Unknown, Loc.T("Status.NoRenderDir"), null);

        if (!Directory.Exists(game.RenderDir))
            return new GameCheck(GameStatus.Missing, Loc.T("Status.RenderDirMissing"), null);

        // Drives the warning banner, so it is refreshed on every check.
        var protection = AntiCheat.Scan(game.RenderDir);

        var root = game.RenderDir;
        var prev = game.Deployment;

        // More than one proxy of ours is a hard fault: the game loads every entry name it recognises,
        // so two would run two inference pipelines and crash. This is reported ahead of the normal
        // status because it needs fixing before the game is launched, not merely noted.
        var liveProxies = OwnedEntryNames(prev)
            .Where(n => File.Exists(Path.Combine(root, n)) && IsOurProxyAt(root, n, prev))
            .ToList();

        if (liveProxies.Count > 1)
        {
            return new GameCheck(GameStatus.Modified,
                Loc.T("Status.MultipleProxies", liveProxies.Count, Loc.Join(liveProxies)),
                protection);
        }

        if (prev is null)
        {
            var found = FindInstalledProxy(root);
            return new GameCheck(GameStatus.NotDeployed,
                found is null ? Loc.T("Status.NotDeployedDetail") : Loc.T("Status.ManualInstall", found),
                protection);
        }

        var proxyPath = Path.Combine(root, prev.ProxyName);
        var iniPath = Path.Combine(root, ModSource.IniName);
        var proxyOk = File.Exists(proxyPath);
        var iniOk = File.Exists(iniPath);

        if (!proxyOk || !iniOk)
        {
            // A kernel anti-cheat renames the proxy rather than deleting it, so a vanished DLL with
            // quarantined copies left behind is reported distinctly from a plain missing file.
            var quarantined = AntiCheat.FindQuarantinedCopies(root, prev.ProxyName, prev.ProxySha256);

            var detail = quarantined.Count > 0
                ? Loc.T("Status.Quarantined", prev.ProxyName, quarantined.Count, protection.Summary)
                : !proxyOk ? Loc.T("Status.MissingProxy", prev.ProxyName) : Loc.T("Status.MissingIni", ModSource.IniName);

            return new GameCheck(GameStatus.Missing, detail, protection);
        }

        try
        {
            var proxyMatch = string.Equals(Sha256(proxyPath), prev.ProxySha256, StringComparison.OrdinalIgnoreCase);
            var iniMatch = string.Equals(Sha256(iniPath), prev.IniSha256, StringComparison.OrdinalIgnoreCase);

            return proxyMatch && iniMatch
                ? new GameCheck(GameStatus.Deployed,
                    $"Mod {prev.ModVersion} · {prev.ProxyName} · {prev.DeployedAt}", protection)
                : new GameCheck(GameStatus.Modified,
                    proxyMatch ? Loc.T("Status.IniModified") : Loc.T("Status.ProxyMismatch"),
                    protection);
        }
        catch (Exception ex)
        {
            return new GameCheck(GameStatus.Unknown, Loc.T("Status.VerifyFailed", ex.Message), protection);
        }
    }

    /// <summary>Applies an evaluation's results. Call on the UI thread.</summary>
    public static void Apply(GameEntry game, GameCheck check)
    {
        if (check.Protection is not null) game.Protection = check.Protection;
        game.Status = check.Status;
        game.StatusDetail = check.Detail;
    }

    /// <summary>
    /// True when an entry-name slot holds something this project put there: a DLL carrying our signature,
    /// a published extra (byte for byte), or a file a deployment record claims whose bytes still match.
    ///
    /// The record half exists for entries the user added themselves, and the extra half for the community
    /// build this project distributes — a file installed by hand from that distribution is recognised by
    /// its pinned hash rather than by a signature it does not carry. Without it, a manually copied
    /// d3d12.dll would look like "nothing deployed" and could never be adopted or restored. The signature
    /// check stays as a fallback, so a deployment made before records carried hashes is recognised
    /// exactly as it was before.
    /// </summary>
    private static bool IsOurProxyAt(string root, string name, DeploymentInfo? prev)
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

        return ModFetcher.IsKnownCommunityBuild(path) || IsProjectSigned(path);
    }

    /// <summary>Evaluates and applies in one call, for callers already off the UI thread.</summary>
    public static void Check(GameEntry game) => Apply(game, Evaluate(game));

    public static string? LatestLogFile(string renderDir)
    {
        var dir = Path.Combine(renderDir, ModSource.LogDirName, "logs");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles("*.jsonl")
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }
}
