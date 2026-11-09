using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>
/// The local mod folder, one subfolder per release:
///
/// <code>
/// mod/
///   versions/0.4.0/      previous release, kept for rolling back
///   versions/0.4.1/      active release
///   current.json         { "active": "0.4.1" }
/// </code>
///
/// A new release is assembled in a scratch folder beside the others and moved into place only once it
/// is complete, then <c>current.json</c> is switched over. An interrupted or failed install therefore
/// never touches the release in use. A root without <c>current.json</c> is the pre-versioned flat layout
/// and is used as it is.
/// </summary>
public static partial class ModStore
{
    public const string VersionsDirName = "versions";
    public const string CurrentFileName = "current.json";

    /// <summary>Releases kept on disk: the active one and the one before it.</summary>
    public const int KeepVersions = 2;

    private sealed record Current(string Active);

    /// <summary>The folder holding the active release under <paramref name="root"/>.</summary>
    public static string ActiveDirectory(string root)
    {
        var active = ActiveVersion(root);
        if (active is not null)
        {
            var dir = Path.Combine(root, VersionsDirName, active);
            if (File.Exists(Path.Combine(dir, ModSource.IniName))) return dir;
        }
        return root;
    }

    /// <summary>The version <c>current.json</c> names, or null for a flat or unreadable layout.</summary>
    public static string? ActiveVersion(string root)
    {
        try
        {
            var file = Path.Combine(root, CurrentFileName);
            if (!File.Exists(file)) return null;
            var current = JsonSerializer.Deserialize<Current>(File.ReadAllText(file), PayloadManifest.JsonOptions);
            return current?.Active is { } v && IsSafeLabel(v) ? v : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Releases present under <c>versions/</c>, newest folder first.</summary>
    public static IReadOnlyList<string> InstalledVersions(string root)
    {
        var dir = Path.Combine(root, VersionsDirName);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return new DirectoryInfo(dir).GetDirectories()
            .Where(d => IsSafeLabel(d.Name) && File.Exists(Path.Combine(d.FullName, ModSource.IniName)))
            .OrderByDescending(d => d.CreationTimeUtc)
            .Select(d => d.Name)
            .ToArray();
    }

    /// <summary>
    /// Installs a release: <paramref name="populate"/> fills an empty scratch folder, which then becomes
    /// <c>versions/&lt;label&gt;</c> and the active release. Proxies the user imported into the previously
    /// active release are carried over. Returns the new active folder.
    /// </summary>
    public static string Activate(string root, string label, Action<string> populate, OpResult? log = null)
    {
        var version = IsSafeLabel(label) ? label : "unknown";
        var versions = Path.Combine(root, VersionsDirName);
        Directory.CreateDirectory(versions);
        MigrateFlatLayout(root, log);

        var previous = ActiveDirectory(root);
        var incoming = Path.Combine(versions, ".incoming-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(versions, version);
        string? retired = null;

        try
        {
            Directory.CreateDirectory(incoming);
            populate(incoming);
            if (!File.Exists(Path.Combine(incoming, ModSource.IniName)))
                throw new InvalidOperationException(Loc.T("ModSource.IniMissing", ModSource.IniName));

            CarryImportedProxies(previous, incoming, log);

            if (Directory.Exists(target))
            {
                retired = Path.Combine(versions, ".retired-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, retired);
            }
            Directory.Move(incoming, target);
            WriteCurrent(root, version);
        }
        catch
        {
            TryDeleteDirectory(incoming);
            // Put a replaced copy of the same release back if the switch did not complete.
            if (retired is not null && !Directory.Exists(target)) Directory.Move(retired, target);
            throw;
        }

        if (retired is not null) TryDeleteDirectory(retired);
        Prune(root, version);
        return target;
    }

    /// <summary>
    /// Entries a pre-versioned mod root holds. Only these are moved, so a user-chosen folder that also
    /// contains unrelated files keeps them where they are.
    /// </summary>
    private static readonly string[] FlatLayoutEntries =
    {
        ModSource.IniName, "version.dll", ModSource.AltDirName, ModSource.LogDirName, "config", "docs",
        "README.md", "README.en.md", "THIRD_PARTY_NOTICES.txt", BuildCatalog.Marker, ModFetcher.VersionMarkerName,
    };

    /// <summary>
    /// Moves a pre-versioned payload sitting directly in <paramref name="root"/> into
    /// <c>versions/&lt;its label&gt;</c> and makes it active, so the release being replaced is kept for
    /// rolling back instead of overwritten in place.
    /// </summary>
    internal static void MigrateFlatLayout(string root, OpResult? log = null)
    {
        if (ActiveVersion(root) is not null || !File.Exists(Path.Combine(root, ModSource.IniName))) return;

        string? label = null;
        try { label = File.ReadAllText(Path.Combine(root, ModFetcher.VersionMarkerName)).Trim(); }
        catch { /* no marker: an older download or a hand-made folder */ }
        if (!IsSafeLabel(label)) label = "legacy";

        var target = Path.Combine(root, VersionsDirName, label!);
        if (Directory.Exists(target)) target = Path.Combine(root, VersionsDirName, label + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        Directory.CreateDirectory(target);

        foreach (var name in FlatLayoutEntries)
        {
            var from = Path.Combine(root, name);
            var to = Path.Combine(target, name);
            if (File.Exists(from)) File.Move(from, to);
            else if (Directory.Exists(from)) Directory.Move(from, to);
        }

        WriteCurrent(root, Path.GetFileName(target));
        log?.Note(Loc.T("Fetch.MigratedLayout", Path.GetFileName(target)));
    }

    /// <summary>Makes an installed release the active one again.</summary>
    public static bool Switch(string root, string version)
    {
        if (!IsSafeLabel(version) || !File.Exists(Path.Combine(root, VersionsDirName, version, ModSource.IniName)))
            return false;
        WriteCurrent(root, version);
        return true;
    }

    private static void WriteCurrent(string root, string version)
    {
        var file = Path.Combine(root, CurrentFileName);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new Current(version), PayloadManifest.JsonOptions));
        File.Move(tmp, file, overwrite: true);
    }

    /// <summary>
    /// Copies proxies the user added (<see cref="ModSource.ImportProxy"/>) into the new release, so an
    /// update does not silently drop them. Project builds from an older release are left behind: pairing
    /// them with a newer INI is a combination neither release supports.
    /// </summary>
    private static void CarryImportedProxies(string previous, string incoming, OpResult? log)
    {
        if (string.Equals(Path.GetFullPath(previous), Path.GetFullPath(incoming), StringComparison.OrdinalIgnoreCase)) return;
        var from = Path.Combine(previous, ModSource.AltDirName);
        if (!Directory.Exists(from)) return;

        foreach (var file in Directory.EnumerateFiles(from, "*.dll"))
        {
            var name = Path.GetFileName(file);
            if (ModSource.ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(incoming, ModSource.AltDirName, name);
            if (File.Exists(dest)) continue;

            var hash = DeploymentService.Sha256(file);
            if (PayloadCatalog.IsProjectBuild(hash) || DeploymentService.IsProjectSigned(file))
            {
                log?.Note(Loc.T("Fetch.PrunedStale", name));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
            log?.Note(Loc.T("Fetch.CarriedImport", name));
        }
    }

    /// <summary>Removes releases beyond <see cref="KeepVersions"/>, never the active one, and stale scratch folders.</summary>
    private static void Prune(string root, string active)
    {
        var versions = Path.Combine(root, VersionsDirName);
        foreach (var scratch in Directory.EnumerateDirectories(versions, ".*"))
        {
            // Scratch folders of an install that is still running are minutes old at most.
            if (Directory.GetCreationTimeUtc(scratch) < DateTime.UtcNow.AddHours(-1)) TryDeleteDirectory(scratch);
        }

        foreach (var old in InstalledVersions(root).Where(v => v != active).Skip(KeepVersions - 1))
            TryDeleteDirectory(Path.Combine(versions, old));
    }

    public static bool IsSafeLabel(string? label) =>
        !string.IsNullOrWhiteSpace(label) && label.Length <= 64 && SafeLabel().IsMatch(label) && label is not ("." or "..");

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z._-]*$")]
    private static partial Regex SafeLabel();

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort; Prune retries on the next install */ }
    }
}
