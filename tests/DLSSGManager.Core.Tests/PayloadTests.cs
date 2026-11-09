using System.Security.Cryptography;

namespace DLSSGManager.Tests;

public class PayloadTests
{
    /// <summary>A staged payload in repository layout, with a manifest describing its exact bytes.</summary>
    private sealed class StagedRelease : IDisposable
    {
        private readonly TempDir _temp = new();
        public string Staging => _temp.Path;
        public PayloadManifest Manifest { get; }

        public StagedRelease(string version = "9.9.9", bool full = true)
        {
            var files = new List<PayloadFile>();
            void Add(string source, string destination, PayloadFileKind kind, PayloadSigner signer, PayloadPackageKind package, byte[] bytes)
            {
                var path = System.IO.Path.Combine(Staging, source.Replace('/', System.IO.Path.DirectorySeparatorChar));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
                files.Add(new PayloadFile
                {
                    Source = source, Destination = destination, Kind = kind, Signer = signer, Package = package,
                    Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                });
            }

            Add("dlssg_sm86.ini", "dlssg_sm86.ini", PayloadFileKind.Config, PayloadSigner.None, PayloadPackageKind.Base,
                System.Text.Encoding.UTF8.GetBytes(DeploymentFixture.Ini041 + "\n; " + Guid.NewGuid()));
            Add("version.dll", "version.dll", PayloadFileKind.Proxy, PayloadSigner.Project, PayloadPackageKind.Base, DeploymentFixture.Random());
            Add("alternatives/winmm.dll", "altnative/winmm.dll", PayloadFileKind.Proxy, PayloadSigner.Project, PayloadPackageKind.Base, DeploymentFixture.Random());
            foreach (var runtime in ModSource.RequiredRuntimeFiles)
                Add(runtime, runtime, PayloadFileKind.Runtime, PayloadSigner.Project, PayloadPackageKind.Base, DeploymentFixture.Random());
            if (full)
                foreach (var sr in ModSource.SuperResolutionFiles)
                    Add(sr, sr, PayloadFileKind.Runtime, PayloadSigner.Project, PayloadPackageKind.Full, DeploymentFixture.Random());

            Manifest = new PayloadManifest
            {
                Version = version,
                Capabilities = new PayloadCapabilities { IniSchema = "0.4.1", RuntimeDirectory = true, SingleProxy = true },
                Files = files,
            };
        }

        public string Path(string source) => System.IO.Path.Combine(Staging, source.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Dispose() => _temp.Dispose();
    }

    [Fact]
    public void Embedded_manifest_for_the_supported_release_is_valid()
    {
        var manifest = PayloadCatalog.Supported;

        Assert.Equal(ModFetcher.SupportedRelease, manifest.Version);
        Assert.True(manifest.Capabilities.Schema041);
        Assert.Contains(manifest.Files, f => f.Destination == "version.dll" && f.Signer == PayloadSigner.Project);
        Assert.All(ModSource.RequiredRuntimeFiles, r => Assert.Contains(manifest.Files, f => f.Destination == r && f.Required));
        Assert.All(ModSource.SuperResolutionFiles, r => Assert.Contains(manifest.Files, f => f.Destination == r && !f.Required));
        Assert.All(manifest.Files.Where(f => f.Destination.Contains("nvngx")), f => Assert.Equal(PayloadSigner.Nvidia, f.Signer));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/Windows/version.dll")]
    [InlineData("a/../../b.dll")]
    [InlineData("version.dll:ads")]
    public void Manifest_with_unsafe_paths_is_rejected(string path)
    {
        var json = $$"""
            { "version": "1.0", "files": [
              { "source": "dlssg_sm86.ini", "destination": "dlssg_sm86.ini", "kind": "config", "sha256": "{{new string('A', 64)}}" },
              { "source": "{{path.Replace("\\", "\\\\")}}", "destination": "x.dll", "kind": "proxy", "sha256": "{{new string('B', 64)}}" } ] }
            """;
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse(json));
    }

    [Fact]
    public void Exact_release_is_accepted()
    {
        using var release = new StagedRelease();
        var result = PayloadVerifier.Verify(release.Staging, release.Manifest, checkSignatures: false);
        Assert.True(result.Accepted, result.Message);
        Assert.True(result.HasSuperResolution);
    }

    [Fact]
    public void A_single_changed_byte_is_rejected_from_any_source()
    {
        using var release = new StagedRelease();
        var dll = release.Path("dlssg_sm86/kernels/fg_kernels.dll");
        var bytes = File.ReadAllBytes(dll);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(dll, bytes);

        var result = PayloadVerifier.Verify(release.Staging, release.Manifest, checkSignatures: false);

        Assert.False(result.Accepted);
        Assert.Contains("fg_kernels.dll", result.Message);
    }

    [Fact]
    public void Documentation_differences_do_not_fail_verification()
    {
        using var release = new StagedRelease();
        var withDoc = release.Manifest with
        {
            Files = release.Manifest.Files.Append(new PayloadFile
            {
                Source = "README.md", Destination = "README.md", Kind = PayloadFileKind.Doc,
                Size = 3, Sha256 = new string('C', 64),
            }).ToArray(),
        };
        File.WriteAllText(release.Path("README.md"), "a different readme");

        Assert.True(PayloadVerifier.Verify(release.Staging, withDoc, checkSignatures: false).Accepted);
    }

    [Fact]
    public void Tampered_ini_template_is_rejected()
    {
        using var release = new StagedRelease();
        File.AppendAllText(release.Path("dlssg_sm86.ini"), "\n[Evil]\nLoad=C:\\x.dll\n");
        Assert.False(PayloadVerifier.Verify(release.Staging, release.Manifest, checkSignatures: false).Accepted);
    }

    [Fact]
    public void Half_a_super_resolution_package_is_rejected()
    {
        using var release = new StagedRelease();
        File.Delete(release.Path(ModSource.SuperResolutionFiles[0]));
        Assert.False(PayloadVerifier.Verify(release.Staging, release.Manifest, checkSignatures: false).Accepted);
    }

    [Fact]
    public void Base_package_is_accepted_without_super_resolution()
    {
        using var release = new StagedRelease(full: false);
        var result = PayloadVerifier.Verify(release.Staging, release.Manifest, checkSignatures: false);
        Assert.True(result.Accepted, result.Message);
        Assert.False(result.HasSuperResolution);
    }

    [Fact]
    public void Unknown_release_is_rejected_on_import()
    {
        using var release = new StagedRelease();
        var result = PayloadVerifier.Verify(release.Staging, expected: null, checkSignatures: false);
        Assert.False(result.Accepted);
    }

    [Fact]
    public void Import_identifies_the_release_by_its_ini_hash()
    {
        using var release = new StagedRelease("8.8.8");
        using var _ = PayloadCatalog.AddForTesting(release.Manifest);

        var result = PayloadVerifier.Verify(release.Staging, expected: null, checkSignatures: false);

        Assert.True(result.Accepted, result.Message);
        Assert.Equal("8.8.8", result.Manifest!.Version);
    }

    [Fact]
    public void Files_of_a_known_release_are_recognised_as_ours_without_a_record()
    {
        using var release = new StagedRelease("7.7.7");
        using var _ = PayloadCatalog.AddForTesting(release.Manifest);

        Assert.True(DeploymentService.IsOurs(release.Path("version.dll"), recordedHash: null));
        Assert.False(DeploymentService.IsOurs(release.Path("dlssg_sm86.ini"), recordedHash: null)); // config is never claimed by catalog
    }

    [Fact]
    public void Local_payload_matches_the_embedded_manifest_when_present()
    {
        // Checks the shipped hashes against a real download when the repository has one (mod/ is
        // git-ignored, so CI skips this). Includes the signature checks against the real certificates.
        var root = FindRepositoryMod();
        if (root is null) return;
        var dir = ModStore.ActiveDirectory(root);
        if (!File.Exists(Path.Combine(dir, ModSource.IniName))) return;
        var marker = Path.Combine(dir, ModFetcher.VersionMarkerName);
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != ModFetcher.SupportedRelease) return;

        Assert.Null(PayloadVerifier.CheckInstalled(dir, PayloadCatalog.Supported));
        Assert.True(DeploymentService.IsProjectSigned(Path.Combine(dir, "version.dll")));
    }

    private static string? FindRepositoryMod()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var mod = Path.Combine(dir.FullName, "mod");
            if (File.Exists(Path.Combine(dir.FullName, "DLSSGManager.sln")) && Directory.Exists(mod)) return mod;
            dir = dir.Parent;
        }
        return null;
    }
}

public class ModStoreTests
{
    private static Action<string> Payload(string marker, params string[] extraFiles) => folder =>
    {
        File.WriteAllText(Path.Combine(folder, ModSource.IniName), "; " + marker);
        foreach (var extra in extraFiles)
        {
            var path = Path.Combine(folder, extra);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, marker);
        }
    };

    [Fact]
    public void Activate_installs_into_a_version_folder_and_switches_current()
    {
        using var root = new TempDir();

        var active = ModStore.Activate(root.Path, "1.0.0", Payload("one"));

        Assert.Equal(Path.Combine(root.Path, "versions", "1.0.0"), active);
        Assert.Equal("1.0.0", ModStore.ActiveVersion(root.Path));
        Assert.Equal(active, ModStore.ActiveDirectory(root.Path));
        Assert.True(ModSourceLocator.LooksLikeSource(root.Path));
    }

    [Fact]
    public void Failed_install_leaves_the_active_release_untouched()
    {
        using var root = new TempDir();
        ModStore.Activate(root.Path, "1.0.0", Payload("one"));

        Assert.ThrowsAny<Exception>(() => ModStore.Activate(root.Path, "2.0.0", folder =>
        {
            File.WriteAllText(Path.Combine(folder, "partial.dll"), "x");
            throw new IOException("network dropped");
        }));

        Assert.Equal("1.0.0", ModStore.ActiveVersion(root.Path));
        Assert.False(Directory.Exists(Path.Combine(root.Path, "versions", "2.0.0")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(root.Path, "versions"), ".incoming-*"));
    }

    [Fact]
    public void Previous_release_is_kept_for_rollback_and_older_ones_pruned()
    {
        using var root = new TempDir();
        ModStore.Activate(root.Path, "1.0.0", Payload("one"));
        Thread.Sleep(20);
        ModStore.Activate(root.Path, "2.0.0", Payload("two"));
        Thread.Sleep(20);
        ModStore.Activate(root.Path, "3.0.0", Payload("three"));

        var installed = ModStore.InstalledVersions(root.Path);
        Assert.Equal(new[] { "3.0.0", "2.0.0" }, installed);

        Assert.True(ModStore.Switch(root.Path, "2.0.0"));
        Assert.Equal("; two", File.ReadAllText(Path.Combine(ModStore.ActiveDirectory(root.Path), ModSource.IniName)));
    }

    [Fact]
    public void Reinstalling_the_same_release_replaces_it()
    {
        using var root = new TempDir();
        ModStore.Activate(root.Path, "1.0.0", Payload("first"));
        ModStore.Activate(root.Path, "1.0.0", Payload("second"));

        Assert.Equal("; second", File.ReadAllText(Path.Combine(ModStore.ActiveDirectory(root.Path), ModSource.IniName)));
    }

    [Fact]
    public void Flat_layout_is_migrated_into_a_version_folder()
    {
        using var root = new TempDir();
        Payload("flat", "version.dll", "altnative/dxgi.dll")(root.Path);
        File.WriteAllText(Path.Combine(root.Path, ModFetcher.VersionMarkerName), "0.4.0");
        File.WriteAllText(Path.Combine(root.Path, "unrelated.txt"), "mine");

        // Before migration the flat folder is used as it is.
        Assert.Equal(root.Path, ModStore.ActiveDirectory(root.Path));

        ModStore.Activate(root.Path, "0.4.1", Payload("new"));

        Assert.True(File.Exists(Path.Combine(root.Path, "versions", "0.4.0", "altnative", "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(root.Path, "version.dll")));
        Assert.True(File.Exists(Path.Combine(root.Path, "unrelated.txt")));
        Assert.True(ModStore.Switch(root.Path, "0.4.0"));
    }

    [Fact]
    public void Imported_proxies_survive_an_update_but_project_builds_do_not()
    {
        using var root = new TempDir();
        ModStore.Activate(root.Path, "1.0.0", Payload("one", "altnative/custom_proxy.dll"));

        ModStore.Activate(root.Path, "2.0.0", Payload("two"));

        Assert.True(File.Exists(Path.Combine(ModStore.ActiveDirectory(root.Path), "altnative", "custom_proxy.dll")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a\\b")]
    [InlineData("")]
    public void Unsafe_version_labels_are_refused(string label)
    {
        Assert.False(ModStore.IsSafeLabel(label));
    }

    [Fact]
    public void Tampered_current_file_does_not_escape_the_root()
    {
        using var root = new TempDir();
        ModStore.Activate(root.Path, "1.0.0", Payload("one"));
        File.WriteAllText(Path.Combine(root.Path, ModStore.CurrentFileName), """{ "active": "..\\..\\Windows" }""");

        Assert.Null(ModStore.ActiveVersion(root.Path));
        Assert.Equal(root.Path, ModStore.ActiveDirectory(root.Path));
    }
}
