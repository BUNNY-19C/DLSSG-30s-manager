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

        await ElevatedProtocol.WriteAsync(server, new ElevatedRequest(plan, previous, AppPaths.Root), timeout.Token).ConfigureAwait(false);
        var response = await ElevatedProtocol.ReadAsync<ElevatedResponse>(server, timeout.Token).ConfigureAwait(false);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        var result = new OpResult { Ok = response.Ok, Message = response.Message };
        result.Lines.AddRange(response.Lines);
        return new ExecutionResult(result, response.RecordChanged, response.Record);
    }
}
