namespace DLSSGManager.Tests;

/// <summary>
/// Real network checks through the production HTTP stack (factory, resilience pipeline, connect-time
/// address policy). Off by default; set DLSSGMANAGER_LIVE_TESTS=1 to run them.
/// </summary>
[Collection("PayloadHttp")]
public class LiveNetworkTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("DLSSGMANAGER_LIVE_TESTS") == "1";

    [Fact]
    public async Task Upstream_version_probe_answers()
    {
        if (!Enabled) return;
        var version = await ModFetcher.DetectLatestVersionAsync(CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Fact]
    public async Task Base_package_downloads_and_verifies_from_the_official_endpoint()
    {
        if (!Enabled) return;
        using var root = new TempDir();

        var result = await ModFetcher.DownloadIntoAsync(root.Path, null, CancellationToken.None, sourceId: "raw",
            package: PayloadPackage.Base);

        Assert.True(result.Ok, result.Message + "\n" + string.Join("\n", result.Lines));
        Assert.Equal(ModFetcher.SupportedRelease, ModStore.ActiveVersion(root.Path));
        Assert.Null(PayloadVerifier.CheckInstalled(ModStore.ActiveDirectory(root.Path), PayloadCatalog.Supported));
    }
}
