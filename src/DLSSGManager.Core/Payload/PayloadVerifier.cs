namespace DLSSGManager;

/// <summary>Result of checking a staged payload against a release manifest.</summary>
public sealed record PayloadVerification(bool Accepted, string Message, PayloadManifest? Manifest, bool HasSuperResolution)
{
    public static PayloadVerification Reject(string message) => new(false, message, null, false);
}

/// <summary>
/// Checks staged files against the release manifest before anything reaches the mod folder.
///
/// Every listed file present must have exactly the bytes the manifest records, from whichever source it
/// came — an official endpoint, a mirror, or a zip the user dropped in. This replaces the previous
/// scheme, where a certificate mismatch on GitHub's own endpoints was only logged: a tag or release
/// attachment can be replaced, and the pinned hashes are what make the content itself the authority.
/// Signatures are still checked as a second, independent line.
/// </summary>
public static class PayloadVerifier
{
    /// <summary>
    /// Verifies a staged payload laid out like the upstream repository. With <paramref name="expected"/>
    /// null the release is identified from the INI template's hash (offline imports).
    /// </summary>
    public static PayloadVerification Verify(string staging, PayloadManifest? expected = null, bool checkSignatures = true)
    {
        var ini = Path.Combine(staging, ModSource.IniName);
        if (!File.Exists(ini)) return PayloadVerification.Reject(Loc.T("Fetch.Incomplete", ModSource.IniName));

        var manifest = expected ?? PayloadCatalog.FindByIniHash(DeploymentService.Sha256(ini));
        if (manifest is null) return PayloadVerification.Reject(Loc.T("Fetch.UnknownRelease"));

        var missing = manifest.Files.Where(f => f.Required && !File.Exists(StagedPath(staging, f))).Select(f => f.Source).ToList();
        if (missing.Count > 0) return PayloadVerification.Reject(Loc.T("Fetch.Incomplete", Loc.Join(missing)));

        var full = manifest.Files.Where(f => f.Package == PayloadPackageKind.Full).ToList();
        var fullPresent = full.Count(f => File.Exists(StagedPath(staging, f)));
        if (fullPresent != 0 && fullPresent != full.Count) return PayloadVerification.Reject(Loc.T("Manage.NeedFullPackage"));

        var mismatched = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = StagedPath(staging, file);
            if (!File.Exists(path)) continue;
            if (new FileInfo(path).Length != file.Size ||
                !DeploymentService.Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                mismatched.Add(file.Source);
        }
        if (mismatched.Count > 0) return PayloadVerification.Reject(Loc.T("Fetch.HashMismatch", manifest.Version, Loc.Join(mismatched)));

        if (checkSignatures)
        {
            var unsigned = new List<string>();
            foreach (var file in manifest.Files.Where(f => f.Signer != PayloadSigner.None))
            {
                var path = StagedPath(staging, file);
                if (!File.Exists(path)) continue;
                var nvidia = file.Signer == PayloadSigner.Nvidia;
                using var cert = DeploymentService.ReadSignerCertificate(path, out var intact, requireTrusted: nvidia);
                var ok = nvidia
                    ? intact && cert?.Subject.Contains("NVIDIA Corporation", StringComparison.OrdinalIgnoreCase) == true
                    : ProjectSigning.IsProjectSignature(cert, intact);
                if (!ok) unsigned.Add(Path.GetFileName(file.Source));
            }
            if (unsigned.Count > 0) return PayloadVerification.Reject(Loc.T("Fetch.Unsigned", Loc.Join(unsigned)));
        }

        return new PayloadVerification(true, Loc.T("Fetch.HashOk", manifest.Version), manifest, fullPresent == full.Count && full.Count > 0);
    }

    /// <summary>
    /// Copies a verified payload from staging (repository layout) into <paramref name="destination"/>
    /// (the manager's layout). Only files the manifest lists are copied.
    /// </summary>
    public static int Publish(string staging, string destination, PayloadManifest manifest)
    {
        var copied = 0;
        var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
        foreach (var file in manifest.Files)
        {
            var source = StagedPath(staging, file);
            if (!File.Exists(source)) continue;

            var dest = Path.GetFullPath(Path.Combine(destination, file.Destination.Replace('/', Path.DirectorySeparatorChar)));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Loc.T("Fetch.EscapeAttempt"));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            DeploymentService.Unblock(dest);
            copied++;
        }

        File.WriteAllText(Path.Combine(destination, BuildCatalog.Marker), manifest.RuntimeModel);
        File.WriteAllText(Path.Combine(destination, ModFetcher.VersionMarkerName), manifest.Version, new System.Text.UTF8Encoding(false));
        return copied;
    }

    /// <summary>Checks an installed release folder against its manifest; null when intact.</summary>
    public static string? CheckInstalled(string directory, PayloadManifest manifest)
    {
        foreach (var file in manifest.Files.Where(f => f.Kind != PayloadFileKind.Doc))
        {
            var path = Path.Combine(directory, file.Destination.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                if (file.Required) return Loc.T("Fetch.Incomplete", file.Destination);
                continue;
            }
            if (!DeploymentService.Sha256(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                return Loc.T("Fetch.HashMismatch", manifest.Version, file.Destination);
        }
        return null;
    }

    private static string StagedPath(string staging, PayloadFile file) =>
        Path.Combine(staging, file.Source.Replace('/', Path.DirectorySeparatorChar));
}
