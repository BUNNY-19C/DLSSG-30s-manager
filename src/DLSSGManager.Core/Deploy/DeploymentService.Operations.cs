namespace DLSSGManager;

public static partial class DeploymentService
{
    /// <summary>
    /// Installs the proxy, INI and runtime directory into the game folder: plans with
    /// <see cref="DeployPlanner"/>, runs the plan with <see cref="PlanExecutor"/>, and updates the
    /// game's deployment record.
    ///
    /// <paramref name="allowProtected"/> defaults to false: a game with a kernel-mode anti-cheat will
    /// quarantine the proxy before it loads (so the mod cannot work) and may record a violation
    /// against the account, so that combination is refused unless the caller explicitly overrides.
    /// </summary>
    public static OpResult Deploy(GameEntry game, ModSource source, bool allowProtected = false, IPlanJournal? journal = null)
    {
        var planned = DeployPlanner.PlanDeploy(game, source, allowProtected);
        if (planned.Protection is not null) game.Protection = planned.Protection;
        if (planned.Plan is null) return Failed(planned.Error!);

        var executed = PlanExecutor.Execute(planned.Plan, game.Deployment, journal);
        executed.ApplyTo(game);
        return executed.Result;
    }

    /// <summary>
    /// Removes what the manager put into the game folder and puts back what it displaced. Only files
    /// provably the manager's are removed; see <see cref="DeployPlanner.PlanRestore"/>.
    /// </summary>
    public static OpResult Restore(GameEntry game, bool removeLogs, IPlanJournal? journal = null)
    {
        var planned = DeployPlanner.PlanRestore(game, removeLogs);
        if (planned.Plan is null) return Failed(planned.Error!);

        var executed = PlanExecutor.Execute(planned.Plan, game.Deployment, journal);
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
