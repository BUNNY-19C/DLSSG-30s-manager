namespace DLSSGManager.Cli;

public sealed partial class Cli
{
    private static string ModRoot(Arguments a, AppData data) =>
        a.Optional("mod") is { } explicitRoot ? Path.GetFullPath(explicitRoot) : ModSourceLocator.ResolveTarget(data.ModSourcePath);

    private int DeployGame(Arguments a)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;
        if (RequireGame(data, a) is not { } game) return ExitCode.NotFound;

        var allowProtected = a.Flag("allow-anticheat");
        var source = new ModSource(BuildCatalog.SourceDirectory(ModRoot(a, data), game.Profile.RuntimeModel));
        var planned = DeployPlanner.PlanDeploy(game, source, allowProtected);
        if (planned.Protection is not null) game.Protection = planned.Protection;

        if (planned.Plan is null)
        {
            error.WriteLine(planned.Error);
            return planned.Protection?.HasKernelAntiCheat == true && !allowProtected ? ExitCode.Blocked : ExitCode.Failed;
        }
        return ExecutePlan(data, game, planned);
    }

    private int RestoreGame(Arguments a)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;
        if (RequireGame(data, a) is not { } game) return ExitCode.NotFound;

        var planned = DeployPlanner.PlanRestore(game, a.Flag("remove-logs"));
        if (planned.Plan is null)
        {
            error.WriteLine(planned.Error);
            return ExitCode.Failed;
        }
        return ExecutePlan(data, game, planned);
    }

    /// <summary>
    /// Runs a plan in this process, journalled. A folder needing administrator rights is reported, not
    /// elevated: a script decides for itself whether to run the command elevated.
    /// </summary>
    private int ExecutePlan(AppData data, GameEntry game, PlanOutcome planned)
    {
        if (planned.NeedsElevation)
        {
            error.WriteLine(Loc.T("Deploy.NoPermission"));
            return ExitCode.NeedsElevation;
        }

        var result = PlanExecutor.Execute(planned.Plan!, game.Deployment, new FilePlanJournal(game.Deployment));
        result.ApplyTo(game);
        foreach (var line in result.Result.Lines) output.WriteLine(line);

        // Record first, then report: a deployment the library does not know about cannot be restored.
        if (!SaveLibrary(data)) return ExitCode.Failed;
        if (!result.Result.Ok)
        {
            error.WriteLine(result.Result.Message);
            return ExitCode.Failed;
        }
        output.WriteLine(result.Result.Message);
        return ExitCode.Ok;
    }

    private async Task<int> FetchAsync(Arguments a, CancellationToken ct)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var package = a.Optional("package")?.ToLowerInvariant() switch
        {
            null or "any" => PayloadPackage.Any,
            "full" => PayloadPackage.Full,
            "base" => PayloadPackage.Base,
            var other => throw new ArgumentException("未知的 --package：" + other),
        };

        var result = await ModFetcher.DownloadIntoAsync(ModRoot(a, data), new SyncProgress<string>(output.WriteLine), ct,
            a.Optional("source"), package: package);
        return Report(result);
    }

    private async Task<int> ImportAsync(Arguments a, CancellationToken ct)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var zip = Path.GetFullPath(a.Required("zip"));
        var result = await ModFetcher.ImportArchiveAsync(zip, ModRoot(a, data), new SyncProgress<string>(output.WriteLine), ct);
        return Report(result);
    }

    private int Report(OpResult result)
    {
        foreach (var line in result.Lines) output.WriteLine(line);
        if (!result.Ok)
        {
            error.WriteLine(result.Message);
            return ExitCode.Failed;
        }
        output.WriteLine(result.Message);
        return ExitCode.Ok;
    }

    private int VerifyPayload(Arguments a)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var dir = ModStore.ActiveDirectory(ModRoot(a, data));
        var source = new ModSource(dir);
        if (!source.IsValid)
        {
            error.WriteLine($"{dir}: {source.ValidationMessage}");
            return ExitCode.NotFound;
        }

        var manifest = PayloadCatalog.Find(source.Version);
        if (manifest is null)
        {
            error.WriteLine($"{dir}: 版本 {source.Version} 没有内置清单，无法逐文件核对。");
            return ExitCode.Failed;
        }

        var problem = PayloadVerifier.CheckInstalled(dir, manifest);
        if (problem is not null)
        {
            error.WriteLine(problem);
            return ExitCode.Failed;
        }
        output.WriteLine(Loc.T("Fetch.HashOk", manifest.Version) + $"（{dir}）");
        return ExitCode.Ok;
    }
}
