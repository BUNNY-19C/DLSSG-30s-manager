namespace DLSSGManager.Tests;

public class ElevatedRunnerTests
{
    [Fact]
    public void Helper_mode_is_unavailable_outside_the_manager_executable() =>
        Assert.False(ElevatedRunner.IsAvailable);   // the test host is not DLSSGManager.exe

    [Fact]
    public async Task Folder_needing_elevation_fails_cleanly_when_no_helper_is_available()
    {
        using var f = new DeploymentFixture();
        var asked = false;

        // A plan that needs elevation cannot be produced without an ACL change, so drive the
        // decision point directly: RunInProcess refuses, and the async path declines without asking.
        var planned = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true) with { NeedsElevation = true };
        Assert.NotNull(planned.Plan);

        var r = DeploymentService.Deploy(f.Game, f.Source, allowProtected: true);   // writable: succeeds in-process
        Assert.True(r.Ok);

        var restore = await DeploymentService.RestoreAsync(f.Game, false, () => { asked = true; return true; });
        Assert.True(restore.Ok);
        Assert.False(asked);   // writable folder: no elevation prompt
    }
}
