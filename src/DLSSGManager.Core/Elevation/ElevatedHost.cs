namespace DLSSGManager;

/// <summary>
/// The elevated helper's work, kept in Core so it can be exercised over a pipe without UAC.
///
/// Reads one <see cref="ElevatedRequest"/>, validates it (<see cref="ElevatedProtocol.Validate"/>),
/// runs the plan journalled, and writes one <see cref="ElevatedResponse"/>. The data folder comes from
/// the request — an elevated process may run under a different profile — and is applied before
/// anything is logged.
/// </summary>
public static class ElevatedHost
{
    /// <summary>Mod store folders a plan's sources may come from.</summary>
    public static IReadOnlyList<string> SourceRoots() =>
        new[] { ModSourceLocator.ResolveTarget(null), AppPaths.UserModDir, AppPaths.BundledModDir }
            .Concat(ModSourceLocator.FindRepositoryRoot() is { } repo ? new[] { Path.Combine(repo, "mod") } : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Helper-mode entry point: connects to <paramref name="pipeName"/>, handles one request and returns
    /// a process exit code. 2 = bad arguments, 3 = could not connect, 4 = pipe failure, 1 = plan failed.
    /// </summary>
    public static async Task<int> RunAsync(string? pipeName)
    {
        if (!Shell.IsHelperPipeName(pipeName)) return 2;

        await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName!,
            System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        try
        {
            await pipe.ConnectAsync(10_000, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            return 3;
        }

        try
        {
            var response = await HandleAsync(pipe, timeout.Token).ConfigureAwait(false);
            return response.Ok ? 0 : 1;
        }
        catch
        {
            return 4;
        }
    }

    public static async Task<ElevatedResponse> HandleAsync(Stream pipe, CancellationToken ct,
        Func<IReadOnlyList<string>>? sourceRoots = null)
    {
        ElevatedResponse response;
        try
        {
            var request = await ElevatedProtocol.ReadAsync<ElevatedRequest>(pipe, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.DataRoot) && Path.IsPathFullyQualified(request.DataRoot))
                Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, request.DataRoot);

            var invalid = ElevatedProtocol.Validate(request, (sourceRoots ?? SourceRoots)());
            if (invalid is not null)
            {
                AppPaths.Log("[elevated] 拒绝执行: " + invalid);
                response = new ElevatedResponse(false, Loc.T("Elevated.Failed", invalid), new List<string>(), false, request.Previous);
            }
            else
            {
                var journal = new FilePlanJournal(request.Previous, Path.Combine(request.DataRoot, "journal"));
                var result = PlanExecutor.Execute(request.Plan, request.Previous, journal);
                response = new ElevatedResponse(result.Result.Ok, result.Result.Message, result.Result.Lines,
                    result.RecordChanged, result.Record);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = new ElevatedResponse(false, Loc.T("Elevated.Failed", ex.Message), new List<string>(), false, null);
        }

        await ElevatedProtocol.WriteAsync(pipe, response, ct).ConfigureAwait(false);
        return response;
    }
}
