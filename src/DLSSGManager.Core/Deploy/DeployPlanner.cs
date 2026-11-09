using System.Globalization;

namespace DLSSGManager;

/// <summary>
/// Decides what a deploy or restore will do to a game folder. Reads the folder, the payload and the
/// deployment record; writes nothing.
///
/// Every ownership decision lives here: a file is only scheduled for overwrite-without-backup or for
/// deletion when it is provably the manager's (a recorded hash or a pinned project signature, see
/// <see cref="DeploymentService.IsOurs"/>). Deletions carry the hash observed while planning, so the
/// executor will not remove a file that changed in between.
/// </summary>
public static class DeployPlanner
{
    public static PlanOutcome PlanDeploy(GameEntry game, ModSource source, bool allowProtected = false)
    {
        if (!source.IsValid)
        {
            // The common case for a fresh clone is that the mod files were never fetched, so say that
            // rather than only reporting which file is missing.
            var hint = Directory.Exists(source.Root)
                ? $"（{source.Root}）"
                : Loc.T("Deploy.SourceMissingHint");
            return PlanOutcome.Failure(Loc.T("Deploy.SourceUnavailable", source.ValidationMessage, hint));
        }

        // A payload from a known release must still be exactly that release: the mod folder is
        // user-writable, and these files are about to be loaded into a game.
        var manifest = PayloadCatalog.Find(source.Version);
        if (manifest is not null && PayloadVerifier.CheckInstalled(source.Root, manifest) is { } tampered)
            return PlanOutcome.Failure(Loc.T("Deploy.PayloadTampered", manifest.Version, tampered));

        var configurationError = source.ConfigurationError(game.Profile);
        if (configurationError is not null || source.RuntimeModel != BuildCatalog.Normalize(game.Profile.RuntimeModel))
            return PlanOutcome.Failure(configurationError ?? Loc.T("Manage.BuildMismatch"));

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
            return PlanOutcome.Failure(Loc.T("Deploy.NeedDir"));

        var running = DeploymentService.ProcessesRunningIn(game.RenderDir);
        if (running.Count > 0)
            return PlanOutcome.Failure(Loc.T("Deploy.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));

        var protection = AntiCheat.Scan(game.RenderDir);
        if (protection.HasKernelAntiCheat && !allowProtected)
            return PlanOutcome.Failure(Loc.T("Deploy.BlockedKernel", protection.Summary, protection.Evidence))
                with { Protection = protection };

        // Planning only reads, so a folder this process cannot write still gets a full plan; the caller
        // decides whether to run it through the elevated helper.
        var writable = DeploymentService.IsWritable(game.RenderDir);

        try
        {
            var outcome = BuildDeploy(game, source) with { Protection = protection };
            return outcome.Plan is not null && !writable ? outcome with { NeedsElevation = true } : outcome;
        }
        catch (Exception ex)
        {
            return PlanOutcome.Failure(Loc.T("Deploy.Failed", ex.Message)) with { Protection = protection };
        }
    }

    private static PlanOutcome BuildDeploy(GameEntry game, ModSource source)
    {
        var root = game.RenderDir;
        var proxy = DeploymentService.PickFreeProxy(game, source);
        if (proxy is null)
        {
            return PlanOutcome.Failure(string.Equals(game.PreferredProxy, GameEntry.AutoProxy, StringComparison.OrdinalIgnoreCase)
                ? Loc.T("Detail.EntriesTaken")
                : Loc.T("Deploy.InvalidProxyName", game.PreferredProxy));
        }

        var prev = game.Deployment;
        var proxyDest = Path.Combine(root, proxy);
        if (File.Exists(proxyDest) && !DeploymentService.IsOurs(proxyDest, prev?.ProxySha256))
            return PlanOutcome.Failure(Loc.T("Deploy.ProxyTaken", proxy));

        // The mod requires exactly one proxy in the game folder: the game loads every entry name it
        // recognises, so two would run two inference pipelines at once. Rather than trusting the
        // deployment record (which can be absent, e.g. after the library is reset), scan the entry
        // names for anything of ours. "Ours" includes a file the record claims by hash: an entry the
        // user added themselves carries no signature this project can vouch for.
        var redundantProxies = DeploymentService.OwnedEntryNames(prev)
            .Where(n => !string.Equals(n, proxy, StringComparison.OrdinalIgnoreCase))
            .Where(n => File.Exists(Path.Combine(root, n)) && DeploymentService.IsOurProxyAt(root, n, prev))
            .ToList();

        if (prev is not null && prev.RuntimeModel != source.RuntimeModel && !source.RequiresSingleProxy &&
            redundantProxies.Any(n => !File.Exists(source.DllPath(n))))
            return PlanOutcome.Failure(Loc.T("Manage.StandbyMismatch"));

        var iniText = IniTemplate.Render(source.IniText, game.Profile, source.Supports041);
        var ops = new List<FileOp>();
        var backups = 0;

        // Runtime directory: anything in a slot that is not ours is backed up before being replaced.
        var runtimeFiles = source.DeployableRuntimeFiles.ToArray();
        foreach (var name in runtimeFiles)
        {
            var dest = Path.Combine(root, name);
            if (File.Exists(dest) && !DeploymentService.IsOurs(dest, DeploymentService.RecordedHashFor(prev, name)))
            {
                ops.Add(new BackupOp(name));
                backups++;
            }
            ops.Add(Write(name, Path.Combine(source.Root, name), FileRole.Runtime));
        }

        // Changing back to a legacy source removes only runtime files the previous record owns.
        foreach (var name in ModSource.RuntimeFiles.Except(runtimeFiles))
        {
            var dest = Path.Combine(root, name);
            var hash = DeploymentService.RecordedHashFor(prev, name);
            if (hash is not null && File.Exists(dest) && DeploymentService.Sha256(dest).Equals(hash, StringComparison.OrdinalIgnoreCase))
                ops.Add(new DeleteFileOp(name, hash));
        }

        // Extra proxies of ours: on 0.2.x and 0.4.x payloads two live pipelines crash, so they are
        // trimmed. On 0.3.3+ the loader runs them as standby forwarders by design, so there they stay,
        // refreshed and recorded with the deployment so a later restore still removes them.
        foreach (var name in redundantProxies)
        {
            if (source.RequiresSingleProxy)
            {
                // A file this manager wrote (recorded hash) or built upstream can go; a community build the
                // user installed by hand is recognised but not ours, so it is set aside first and comes back
                // on restore.
                var path = Path.Combine(root, name);
                var hash = DeploymentService.Sha256(path);
                var wroteIt = string.Equals(hash, DeploymentService.RecordedHashFor(prev, name), StringComparison.OrdinalIgnoreCase) ||
                              PayloadCatalog.IsProjectBuild(hash) || DeploymentService.IsProjectSigned(path);
                if (!wroteIt)
                {
                    ops.Add(new BackupOp(name));
                    backups++;
                }
                ops.Add(new DeleteFileOp(name, hash, NoteSpec.Of("Deploy.RemoveRedundant", name)));
                continue;
            }

            ops.Add(File.Exists(source.DllPath(name))
                ? Write(name, source.DllPath(name), FileRole.Standby)
                : new RecordExistingOp(name, FileRole.Standby));
            ops.Add(new NoteOp(NoteSpec.Of("Deploy.StandbyKept", name)));
        }

        // The INI we generate is meant to be edited, so its hash drifts from the record; ownership is
        // decided by content as well. Judging by hash alone would back up our own file and put it back
        // on uninstall.
        var iniDest = Path.Combine(root, ModSource.IniName);
        if (File.Exists(iniDest) && !DeploymentService.LooksLikeProjectIni(iniDest) &&
            !DeploymentService.IsOurs(iniDest, prev?.IniSha256))
        {
            ops.Add(new BackupOp(ModSource.IniName));
            backups++;
        }

        ops.Add(Write(proxy, source.DllPath(proxy), FileRole.Proxy));
        ops.Add(new WriteTextOp(ModSource.IniName, iniText, FileRole.Ini));

        ops.Add(new NoteOp(NoteSpec.Of("Deploy.Done", proxy, ModSource.IniName, root)));
        ops.Add(new NoteOp(SettingSummary(game.Profile, source)));

        var restoreFolder = Path.Combine(AppPaths.RestoreRoot, game.Id, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        if (backups > 0)
            ops.Add(new NoteOp(NoteSpec.Of("Deploy.BackedUp", backups.ToString(CultureInfo.InvariantCulture), restoreFolder)));

        return PlanOutcome.Success(new DeployPlan(PlanKind.Deploy, game.Id, root, ops)
        {
            ProxyName = proxy,
            ModVersion = source.Version,
            RuntimeModel = source.RuntimeModel,
            AppliedConfiguration = ConfigurationState.Applied(game),
            RestoreFolder = restoreFolder,
        });
    }

    /// <summary>
    /// The summary names the keys actually written, which the INI schema decides: a 0.3.0 payload never
    /// reads Router or KernelImage, so logging them would report settings that exist only in the
    /// interface's legacy panel.
    /// </summary>
    private static NoteSpec SettingSummary(GameProfile p, ModSource source)
    {
        static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        string OnOff(bool value) => Loc.T(value ? "Deploy.On" : "Deploy.Off");

        if (source.IsLegacySchema)
            return NoteSpec.Of("Detail.SettingSummary", p.Router, p.KernelImage, N(p.MaxGeneratedFrames + 1),
                OnOff(p.HardwareBilinear), N(p.LogLevel));

        if (source.UsesRuntimeDirectory)
            return NoteSpec.Of("Detail.SettingSummary040", p.Engine, OnOff(p.Enabled), N(p.OptimizedTier),
                source.Supports041 && p.Preset == "Auto" ? "A" : p.Preset,
                N(p.UsesXeSS ? (source.Supports041 ? p.XeSSMaxGeneratedFrames + 1 : 2) : p.MaxGeneratedFrames + 1),
                p.SuperResolutionMode, N(p.LogLevel));

        return NoteSpec.Of("Detail.SettingSummaryModern", OnOff(p.Enabled), N(p.OptimizedTier), p.Preset,
            N(p.MaxGeneratedFrames + 1), N(p.LogLevel));
    }

    public static PlanOutcome PlanRestore(GameEntry game, bool removeLogs)
    {
        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
            return PlanOutcome.Failure(Loc.T("Restore.NeedDir"));

        var running = DeploymentService.ProcessesRunningIn(game.RenderDir);
        if (running.Count > 0)
            return PlanOutcome.Failure(Loc.T("Restore.GameRunning", Loc.Join(running.Select(p => p.ProcessName))));

        try
        {
            return PlanOutcome.Success(BuildRestore(game, removeLogs)) with
            {
                NeedsElevation = !DeploymentService.IsWritable(game.RenderDir),
            };
        }
        catch (Exception ex)
        {
            return PlanOutcome.Failure(Loc.T("Restore.Failed", ex.Message));
        }
    }

    private static DeployPlan BuildRestore(GameEntry game, bool removeLogs)
    {
        var root = game.RenderDir;
        var prev = game.Deployment;
        var ops = new List<FileOp>();
        var modernLayout = ModSource.RuntimeFiles.Any(p => File.Exists(Path.Combine(root, p))) ||
            prev?.Files.Any(f => ModSource.RuntimeFiles.Contains(f.FileName)) == true ||
            new ModSource(root).UsesRuntimeDirectory;

        // Runtime components: NVIDIA signatures alone do not prove manager ownership; the deployment
        // hash (or the project's pinned signature) does.
        foreach (var name in ModSource.RuntimeFiles)
            AddOwnedDeletion(ops, root, name, DeploymentService.RecordedHashFor(prev, name));

        // Proxies: the recorded one, plus any project-signed or record-claimed DLL in an entry slot. A
        // stray proxy (an old name switch, a lost record) would otherwise stay live in a folder the user
        // expects to be clean.
        foreach (var name in DeploymentService.OwnedEntryNames(prev))
            AddOwnedDeletion(ops, root, name, DeploymentService.RecordedHashFor(prev, name));

        // The INI. A recorded deployment proves we put an INI at this exact path, so a hash mismatch only
        // means the user edited it by hand — still ours to remove.
        var iniPath = Path.Combine(root, ModSource.IniName);
        if (File.Exists(iniPath))
        {
            var byHash = prev is not null && DeploymentService.IsOurs(iniPath, prev.IniSha256);
            var byBanner = DeploymentService.LooksLikeProjectIni(iniPath);
            if (byHash || byBanner)
            {
                if (prev is not null && !byHash)
                    ops.Add(new NoteOp(NoteSpec.Of("Restore.IniEdited", ModSource.IniName)));
                ops.Add(new DeleteFileOp(ModSource.IniName, DeploymentService.Sha256(iniPath),
                    NoteSpec.Of("Restore.Removed", ModSource.IniName)));
            }
            else
            {
                ops.Add(new NoteOp(NoteSpec.Of("Restore.IniForeign", ModSource.IniName)));
            }
        }

        // Copies an anti-cheat quarantined by renaming (e.g. version.dll.3787982156). Only files that
        // are provably ours are listed, so another tool's ".bak" survives.
        foreach (var name in DeploymentService.OwnedEntryNames(prev))
        {
            foreach (var copy in AntiCheat.FindQuarantinedCopies(root, name, DeploymentService.RecordedHashFor(prev, name)))
            {
                var file = Path.GetFileName(copy);
                ops.Add(new DeleteFileOp(file, DeploymentService.Sha256(copy),
                    NoteSpec.Of("Restore.QuarantinedRemoved", file), BestEffort: true,
                    FailureNoteKey: "Restore.QuarantinedFailed"));
            }
        }

        // Put back whatever we displaced. Whether the slot is free is decided when the step runs,
        // after the deletions above.
        foreach (var backup in prev?.Backups ?? new List<BackupItem>())
        {
            var runtime = ModSource.RuntimeFiles.Contains(backup.FileName);
            ops.Add(new RestoreBackupOp(backup, backup.FileName, SkipIfExists: runtime));
        }

        if (removeLogs)
        {
            var logs = modernLayout ? ModSource.LogDirName + "/logs" : ModSource.LogDirName;
            if (Directory.Exists(Path.Combine(root, logs)))
                ops.Add(new DeleteDirectoryOp(logs, NoteSpec.Of("Restore.LogsDeleted", ModSource.LogDirName)));
        }

        ops.Add(new PruneEmptyDirectoriesOp(RuntimeDirectories()));

        return new DeployPlan(PlanKind.Restore, game.Id, root, ops);
    }

    /// <summary>A write step pinned to the payload file's current bytes.</summary>
    private static WriteFileOp Write(string relative, string source, FileRole role) =>
        new(relative, source, role, DeploymentService.Sha256(source));

    private static void AddOwnedDeletion(List<FileOp> ops, string root, string name, string? recordedHash)
    {
        var path = Path.Combine(root, name);
        if (!File.Exists(path)) return;

        if (DeploymentService.IsOurs(path, recordedHash))
            ops.Add(new DeleteFileOp(name, DeploymentService.Sha256(path), NoteSpec.Of("Restore.Removed", name)));
        else
            ops.Add(new NoteOp(NoteSpec.Of("Restore.ProxyKept", name)));
    }

    /// <summary>Runtime folders deepest first, then the log folder and the mod folder itself.</summary>
    private static string[] RuntimeDirectories() =>
        ModSource.RuntimeFiles
            .Select(p => p[..p.LastIndexOf('/')])
            .Append(ModSource.LogDirName + "/logs")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Append(ModSource.LogDirName)
            .ToArray();
}
