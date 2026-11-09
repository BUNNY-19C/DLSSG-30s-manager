namespace DLSSGManager.Tests;

/// <summary>
/// Deploy → restore must leave a game folder exactly as it was found, and must never remove or
/// overwrite a file that is not provably the manager's.
/// </summary>
public class DeploymentRoundTripTests
{
    private static OpResult Deploy(DeploymentFixture f) =>
        DeploymentService.Deploy(f.Game, f.Source, allowProtected: true);

    private static void AssertOk(OpResult r) => Assert.True(r.Ok, r.Message + "\n" + string.Join("\n", r.Lines));

    [Fact]
    public void Clean_folder_round_trips_to_empty()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("Game.exe", DeploymentFixture.Random());
        var before = f.Snapshot();

        AssertOk(Deploy(f));

        var deployed = f.Snapshot();
        Assert.Contains("version.dll", deployed.Keys);
        Assert.Contains("dlssg_sm86.ini", deployed.Keys);
        foreach (var runtime in ModSource.RequiredRuntimeFiles) Assert.Contains(runtime, deployed.Keys);
        Assert.NotNull(f.Game.Deployment);
        Assert.Equal("version.dll", f.Game.Deployment!.ProxyName);
        Assert.Equal(2 + ModSource.RequiredRuntimeFiles.Length, f.Game.Deployment.Files.Count);
        Assert.False(ConfigurationState.NeedsApply(f.Game));

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));

        Assert.Equal(before, f.Snapshot());
        Assert.Empty(f.Directories());
        Assert.Null(f.Game.Deployment);
    }

    [Fact]
    public void Deployed_ini_carries_the_profile()
    {
        using var f = new DeploymentFixture();
        f.Game.Profile.MaxGeneratedFrames = 5;
        f.Game.Profile.OptimizedTier = 3;

        AssertOk(Deploy(f));

        var ini = File.ReadAllText(f.GamePath("dlssg_sm86.ini"));
        Assert.Contains("MaxGeneratedFrames=5", ini);
        Assert.Contains("Optimized=3", ini);
    }

    [Fact]
    public void Foreign_ini_is_backed_up_and_restored_and_foreign_proxy_is_never_touched()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; some other tool's settings\nFoo=1\n");
        f.WriteGameFile("dxgi.dll", DeploymentFixture.Random()); // e.g. ReShade
        var before = f.Snapshot();

        AssertOk(Deploy(f));

        Assert.Equal(before["dxgi.dll"], f.Snapshot()["dxgi.dll"]);
        var backup = Assert.Single(f.Game.Deployment!.Backups);
        Assert.Equal("dlssg_sm86.ini", backup.FileName);
        Assert.True(File.Exists(backup.StoredPath));

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));

        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Redeploy_carries_backups_so_restore_still_returns_the_original()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; original\nFoo=1\n");
        var before = f.Snapshot();

        AssertOk(Deploy(f));
        f.Game.Profile.MaxGeneratedFrames = 2;
        AssertOk(Deploy(f));
        f.Game.Profile.LogLevel = 3;
        AssertOk(Deploy(f));

        Assert.Single(f.Game.Deployment!.Backups);

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Foreign_runtime_file_is_backed_up_and_restored()
    {
        using var f = new DeploymentFixture();
        const string runtime = "dlssg_sm86/runtime/nvngx_dlssg.dll";
        f.WriteGameFile(runtime, DeploymentFixture.Random());
        var before = f.Snapshot();

        AssertOk(Deploy(f));
        Assert.NotEqual(before[runtime], f.Snapshot()[runtime]);
        Assert.Contains(f.Game.Deployment!.Backups, b => b.FileName == runtime);

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Occupied_entry_name_is_skipped_on_auto()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("version.dll", DeploymentFixture.Random()); // another product's proxy
        var before = f.Snapshot();

        AssertOk(Deploy(f));

        Assert.Equal("winmm.dll", f.Game.Deployment!.ProxyName);
        Assert.Equal(before["version.dll"], f.Snapshot()["version.dll"]);

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));
        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Explicit_entry_name_held_by_another_product_fails_without_changes()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("version.dll", DeploymentFixture.Random());
        f.Game.PreferredProxy = "version.dll";
        var before = f.Snapshot();

        var r = Deploy(f);

        Assert.False(r.Ok);
        Assert.Equal(before, f.Snapshot());
        Assert.Null(f.Game.Deployment);
    }

    [Fact]
    public void Proxy_changed_after_deploy_is_kept_on_restore()
    {
        using var f = new DeploymentFixture();
        AssertOk(Deploy(f));
        var replaced = DeploymentFixture.Random();
        f.WriteGameFile("version.dll", replaced);

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));

        Assert.True(File.Exists(f.GamePath("version.dll")));
        Assert.Equal(replaced, File.ReadAllBytes(f.GamePath("version.dll")));
        Assert.False(File.Exists(f.GamePath("dlssg_sm86.ini")));
    }

    [Fact]
    public void Hand_edited_ini_is_still_removed()
    {
        using var f = new DeploymentFixture();
        AssertOk(Deploy(f));
        File.AppendAllText(f.GamePath("dlssg_sm86.ini"), "\n; tweaked by hand\n");

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));

        Assert.False(File.Exists(f.GamePath("dlssg_sm86.ini")));
    }

    [Fact]
    public void Logs_are_only_removed_when_asked()
    {
        using var f = new DeploymentFixture();
        AssertOk(Deploy(f));
        f.WriteGameFile("dlssg_sm86/logs/dlssg_sm86.log", "log");

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));
        Assert.True(File.Exists(f.GamePath("dlssg_sm86/logs/dlssg_sm86.log")));

        AssertOk(Deploy(f));
        AssertOk(DeploymentService.Restore(f.Game, removeLogs: true));
        Assert.Empty(f.Snapshot());
        Assert.Empty(f.Directories());
    }

    [Fact]
    public void Locked_runtime_file_fails_before_anything_is_written()
    {
        using var f = new DeploymentFixture();
        const string runtime = "dlssg_sm86/kernels/fg_kernels.dll";
        f.WriteGameFile(runtime, DeploymentFixture.Random());
        var before = f.Snapshot();

        OpResult r;
        using (new FileStream(f.GamePath(runtime), FileMode.Open, FileAccess.Read, FileShare.Read))
            r = Deploy(f);

        Assert.False(r.Ok);
        Assert.Equal(before, f.Snapshot());
        Assert.Null(f.Game.Deployment);
    }

    [Fact]
    public void Super_resolution_files_are_deployed_and_removed_when_present()
    {
        using var f = new DeploymentFixture(superResolution: true);
        f.Game.Profile.SuperResolutionMode = "M-INT8";

        AssertOk(Deploy(f));
        foreach (var sr in ModSource.SuperResolutionFiles) Assert.True(File.Exists(f.GamePath(sr)));

        AssertOk(DeploymentService.Restore(f.Game, removeLogs: false));
        Assert.Empty(f.Snapshot());
    }

    [Fact]
    public void Status_check_reports_deployed_then_modified()
    {
        using var f = new DeploymentFixture();
        AssertOk(Deploy(f));

        Assert.Equal(GameStatus.Deployed, DeploymentService.Evaluate(f.Game).Status);

        File.AppendAllText(f.GamePath("dlssg_sm86.ini"), "\n");
        Assert.Equal(GameStatus.Modified, DeploymentService.Evaluate(f.Game).Status);

        File.Delete(f.GamePath("version.dll"));
        Assert.Equal(GameStatus.Missing, DeploymentService.Evaluate(f.Game).Status);
    }
}
