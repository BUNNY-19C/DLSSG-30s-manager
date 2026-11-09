namespace DLSSGManager.Tests;

/// <summary>
/// Requests a hostile process of the same user could send to the elevated helper. Each must be refused
/// by <see cref="ElevatedProtocol.Validate"/> before anything runs with administrator rights.
/// </summary>
public class ElevationAttackTests
{
    private static ElevationTrust Trust(DeploymentFixture f) => new(AppPaths.RestoreRoot, new[] { f.PayloadRoot });

    private static DeployPlan DeployPlan(DeploymentFixture f) =>
        DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;

    private static string? Validate(DeploymentFixture f, DeployPlan plan, string? dataRoot = null) =>
        ElevatedProtocol.Validate(new ElevatedRequest(plan, null, dataRoot ?? AppPaths.Root), Trust(f));

    /// <summary>A directory junction: what an unprivileged attacker can actually create.</summary>
    private static bool TryLink(string link, string target)
    {
        Junction.Create(link, target);
        return Directory.Exists(link);
    }

    [Fact]
    public void The_planner_output_is_accepted()
    {
        using var f = new DeploymentFixture();
        Assert.Null(Validate(f, DeployPlan(f)));
    }

    [Fact]
    public void A_data_root_in_the_request_does_not_widen_the_trusted_folders()
    {
        using var f = new DeploymentFixture();
        using var attacker = new TempDir();
        var evil = Path.Combine(attacker.Path, "mod", "evil.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(evil)!);
        File.WriteAllBytes(evil, DeploymentFixture.Random());

        // Claims the attacker's folder as the data root, so <DataRoot>\mod would contain the source.
        var original = DeployPlan(f);
        var plan = original with
        {
            Ops = original.Ops.Select(op => op is WriteFileOp { Role: FileRole.Proxy } w
                ? w with { SourcePath = evil, ExpectedSha256 = DeploymentService.Sha256(evil) }
                : op).ToArray(),
        };

        Assert.NotNull(Validate(f, plan, dataRoot: attacker.Path));
    }

    [Fact]
    public void Copying_a_file_that_is_neither_a_release_file_nor_in_the_store_is_refused()
    {
        using var f = new DeploymentFixture();
        var original = DeployPlan(f);
        var plan = original with
        {
            Ops = original.Ops.Select(op => op is WriteFileOp { Role: FileRole.Proxy } w
                ? w with { ExpectedSha256 = new string('A', 64) }
                : op).ToArray(),
        };
        Assert.NotNull(Validate(f, plan));
    }

    [Theory]
    [InlineData("payload.bat")]
    [InlineData("..\\..\\startup.ps1")]
    [InlineData("dlssg_sm86/runtime/../../x.ini")]
    public void Text_writes_other_than_the_ini_are_refused(string target)
    {
        using var f = new DeploymentFixture();
        var original = DeployPlan(f);
        var plan = original with { Ops = original.Ops.Append(new WriteTextOp(target, "calc.exe", FileRole.Ini)).ToArray() };
        Assert.NotNull(Validate(f, plan));
    }

    [Theory]
    [InlineData("Game.exe")]
    [InlineData("save/profile.dat")]
    [InlineData("steam_api64.dll.bak")]
    [InlineData("steam_api64.dll")]
    public void Deleting_files_the_manager_does_not_own_is_refused(string target)
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile(target, DeploymentFixture.Random());
        var plan = new DeployPlan(PlanKind.Restore, "abc", f.GameDir, new FileOp[]
        {
            new DeleteFileOp(target, DeploymentService.Sha256(f.GamePath(target))),
        });
        Assert.NotNull(Validate(f, plan));
    }

    [Theory]
    [InlineData("save")]
    [InlineData("dlssg_sm86")]
    [InlineData(".")]
    public void Recursive_deletion_outside_the_log_folder_is_refused(string target)
    {
        using var f = new DeploymentFixture();
        var plan = new DeployPlan(PlanKind.Restore, "abc", f.GameDir, new FileOp[] { new DeleteDirectoryOp(target) });
        Assert.NotNull(Validate(f, plan));
    }

    [Fact]
    public void A_deploy_plan_without_a_restore_folder_is_refused()
    {
        using var f = new DeploymentFixture();
        Assert.NotNull(Validate(f, DeployPlan(f) with { RestoreFolder = "" }));
    }

    [Fact]
    public void A_restore_folder_outside_the_data_folder_is_refused()
    {
        using var f = new DeploymentFixture();
        using var other = new TempDir();
        Assert.NotNull(Validate(f, DeployPlan(f) with { RestoreFolder = other.Path }));
    }

    [Theory]
    [InlineData(@"\\localhost\C$\Windows\Temp")]
    [InlineData(@"\\?\C:\Windows\Temp")]
    [InlineData(@"\\.\C:\Windows\Temp")]
    [InlineData(@"C:\PROGRA~1")]
    public void Network_device_and_short_paths_are_refused(string renderDir)
    {
        using var f = new DeploymentFixture();
        Assert.NotNull(Validate(f, DeployPlan(f) with { RenderDir = renderDir }));
    }

    [Fact]
    public void A_render_directory_reached_through_an_ancestor_link_is_refused()
    {
        using var f = new DeploymentFixture();
        using var outside = new TempDir();
        Directory.CreateDirectory(Path.Combine(outside.Path, "Real", "Game"));

        // <links>\link -> <outside>\Real; the request names <links>\link\Game, a plain folder itself.
        using var links = new TempDir();
        var link = Path.Combine(links.Path, "link");
        if (!TryLink(link, Path.Combine(outside.Path, "Real"))) return;

        Assert.NotNull(Validate(f, DeployPlan(f) with { RenderDir = Path.Combine(link, "Game") }));
    }

    [Fact]
    public void The_render_directory_itself_becoming_a_link_is_caught_at_execution()
    {
        using var f = new DeploymentFixture();
        using var elsewhere = new TempDir();
        var plan = DeployPlan(f);

        // After planning, the folder is swapped for a link pointing somewhere else.
        Directory.Delete(f.GameDir, recursive: true);
        if (!TryLink(f.GameDir, elsewhere.Path)) return;

        var result = PlanExecutor.Execute(plan, null);

        Assert.False(result.Result.Ok);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_tampered_backup_is_not_put_back()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; the user's original\n");
        Assert.True(DeploymentService.Deploy(f.Game, f.Source, allowProtected: true).Ok);

        var backup = Assert.Single(f.Game.Deployment!.Backups);
        File.WriteAllText(backup.StoredPath, "[Evil]\nLoad=C:\\x.dll\n");   // swapped in the user-writable restore folder

        var r = DeploymentService.Restore(f.Game, removeLogs: false);

        Assert.True(r.Ok);
        Assert.False(File.Exists(f.GamePath("dlssg_sm86.ini")));
    }

    [Fact]
    public void A_forged_record_from_the_helper_is_cut_down_to_what_is_on_disk()
    {
        using var f = new DeploymentFixture();
        var plan = DeployPlan(f);
        var executed = PlanExecutor.Execute(plan, null);
        Assert.True(executed.Result.Ok);
        var genuineFiles = executed.Record!.Files.Count;

        var forged = executed.Record!;
        forged.Files.Add(new DeployedFile { FileName = "Game.exe", Sha256 = new string('B', 64) });
        forged.Backups.Add(new BackupItem { FileName = "version.dll", StoredPath = @"C:\Windows\System32\version.dll", Sha256 = new string('C', 64) });

        var accepted = ElevatedRunner.CheckRecord(plan, forged, previous: null)!;

        Assert.DoesNotContain(accepted.Files, x => x.FileName == "Game.exe");
        Assert.DoesNotContain(accepted.Backups, b => b.StoredPath.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(genuineFiles, accepted.Files.Count);
        f.Game.Deployment = accepted;
        Assert.Equal(GameStatus.Deployed, DeploymentService.Evaluate(f.Game).Status);
    }

    [Fact]
    public void A_forged_journal_is_not_replayed()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        f.WriteGameFile("save/profile.dat", "precious");
        var plan = new DeployPlan(PlanKind.Restore, "abc", f.GameDir, new FileOp[] { new DeleteDirectoryOp("save") });
        File.WriteAllText(Path.Combine(journal.Path, "abc.json"),
            System.Text.Json.JsonSerializer.Serialize(new JournalEntry { Plan = plan, StartedAt = DateTime.Now }));

        Assert.Empty(PlanRecovery.RecoverAll(journal.Path));
        Assert.True(File.Exists(f.GamePath("save/profile.dat")));
        Assert.True(File.Exists(Path.Combine(journal.Path, "abc.json.rejected")));
    }
}

public class RecoveryFailureTests
{
    [Fact]
    public void A_rollback_that_cannot_finish_keeps_track_of_backups_and_written_files()
    {
        using var f = new DeploymentFixture();
        using var journal = new TempDir();
        f.WriteGameFile("dlssg_sm86.ini", "; the user's original\n");

        var plan = DeployPlanner.PlanDeploy(f.Game, f.Source, allowProtected: true).Plan!;
        PlanExecutor.Execute(plan, null, new NeverCompletes(new FilePlanJournal(null, journal.Path)));

        // Recovery cannot put the original back (stands in for "needed admin rights").
        var ini = f.GamePath("dlssg_sm86.ini");
        File.SetAttributes(ini, FileAttributes.ReadOnly);
        try
        {
            var report = Assert.Single(PlanRecovery.RecoverAll(journal.Path));

            Assert.True(report.Failed);
            Assert.NotNull(report.Record);
            Assert.Contains(report.Record!.Backups, b => b.FileName == ModSource.IniName);
            Assert.Contains(report.Record.Files, x => x.FileName == ModSource.IniName);
        }
        finally
        {
            File.SetAttributes(ini, FileAttributes.Normal);
        }
    }

    /// <summary>Records like the real journal but never removes it — what a killed process leaves behind.</summary>
    private sealed class NeverCompletes(FilePlanJournal inner) : IPlanJournal
    {
        public void Begin(DeployPlan plan) => inner.Begin(plan);
        public void BackedUp(DeployPlan plan, BackupItem backup) => inner.BackedUp(plan, backup);
        public void Deleting(DeployPlan plan, string relativePath) => inner.Deleting(plan, relativePath);
        public void Writing(DeployPlan plan, string relativePath) => inner.Writing(plan, relativePath);
        public void Finish(DeployPlan plan, ExecutionResult result) { }
    }
}

public class ModStoreSafetyTests
{
    [Fact]
    public void Migration_moves_only_payload_files()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, ModSource.IniName), "; old");
        File.WriteAllText(Path.Combine(root.Path, "version.dll"), "dll");
        Directory.CreateDirectory(Path.Combine(root.Path, "config"));
        File.WriteAllText(Path.Combine(root.Path, "config", "mine.ini"), "user");
        Directory.CreateDirectory(Path.Combine(root.Path, "docs"));
        File.WriteAllText(Path.Combine(root.Path, "docs", "notes.md"), "user");

        ModStore.MigrateFlatLayout(root.Path);

        Assert.True(File.Exists(Path.Combine(root.Path, "config", "mine.ini")));
        Assert.True(File.Exists(Path.Combine(root.Path, "docs", "notes.md")));
        Assert.False(File.Exists(Path.Combine(root.Path, "version.dll")));
    }

    [Fact]
    public void A_migrated_release_is_never_pruned()
    {
        using var root = new TempDir();
        File.WriteAllText(Path.Combine(root.Path, ModSource.IniName), "; old");
        File.WriteAllText(Path.Combine(root.Path, "README.md"), "kept with the release");

        for (var i = 1; i <= 3; i++)
        {
            ModStore.Activate(root.Path, $"9.{i}.0", folder => File.WriteAllText(Path.Combine(folder, ModSource.IniName), "; new"));
            Thread.Sleep(20);
        }

        Assert.True(File.Exists(Path.Combine(root.Path, "versions", "legacy", "README.md")));
        Assert.Equal(new[] { "9.3.0", "9.2.0" }, ModStore.InstalledVersions(root.Path).Where(v => v.StartsWith("9.")).ToArray());
    }
}
