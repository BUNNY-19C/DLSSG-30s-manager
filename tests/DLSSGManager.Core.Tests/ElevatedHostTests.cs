using System.IO.Pipes;

namespace DLSSGManager.Tests;

/// <summary>
/// The elevated helper's handler over a real named pipe, in-process (no UAC; elevation only changes
/// what the helper may write).
/// </summary>
public class ElevatedHostTests
{
    private static async Task<ElevatedResponse> Exchange(ElevatedRequest request, IReadOnlyList<string> sourceRoots)
    {
        var name = Shell.NewHelperPipeName();
        await using var server = ElevatedProtocol.CreateServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));

        var helper = ElevatedHost.HandleAsync(client, new ElevationTrust(AppPaths.RestoreRoot, sourceRoots), timeout.Token);
        await ElevatedProtocol.WriteAsync(server, request, timeout.Token);
        var response = await ElevatedProtocol.ReadAsync<ElevatedResponse>(server, timeout.Token);
        await helper;
        return response;
    }

    [Fact]
    public async Task Deploy_and_restore_run_through_the_helper()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; another tool\n");
        var before = f.Snapshot();

        var deploy = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;
        var deployed = await Exchange(new ElevatedRequest(deploy, null, AppPaths.Root), new[] { f.PayloadRoot });

        Assert.True(deployed.Ok, deployed.Message + "\n" + string.Join("\n", deployed.Lines));
        Assert.True(deployed.RecordChanged);
        f.Game.Deployment = deployed.Record;
        Assert.Equal(GameStatus.Deployed, DeploymentService.Evaluate(f.Game).Status);

        var restore = DeployPlanner.PlanRestore(f.Game, removeLogs: false).Plan!;
        var restored = await Exchange(new ElevatedRequest(restore, f.Game.Deployment, AppPaths.Root), new[] { f.PayloadRoot });

        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(FilePlanJournal.Pending(Path.Combine(AppPaths.Root, "journal")));
    }

    [Fact]
    public async Task Plan_with_a_source_outside_the_mod_store_is_refused()
    {
        using var f = new DeploymentFixture();
        using var elsewhere = new TempDir();
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;

        var response = await Exchange(new ElevatedRequest(plan, null, AppPaths.Root), new[] { elsewhere.Path });

        Assert.False(response.Ok);
        Assert.False(response.RecordChanged);
        Assert.Empty(Directory.EnumerateFileSystemEntries(f.GameDir));
    }

    [Fact]
    public async Task Plan_targeting_a_system_folder_is_refused()
    {
        using var f = new DeploymentFixture();
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!
            with { RenderDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows) };

        var response = await Exchange(new ElevatedRequest(plan, null, AppPaths.Root), new[] { f.PayloadRoot });

        Assert.False(response.Ok);
    }
}
