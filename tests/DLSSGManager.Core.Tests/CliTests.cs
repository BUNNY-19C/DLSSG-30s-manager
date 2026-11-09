using DLSSGManager.Cli;

namespace DLSSGManager.Tests;

/// <summary>
/// The command line end to end, in-process. Each test uses its own data folder (the library and
/// journal live there), so these run serially.
/// </summary>
[Collection("AppDataRoot")]
public class CliTests : IDisposable
{
    private readonly TempDir _home = new();
    private readonly string? _previousHome = Environment.GetEnvironmentVariable(AppPaths.RootOverrideVariable);

    public CliTests() => Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, _home.Path);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, _previousHome);
        _home.Dispose();
    }

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await new DLSSGManager.Cli.Cli(output, error).RunAsync(args);
        return (code, output.ToString(), error.ToString());
    }

    private static string LibraryFile => AppPaths.LibraryFile;

    [Fact]
    public async Task Usage_and_bad_arguments_return_2()
    {
        Assert.Equal(ExitCode.Usage, (await Run()).Code);
        Assert.Equal(ExitCode.Ok, (await Run("--help")).Code);
        Assert.Equal(ExitCode.Usage, (await Run("frobnicate")).Code);
        Assert.Equal(ExitCode.Usage, (await Run("deploy")).Code);                // missing --game
        Assert.Equal(ExitCode.Usage, (await Run("add", "--dir")).Code);          // missing value
        Assert.Equal(ExitCode.Usage, (await Run("add", "stray")).Code);          // positional
        Assert.Equal(ExitCode.Usage, (await Run("list", "--x", "1", "--x", "2")).Code);
    }

    [Fact]
    public async Task Add_deploy_status_restore_round_trip()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("dlssg_sm86.ini", "; another tool\n");
        var before = f.Snapshot();

        var added = await Run("add", "--dir", f.GameDir, "--name", "Round Trip");
        Assert.Equal(ExitCode.Ok, added.Code);
        var id = added.Out.Trim();

        Assert.Equal(ExitCode.Failed, (await Run("status", "--game", "Round Trip")).Code);   // not deployed yet

        var deployed = await Run("deploy", "--game", id, "--mod", f.PayloadRoot);
        Assert.True(deployed.Code == ExitCode.Ok, deployed.Err + deployed.Out);
        Assert.NotNull(Assert.Single(LibraryStore.Load(LibraryFile).Games).Deployment);

        var status = await Run("status", "--game", "Round Trip");
        Assert.Equal(ExitCode.Ok, status.Code);
        Assert.Contains("Deployed", status.Out);

        Assert.Equal(ExitCode.Ok, (await Run("restore", "--game", "Round Trip", "--remove-logs")).Code);
        Assert.Equal(before, f.Snapshot());
        Assert.Null(Assert.Single(LibraryStore.Load(LibraryFile).Games).Deployment);
    }

    [Fact]
    public async Task Kernel_anti_cheat_blocks_deploy_with_exit_5()
    {
        using var f = new DeploymentFixture();
        f.WriteGameFile("BEService_x64.exe", "x");
        await Run("add", "--dir", f.GameDir, "--name", "Protected");

        var blocked = await Run("deploy", "--game", "Protected", "--mod", f.PayloadRoot);
        Assert.Equal(ExitCode.Blocked, blocked.Code);
        Assert.False(File.Exists(f.GamePath("version.dll")));

        var allowed = await Run("deploy", "--game", "Protected", "--mod", f.PayloadRoot, "--allow-anticheat");
        Assert.Equal(ExitCode.Ok, allowed.Code);
    }

    [Fact]
    public async Task Unknown_or_ambiguous_game_returns_3()
    {
        using var a = new DeploymentFixture();
        using var b = new DeploymentFixture();
        await Run("add", "--dir", a.GameDir, "--name", "Same");
        await Run("add", "--dir", b.GameDir, "--name", "Same");

        Assert.Equal(ExitCode.NotFound, (await Run("deploy", "--game", "Nope")).Code);
        Assert.Equal(ExitCode.NotFound, (await Run("deploy", "--game", "Same")).Code);
    }

    [Fact]
    public async Task Adding_the_same_folder_twice_fails()
    {
        using var f = new DeploymentFixture();
        Assert.Equal(ExitCode.Ok, (await Run("add", "--dir", f.GameDir)).Code);
        Assert.Equal(ExitCode.Failed, (await Run("add", "--dir", f.GameDir)).Code);
    }

    [Fact]
    public async Task Unreadable_library_stops_the_cli()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LibraryFile)!);
        File.WriteAllText(LibraryFile, "not json");

        int code;
        using (new FileStream(LibraryFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            code = (await Run("list")).Code;

        Assert.Equal(ExitCode.Failed, code);
        Assert.Equal("not json", File.ReadAllText(LibraryFile));
    }

    [Fact]
    public async Task Recover_with_nothing_pending_succeeds()
    {
        var r = await Run("recover");
        Assert.Equal(ExitCode.Ok, r.Code);
    }

    [Fact]
    public async Task Verify_checks_the_real_payload_when_present()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DLSSGManager.sln"))) dir = dir.Parent;
        var mod = dir is null ? null : Path.Combine(dir.FullName, "mod");
        if (mod is null || !new ModSource(ModStore.ActiveDirectory(mod)).IsValid) return;

        var r = await Run("verify", "--mod", mod);
        Assert.True(r.Code == ExitCode.Ok, r.Err);
    }
}

[CollectionDefinition("AppDataRoot", DisableParallelization = true)]
public class AppDataRootCollection;
