using System.IO.Pipes;

namespace DLSSGManager.Tests;

/// <summary>
/// The elevated helper's handler over a real named pipe, in-process (no UAC; elevation only changes
/// what the helper may write).
/// </summary>
public class ElevatedHostTests
{
    private static async Task<ElevatedResponse> Exchange(ElevatedRequest request, IReadOnlyList<string> sourceRoots,
        string? restoreRoot = null)
    {
        var name = Shell.NewHelperPipeName();
        await using var server = ElevatedProtocol.CreateServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));

        var helper = ElevatedHost.HandleAsync(client, new ElevationTrust(restoreRoot ?? AppPaths.RestoreRoot, sourceRoots), timeout.Token);
        await ElevatedProtocol.WriteAsync(server, request, timeout.Token);
        var response = await ElevatedProtocol.ReadAsync<ElevatedResponse>(server, timeout.Token);
        await helper;
        return response;
    }

    [Fact]
    public async Task Deploy_and_restore_run_through_the_helper()
    {
        using var f = new DeploymentFixture();
        using var dir = new TempDir();
        f.WriteGameFile("dlssg_sm86.ini", "; another tool\n");
        var before = f.Snapshot();
        var data = new AppData();
        data.Games.Add(f.Game);
        var restoreRoot = dir.File("restore");
        using var writer = LibraryStore.BeginWrite(data, dir.File("library.json"));

        var deploy = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!
            with { RestoreFolder = Path.Combine(restoreRoot, f.Game.Id) };
        var deployed = await Exchange(new ElevatedRequest(deploy, null, dir.Path), new[] { f.PayloadRoot }, restoreRoot);

        Assert.True(deployed.Ok, deployed.Message + "\n" + string.Join("\n", deployed.Lines));
        Assert.True(deployed.RecordChanged);
        Assert.Single(FilePlanJournal.Pending(writer.JournalDirectory));
        f.Game.Deployment = ElevatedRunner.CheckRecord(deploy, deployed.Record, null, restoreRoot);
        Assert.True(writer.Save().Ok);
        Assert.Empty(FilePlanJournal.Pending(writer.JournalDirectory));
        Assert.Equal(GameStatus.Deployed, DeploymentService.Evaluate(f.Game).Status);

        var restore = DeployPlanner.PlanRestore(f.Game, removeLogs: false).Plan!;
        var restored = await Exchange(new ElevatedRequest(restore, f.Game.Deployment, dir.Path), new[] { f.PayloadRoot }, restoreRoot);

        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(before, f.Snapshot());
        Assert.Single(FilePlanJournal.Pending(writer.JournalDirectory));
        f.Game.Deployment = restored.Record;
        Assert.True(writer.Save().Ok);
        Assert.Empty(FilePlanJournal.Pending(writer.JournalDirectory));
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
