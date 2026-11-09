namespace DLSSGManager;

public static partial class DeploymentService
{
    /// <summary>
    /// Installs the proxy, INI and runtime directory into the game folder: plans with
    /// <see cref="DeployPlanner"/>, runs the plan with <see cref="PlanExecutor"/>, and updates the
    /// game's deployment record. Runs in this process only; a folder that needs administrator rights
    /// fails here — use <see cref="DeployAsync"/> to go through the elevated helper.
    ///
    /// <paramref name="allowProtected"/> defaults to false: a game with a kernel-mode anti-cheat will
    /// quarantine the proxy before it loads (so the mod cannot work) and may record a violation
    /// against the account, so that combination is refused unless the caller explicitly overrides.
    /// </summary>
    public static OpResult Deploy(GameEntry game, ModSource source, bool allowProtected = false, IPlanJournal? journal = null)
    {
        var planned = DeployPlanner.PlanDeploy(game, source, allowProtected);
        if (planned.Protection is not null) game.Protection = planned.Protection;
        return RunInProcess(game, planned, journal);
    }

    /// <summary>
    /// Removes what the manager put into the game folder and puts back what it displaced. Only files
    /// provably the manager's are removed; see <see cref="DeployPlanner.PlanRestore"/>.
    /// </summary>
    public static OpResult Restore(GameEntry game, bool removeLogs, IPlanJournal? journal = null) =>
        RunInProcess(game, DeployPlanner.PlanRestore(game, removeLogs), journal);

    /// <summary>
    /// <see cref="Deploy"/> for the interface: journalled for crash recovery, and when the folder needs
    /// administrator rights, <paramref name="confirmElevation"/> is asked and the plan runs in the
    /// elevated helper. Planning happens off the calling thread; the record is applied by the caller's
    /// continuation.
    /// </summary>
    public static async Task<OpResult> DeployAsync(GameEntry game, ModSource source, bool allowProtected,
        Func<bool> confirmElevation, CancellationToken ct = default)
    {
        var planned = await Task.Run(() => DeployPlanner.PlanDeploy(game, source, allowProtected), ct).ConfigureAwait(true);
        if (planned.Protection is not null) game.Protection = planned.Protection;
        return await RunAsync(game, planned, confirmElevation, ct).ConfigureAwait(true);
    }

    /// <summary><see cref="Restore"/> for the interface; see <see cref="DeployAsync"/>.</summary>
    public static async Task<OpResult> RestoreAsync(GameEntry game, bool removeLogs, Func<bool> confirmElevation,
        CancellationToken ct = default)
    {
        var planned = await Task.Run(() => DeployPlanner.PlanRestore(game, removeLogs), ct).ConfigureAwait(true);
        return await RunAsync(game, planned, confirmElevation, ct).ConfigureAwait(true);
    }

    private static OpResult RunInProcess(GameEntry game, PlanOutcome planned, IPlanJournal? journal)
    {
        if (planned.Plan is null) return Failed(planned.Error!);
        if (planned.NeedsElevation) return Failed(Loc.T("Deploy.NoPermission"));

        var executed = PlanExecutor.Execute(planned.Plan, game.Deployment, journal);
        executed.ApplyTo(game);
        return executed.Result;
    }

    private static async Task<OpResult> RunAsync(GameEntry game, PlanOutcome planned, Func<bool> confirmElevation, CancellationToken ct)
    {
        if (planned.Plan is null) return Failed(planned.Error!);
        var previous = game.Deployment;

        ExecutionResult? executed;
        if (!planned.NeedsElevation)
        {
            var journal = new FilePlanJournal(previous);
            executed = await Task.Run(() => PlanExecutor.Execute(planned.Plan, previous, journal), ct).ConfigureAwait(true);
        }
        else
        {
            if (!ElevatedRunner.IsAvailable) return Failed(Loc.T("Deploy.NoPermission"));
            if (!confirmElevation()) return Failed(Loc.T("Elevated.Cancelled"));
            try
            {
                executed = await ElevatedRunner.RunAsync(planned.Plan, previous, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Failed(Loc.T("Elevated.Failed", ex.Message));
            }
            if (executed is null) return Failed(Loc.T("Elevated.Cancelled"));
        }

        executed.ApplyTo(game);
        return executed.Result;
    }

    private static OpResult Failed(string message)
    {
        var r = new OpResult();
        r.Fail(message);
        return r;
    }
}
