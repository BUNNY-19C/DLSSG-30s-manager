using System.IO.Pipes;

namespace DLSSGManager.Tests;

/// <summary>
/// Crash recovery: a journal that simulates the process dying at a given step, then
/// <see cref="PlanRecovery"/> on the next start.
/// </summary>
public class RecoveryTests
{
    /// <summary>Records like the real journal, then "crashes" (throws) when the Nth write begins.</summary>
    private sealed class CrashingJournal(FilePlanJournal inner, int crashOnWrite) : IPlanJournal
    {
        private int _writes;
        public void Begin(DeployPlan plan) => inner.Begin(plan);
        public void BackedUp(DeployPlan plan, BackupItem backup) => inner.BackedUp(plan, backup);
        public void Deleting(DeployPlan plan, string relativePath) => inner.Deleting(plan, relativePath);

        public void Writing(DeployPlan plan, string relativePath)
        {
            inner.Writing(plan, relativePath);
            if (++_writes == crashOnWrite) throw new SimulatedCrash();
        }

        // A crashed process never gets to clean up its journal.
        public void Complete(DeployPlan plan) { }
    }

    private sealed class SimulatedCrash : Exception;

    private static (DeployPlan Plan, string JournalDir) CrashDeploy(DeploymentFixture f, TempDir journal, int crashOnWrite)
    {
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;
        var crashing = new CrashingJournal(new FilePlanJournal(f.Game.Deployment, journal.Path), crashOnWrite);

        // The executor catches the "crash" like any failure; what matters is the journal left on disk,
        // exactly as a killed process would leave it. Let the next write go through first so the
        // destination really is partially written when the run stops.
        PlanExecutor.Execute(plan, f.Game.Deployment, crashing);
        return (plan, journal.Path);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Interrupted_deploy_into_a_clean_folder_is_rolled_back(int crashOnWrite)
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        f.WriteGameFile("Game.exe", DeploymentFixture.Random());
        var before = f.Snapshot();

        CrashDeploy(f, journal, crashOnWrite);
        Assert.Single(FilePlanJournal.Pending(journal.Path));

        var report = Assert.Single(PlanRecovery.RecoverAll(journal.Path));

        Assert.True(report.RolledBack);
        Assert.False(report.Failed);
        Assert.Null(report.Record);
        Assert.Equal(before, f.Snapshot());
        Assert.Empty(FilePlanJournal.Pending(journal.Path));
    }

    [Fact]
    public void Interrupted_deploy_puts_displaced_originals_back()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        f.WriteGameFile("dlssg_sm86.ini", "; another tool\nFoo=1\n");
        f.WriteGameFile("dlssg_sm86/runtime/nvngx_dlssg.dll", DeploymentFixture.Random());
        var before = f.Snapshot();

        CrashDeploy(f, journal, crashOnWrite: 6); // after the runtime files, while writing the proxy

        PlanRecovery.RecoverAll(journal.Path);

        Assert.Equal(before, f.Snapshot());
    }

    [Fact]
    public void Interrupted_redeploy_keeps_the_previous_record_and_files()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        Assert.True(DeploymentService.Deploy(f.Game, f.Source, allowProtected: true).Ok);
        var record = f.Game.Deployment;
        var before = f.Snapshot();

        f.Game.Profile.MaxGeneratedFrames = 1;
        CrashDeploy(f, journal, crashOnWrite: 2);

        var report = Assert.Single(PlanRecovery.RecoverAll(journal.Path));

        // Files the earlier deployment owns are not removed: the game still has a working install.
        // The record comes back from the journal on disk, so compare by content.
        Assert.NotNull(report.Record);
        Assert.Equal(record!.ProxyName, report.Record!.ProxyName);
        Assert.Equal(record.ProxySha256, report.Record.ProxySha256);
        Assert.Equal(record.Files.Select(x => x.Sha256), report.Record.Files.Select(x => x.Sha256));
        foreach (var path in before.Keys) Assert.True(File.Exists(f.GamePath(path)), path);
    }

    [Fact]
    public void A_file_replaced_after_the_crash_is_not_removed_by_recovery()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        CrashDeploy(f, journal, crashOnWrite: 2);

        // Something else wrote to the slot before the manager started again.
        var foreign = DeploymentFixture.Random();
        var first = ModSource.RequiredRuntimeFiles[0];
        f.WriteGameFile(first, foreign);

        PlanRecovery.RecoverAll(journal.Path);

        Assert.Equal(foreign, File.ReadAllBytes(f.GamePath(first)));
    }

    [Fact]
    public void Interrupted_restore_is_finished()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        f.WriteGameFile("dlssg_sm86.ini", "; original\n");
        var before = f.Snapshot();
        Assert.True(DeploymentService.Deploy(f.Game, f.Source, allowProtected: true).Ok);

        var plan = DeployPlanner.PlanRestore(f.Game, removeLogs: false).Plan!;
        var crashing = new CrashOnDelete(new FilePlanJournal(f.Game.Deployment, journal.Path), crashOnDelete: 2);
        PlanExecutor.Execute(plan, f.Game.Deployment, crashing);

        var report = Assert.Single(PlanRecovery.RecoverAll(journal.Path));

        Assert.False(report.RolledBack);
        Assert.Null(report.Record);
        Assert.Equal(before, f.Snapshot());
    }

    private sealed class CrashOnDelete(FilePlanJournal inner, int crashOnDelete) : IPlanJournal
    {
        private int _deletes;
        public void Begin(DeployPlan plan) => inner.Begin(plan);
        public void BackedUp(DeployPlan plan, BackupItem backup) => inner.BackedUp(plan, backup);
        public void Writing(DeployPlan plan, string relativePath) => inner.Writing(plan, relativePath);

        public void Deleting(DeployPlan plan, string relativePath)
        {
            inner.Deleting(plan, relativePath);
            if (++_deletes == crashOnDelete) throw new SimulatedCrash();
        }

        public void Complete(DeployPlan plan) { }
    }

    [Fact]
    public void Completed_runs_leave_no_journal()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();

        Assert.True(DeploymentService.Deploy(f.Game, f.Source, true, new FilePlanJournal(f.Game.Deployment, journal.Path)).Ok);
        Assert.True(DeploymentService.Restore(f.Game, false, new FilePlanJournal(f.Game.Deployment, journal.Path)).Ok);

        Assert.Empty(FilePlanJournal.Pending(journal.Path));
    }

    [Fact]
    public void Unreadable_journal_is_kept_aside()
    {
        using var journal = new TempDir();
        File.WriteAllText(Path.Combine(journal.Path, "abc.json"), "{ broken");

        Assert.Empty(PlanRecovery.RecoverAll(journal.Path));
        Assert.True(File.Exists(Path.Combine(journal.Path, "abc.json.unreadable")));
    }

    [Fact]
    public void Payload_swapped_after_planning_is_not_installed()
    {
        using var f = new DeploymentFixture();
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;
        File.WriteAllBytes(Path.Combine(f.PayloadRoot, "version.dll"), DeploymentFixture.Random());

        var result = PlanExecutor.Execute(plan, f.Game.Deployment);

        Assert.False(result.Result.Ok);
        Assert.False(File.Exists(f.GamePath("version.dll")));
    }
}

public class ElevatedProtocolTests
{
    private static ElevatedRequest Request(DeploymentFixture f, string dataRoot, Func<DeployPlan, DeployPlan>? change = null)
    {
        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;
        return new ElevatedRequest(change is null ? plan : change(plan), null, dataRoot);
    }

    [Fact]
    public void A_normal_plan_from_the_mod_store_is_accepted()
    {
        using var f = new DeploymentFixture();
        Assert.Null(ElevatedProtocol.Validate(Request(f, AppPaths.Root), new[] { f.PayloadRoot }));
    }

    [Fact]
    public void Sources_outside_the_mod_store_are_refused()
    {
        using var f = new DeploymentFixture();
        using var other = new TempDir();
        Assert.NotNull(ElevatedProtocol.Validate(Request(f, AppPaths.Root), new[] { other.Path }));
    }

    [Fact]
    public void System_folders_are_refused_as_targets()
    {
        using var f = new DeploymentFixture();
        var request = Request(f, AppPaths.Root, p => p with { RenderDir = Environment.GetFolderPath(Environment.SpecialFolder.System) });
        Assert.NotNull(ElevatedProtocol.Validate(request, new[] { f.PayloadRoot }));
    }

    [Fact]
    public void Drive_roots_are_refused_as_targets()
    {
        using var f = new DeploymentFixture();
        var request = Request(f, AppPaths.Root, p => p with { RenderDir = Path.GetPathRoot(f.GameDir)! });
        Assert.NotNull(ElevatedProtocol.Validate(request, new[] { f.PayloadRoot }));
    }

    [Fact]
    public void Unpinned_writes_are_refused()
    {
        using var f = new DeploymentFixture();
        var request = Request(f, AppPaths.Root, p => p with
        {
            Ops = p.Ops.Select(op => op is WriteFileOp w ? w with { ExpectedSha256 = "" } : op).ToArray(),
        });
        Assert.NotNull(ElevatedProtocol.Validate(request, new[] { f.PayloadRoot }));
    }

    [Fact]
    public void Backups_from_outside_the_restore_folder_are_refused()
    {
        using var f = new DeploymentFixture();
        var plan = new DeployPlan(PlanKind.Restore, "g", f.GameDir, new FileOp[]
        {
            new RestoreBackupOp(new BackupItem { FileName = "x.dll", StoredPath = @"C:\Windows\System32\kernel32.dll" }, "x.dll", false),
        });
        Assert.NotNull(ElevatedProtocol.Validate(new ElevatedRequest(plan, null, AppPaths.Root), new[] { f.PayloadRoot }));
    }

    [Theory]
    [InlineData("DLSSGManager.Elevated.0123456789abcdef0123456789abcdef", true)]
    [InlineData("DLSSGManager.Elevated.0123456789abcdef0123456789abcde", false)]
    [InlineData("DLSSGManager.Elevated.0123456789abcdef0123456789abcdeX", false)]
    [InlineData("DLSSGManager.Elevated.x\" & calc & \"", false)]
    [InlineData("other", false)]
    public void Only_generated_pipe_names_are_accepted(string name, bool valid) =>
        Assert.Equal(valid, Shell.IsHelperPipeName(name));

    [Fact]
    public async Task Messages_round_trip_over_a_pipe()
    {
        using var f = new DeploymentFixture();
        var name = Shell.NewHelperPipeName();
        await using var server = ElevatedProtocol.CreateServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Task.WhenAll(server.WaitForConnectionAsync(timeout.Token), client.ConnectAsync(timeout.Token));
        var request = Request(f, AppPaths.Root);

        // Write and read concurrently: a pipe's buffer is smaller than a plan, so a write that waits
        // for the reader would never finish if the read only started afterwards.
        var write = ElevatedProtocol.WriteAsync(server, request, timeout.Token);
        var read = ElevatedProtocol.ReadAsync<ElevatedRequest>(client, timeout.Token);
        await Task.WhenAll(write, read);
        var received = await read;

        Assert.Equal(request.Plan.Ops.Count, received.Plan.Ops.Count);
        Assert.Equal(request.Plan.RenderDir, received.Plan.RenderDir);
        Assert.Null(ElevatedProtocol.Validate(received, new[] { f.PayloadRoot }));
    }

    [Fact]
    public async Task Oversized_message_is_refused()
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(ElevatedProtocol.MaxMessageBytes + 1));
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => ElevatedProtocol.ReadAsync<ElevatedRequest>(stream, CancellationToken.None));
    }
}
