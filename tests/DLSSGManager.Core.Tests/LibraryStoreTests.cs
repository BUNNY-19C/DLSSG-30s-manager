namespace DLSSGManager.Tests;

public class LibraryStoreTests
{
    private static AppData LibraryWithDeployment(string name)
    {
        var data = new AppData();
        data.Games.Add(new GameEntry
        {
            Name = name,
            RenderDir = @"C:\Games\" + name,
            Deployment = new DeploymentInfo
            {
                ProxyName = "version.dll",
                ProxySha256 = new string('A', 64),
                Backups = { new BackupItem { FileName = "dlssg_sm86.ini", StoredPath = @"C:\restore\x.ini" } },
            },
        });
        return data;
    }

    [Fact]
    public void Save_then_load_round_trips_deployment_records()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");

        Assert.True(LibraryStore.Save(LibraryWithDeployment("Alpha"), file).Ok);
        var loaded = LibraryStore.LoadWithStatus(file);

        Assert.Equal(LibraryLoadOutcome.Loaded, loaded.Outcome);
        var game = Assert.Single(loaded.Data.Games);
        Assert.Equal("Alpha", game.Name);
        Assert.Equal("version.dll", game.Deployment!.ProxyName);
        Assert.Single(game.Deployment.Backups);
    }

    [Fact]
    public void Missing_library_starts_empty()
    {
        using var dir = new TempDir();
        var loaded = LibraryStore.LoadWithStatus(dir.File("library.json"));

        Assert.Equal(LibraryLoadOutcome.Created, loaded.Outcome);
        Assert.Empty(loaded.Data.Games);
    }

    [Fact]
    public void Corrupt_library_is_set_aside_and_never_overwritten()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");
        const string corrupt = "{ \"Games\": [ { \"Name\": \"Alpha\", "; // truncated mid-write
        File.WriteAllText(file, corrupt);

        var loaded = LibraryStore.LoadWithStatus(file);

        Assert.Equal(LibraryLoadOutcome.Quarantined, loaded.Outcome);
        Assert.NotNull(loaded.SetAsidePath);
        Assert.Equal(corrupt, File.ReadAllText(loaded.SetAsidePath!));

        // The startup path saves right after loading; that must not destroy the set-aside original.
        Assert.True(LibraryStore.Save(loaded.Data, file).Ok);
        Assert.Equal(corrupt, File.ReadAllText(loaded.SetAsidePath!));
    }

    [Fact]
    public void Corrupt_library_falls_back_to_previous_good_copy()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");

        Assert.True(LibraryStore.Save(LibraryWithDeployment("Alpha"), file).Ok);
        var latest = LibraryStore.Load(file);
        Assert.Single(latest.Games).Name = "Beta";
        Assert.True(LibraryStore.Save(latest, file).Ok);   // Alpha rotates into .bak
        File.WriteAllText(file, "not json");

        var loaded = LibraryStore.LoadWithStatus(file);

        Assert.Equal(LibraryLoadOutcome.RecoveredFromBackup, loaded.Outcome);
        Assert.Equal("Alpha", Assert.Single(loaded.Data.Games).Name);
        Assert.True(File.Exists(loaded.SetAsidePath));
    }

    [Fact]
    public void Unmovable_corrupt_library_blocks_saving()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");
        File.WriteAllText(file, "not json");

        LibraryLoadResult loaded;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read)) // blocks the rename
            loaded = LibraryStore.LoadWithStatus(file);

        Assert.Equal(LibraryLoadOutcome.ReadOnly, loaded.Outcome);
        Assert.False(LibraryStore.Save(loaded.Data, file).Ok);
        Assert.Equal("not json", File.ReadAllText(file));
    }

    [Fact]
    public void Stale_snapshot_cannot_overwrite_a_newer_save()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");
        Assert.True(LibraryStore.Save(LibraryWithDeployment("Alpha"), file).Ok);
        var stale = LibraryStore.Load(file);
        var current = LibraryStore.Load(file);
        Assert.Single(current.Games).Name = "From CLI";
        Assert.True(LibraryStore.Save(current, file).Ok);
        Assert.Single(stale.Games).Name = "From GUI";
        Assert.False(LibraryStore.Save(stale, file).Ok);
        Assert.Equal("From CLI", Assert.Single(LibraryStore.Load(file).Games).Name);
        Assert.Equal("Alpha", Assert.Single(LibraryStore.Load(file + LibraryStore.BackupSuffix).Games).Name);
    }

    [Fact]
    public void Active_operation_excludes_other_writers_until_it_saves()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");
        var first = LibraryStore.Load(file);
        var second = LibraryStore.Load(file);
        using (var writer = LibraryStore.BeginWrite(first, file))
        {
            Assert.False(LibraryStore.Save(second, file).Ok);
            Assert.True(writer.Save().Ok);
        }
        Assert.False(LibraryStore.Save(second, file).Ok);
        Assert.True(LibraryStore.Save(LibraryStore.Load(file), file).Ok);
    }

    [Fact]
    public void Legacy_optimized_flag_migrates_to_tier()
    {
        using var dir = new TempDir();
        var file = dir.File("library.json");
        File.WriteAllText(file, """
            { "Games": [ { "Name": "Old", "Profile": { "Optimized": false, "Preset": "b", "MaxGeneratedFrames": 9 } } ] }
            """);

        var profile = Assert.Single(LibraryStore.Load(file).Games).Profile;

        Assert.Equal(0, profile.OptimizedTier);
        Assert.Null(profile.Optimized);
        Assert.Equal("B", profile.Preset);
        Assert.Equal(5, profile.MaxGeneratedFrames);
    }
}
