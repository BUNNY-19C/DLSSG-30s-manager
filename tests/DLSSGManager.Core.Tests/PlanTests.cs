using System.Text.Json;

namespace DLSSGManager.Tests;

public class PlanTests
{
    [Fact]
    public void Planning_writes_nothing()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; foreign\nFoo=1\n");
        var before = f.Snapshot();

        var outcome = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true);

        Assert.NotNull(outcome.Plan);
        Assert.Equal(before, f.Snapshot());
        Assert.Contains(outcome.Plan!.Ops, op => op is BackupOp { RelativePath: "dlssg_sm86.ini" });
        Assert.Contains(outcome.Plan.Ops, op => op is WriteFileOp { Role: FileRole.Proxy });
        Assert.Null(f.Game.Deployment);
    }

    [Fact]
    public void File_changed_between_planning_and_running_is_not_deleted()
    {
        using var f = new DeploymentFixture();
        Assert.True(DeploymentService.Deploy(f.Game, f.Source, allowProtected: true).Ok);

        var plan = DeployPlanner.PlanRestore(f.Game, removeLogs: false).Plan!;
        var swapped = DeploymentFixture.Random();
        f.WriteGameFile("version.dll", swapped);       // another tool replaces it after the check

        PlanExecutor.Execute(plan, f.Game.Deployment);

        Assert.Equal(swapped, File.ReadAllBytes(f.GamePath("version.dll")));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("..\\outside.dll")]
    [InlineData("C:\\Windows\\System32\\version.dll")]
    [InlineData("version.dll:stream")]
    public void Paths_outside_the_game_folder_are_refused(string relative)
    {
        using var f = new DeploymentFixture();
        Assert.Throws<InvalidOperationException>(() => PlanExecutor.Resolve(f.GameDir, relative));
    }

    [Fact]
    public void Writing_through_a_junction_is_refused()
    {
        using var f = new DeploymentFixture();
        using var elsewhere = new TempDir();
        var link = f.GamePath("dlssg_sm86");
        Junction.Create(link, elsewhere.Path);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);

        Assert.Throws<IOException>(() => PlanExecutor.Resolve(f.GameDir, "dlssg_sm86/runtime/nvngx_dlssg.dll"));

        var r = DeploymentService.Deploy(f.Game, f.Source, allowProtected: true);
        Assert.False(r.Ok);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Plans_round_trip_through_json()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; foreign\n");
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;

        var json = JsonSerializer.Serialize(plan);
        var back = JsonSerializer.Deserialize<DeployPlan>(json)!;

        Assert.Equal(plan.Ops.Count, back.Ops.Count);
        Assert.Equal(plan.Ops.Select(o => o.GetType()), back.Ops.Select(o => o.GetType()));
        Assert.Equal(plan.ProxyName, back.ProxyName);

        // A deserialized plan runs exactly like the original.
        var result = PlanExecutor.Execute(back, f.Game.Deployment);
        Assert.True(result.Result.Ok, result.Result.Message);
        result.ApplyTo(f.Game);
        Assert.Equal(GameStatus.Deployed, DeploymentService.Evaluate(f.Game).Status);
    }

    [Fact]
    public void Kernel_anti_cheat_blocks_planning_unless_overridden()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("EasyAntiCheat.exe", "x");

        var blocked = DeployPlanner.PlanDeploy(f.Game, f.Source);
        Assert.Null(blocked.Plan);
        Assert.True(blocked.Protection!.HasKernelAntiCheat);

        Assert.NotNull(DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan);
    }
}
