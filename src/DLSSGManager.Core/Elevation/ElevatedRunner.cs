namespace DLSSGManager;

/// <summary>
/// Runs a plan through the elevated helper. The manager itself is never elevated: only this one plan
/// runs with administrator rights, in a process that exits as soon as it is done.
/// </summary>
public static class ElevatedRunner
{
    /// <summary>
    /// True when this process can act as its own helper: it is the manager executable (which handles
    /// <see cref="Shell.ElevatedSwitch"/>), not a test host or another program hosting Core.
    /// </summary>
    public static bool IsAvailable =>
        string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "DLSSGManager", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sends <paramref name="plan"/> to a freshly elevated helper and waits for the result. Returns null
    /// when the user declined the UAC prompt.
    /// </summary>
    public static async Task<ExecutionResult?> RunAsync(DeployPlan plan, DeploymentInfo? previous, CancellationToken ct)
    {
        var pipeName = Shell.NewHelperPipeName();
        await using var server = ElevatedProtocol.CreateServer(pipeName);

        using var process = Shell.StartElevatedHelper(pipeName);
        if (process is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        var connect = server.WaitForConnectionAsync(timeout.Token);
        var exited = process.WaitForExitAsync(timeout.Token);
        if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == exited)
            throw new InvalidOperationException("Helper exited before connecting (code " + process.ExitCode + ").");
        await connect.ConfigureAwait(false);

        // Another process of this user could race the helper to the pipe; only talk to the one we started.
        if (ElevatedProtocol.ClientProcessId(server) != process.Id)
            throw new InvalidOperationException("Unexpected process connected to the helper pipe.");

        await ElevatedProtocol.WriteAsync(server, new ElevatedRequest(plan, previous, AppPaths.Root), timeout.Token).ConfigureAwait(false);
        var response = await ElevatedProtocol.ReadAsync<ElevatedResponse>(server, timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        var result = new OpResult { Ok = response.Ok, Message = response.Message };
        result.Lines.AddRange(response.Lines);
        return new ExecutionResult(result, response.RecordChanged, CheckRecord(plan, response.Record, previous));
    }

    /// <summary>
    /// Accepts the record the helper reports only as far as the files on disk confirm it: every file it
    /// lists must be a path the plan wrote and still have the reported hash, and every backup must be one
    /// this plan or the previous record made. Anything else is dropped, so a forged record cannot make a
    /// later restore delete or copy files the manager never wrote.
    /// </summary>
    internal static DeploymentInfo? CheckRecord(DeployPlan plan, DeploymentInfo? reported, DeploymentInfo? previous,
        string? trustedRestoreRoot = null)
    {
        if (reported is null) return null;

        var written = plan.Ops.Select(op => op switch
            {
                WriteFileOp w => w.RelativePath,
                WriteTextOp t => t.RelativePath,
                RecordExistingOp e => e.RelativePath,
                _ => null,
            })
            .Where(p => p is not null)
            .Concat(previous?.Files.Select(f => f.FileName) ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool OnDisk(string relative, string sha)
        {
            try
            {
                var path = PlanExecutor.Resolve(plan.RenderDir, relative);
                return File.Exists(path) && DeploymentService.Sha256(path).Equals(sha, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        var restoreRoot = Path.GetFullPath(trustedRestoreRoot ?? AppPaths.RestoreRoot).TrimEnd('\\') + "\\";
        var knownBackups = (previous?.Backups ?? new List<BackupItem>()).Select(b => b.StoredPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planFolder = Path.GetFullPath(plan.RestoreFolder).TrimEnd('\\') + "\\";

        var files = reported.Files.Where(f => written.Contains(f.FileName) && OnDisk(f.FileName, f.Sha256)).ToList();
        var backups = reported.Backups.Where(b =>
            {
                var stored = Path.GetFullPath(b.StoredPath);
                return stored.StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase) &&
                       (knownBackups.Contains(b.StoredPath) || stored.StartsWith(planFolder, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        var proxyOk = files.Any(f => f.FileName == reported.ProxyName && f.Sha256 == reported.ProxySha256);
        var iniOk = files.Any(f => f.FileName == ModSource.IniName && f.Sha256 == reported.IniSha256);

        return new DeploymentInfo
        {
            RuntimeModel = reported.RuntimeModel,
            AppliedConfiguration = reported.AppliedConfiguration,
            ProxyName = proxyOk ? reported.ProxyName : previous?.ProxyName ?? plan.ProxyName,
            ModVersion = reported.ModVersion,
            DeployedAt = reported.DeployedAt,
            ProxySha256 = proxyOk ? reported.ProxySha256 : "",
            IniSha256 = iniOk ? reported.IniSha256 : "",
            RestoreFolder = reported.RestoreFolder,
            Backups = backups,
            Files = files,
        };
    }
}
