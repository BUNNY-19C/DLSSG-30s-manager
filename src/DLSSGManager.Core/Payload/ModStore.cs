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
            File.WriteAllText(Path.Combine(incoming, InstalledMarker), version);

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
    /// Marker written into every release folder the manager assembled itself. Only marked folders are
    /// ever pruned; a folder made by migrating an older layout is never deleted automatically.
    /// </summary>
    public const string InstalledMarker = ".manager-installed";

    /// <summary>
    /// The payload files of an older flat mod root: the INI, the version markers, the proxies the manager
    /// knows, the runtime files and the documentation upstream ships. Nothing else is moved, so files the
    /// user keeps in that folder (and folders such as <c>config\</c> or <c>docs\</c>) stay where they are.
    /// Proxies the user imported into <c>altnative\</c> move with the payload they belong to.
    /// </summary>
    private static IEnumerable<string> FlatLayoutFiles(string root)
    {
        var fixedFiles = new[] { ModSource.IniName, BuildCatalog.Marker, ModFetcher.VersionMarkerName, "README.md", "README.en.md", "THIRD_PARTY_NOTICES.txt" }
            .Concat(ModSource.KnownProxyNames.Select(n => Path.GetRelativePath(root, ModSource.ResolveDllPath(root, n))))
            .Concat(ModSource.RuntimeFiles.Select(f => f.Replace('/', Path.DirectorySeparatorChar)));
        var imported = Directory.Exists(Path.Combine(root, ModSource.AltDirName))
            ? Directory.EnumerateFiles(Path.Combine(root, ModSource.AltDirName), "*.dll").Select(f => Path.GetRelativePath(root, f))
            : Enumerable.Empty<string>();
        return fixedFiles.Concat(imported).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves a pre-versioned payload sitting directly in <paramref name="root"/> into
    /// <c>versions/&lt;its label&gt;</c> and makes it active, so the release being replaced is kept for
    /// rolling back instead of overwritten in place. Only payload files are moved (see
    /// <see cref="FlatLayoutFiles"/>); folders left empty by the move are removed.
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

        foreach (var relative in FlatLayoutFiles(root).ToList())
        {
            var from = Path.Combine(root, relative);
            if (!File.Exists(from)) continue;
            var to = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
        }

        foreach (var dir in new[] { ModSource.AltDirName }
                     .Concat(ModSource.RuntimeFiles.Select(f => Path.GetDirectoryName(f.Replace('/', Path.DirectorySeparatorChar))!))
                     .Append(ModSource.LogDirName)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(d => d.Length))
        {
            var path = Path.Combine(root, dir);
            try { if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path); }
            catch { /* left in place; harmless */ }
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

    /// <summary>
    /// Removes releases beyond <see cref="KeepVersions"/> — only folders the manager assembled itself
    /// (<see cref="InstalledMarker"/>), never the active one, never a migrated folder — and stale scratch
    /// folders of its own.
    /// </summary>
    private static void Prune(string root, string active)
    {
        var versions = Path.Combine(root, VersionsDirName);
        foreach (var scratch in Directory.EnumerateDirectories(versions, ".incoming-*")
                     .Concat(Directory.EnumerateDirectories(versions, ".retired-*")))
        {
            // Scratch folders of an install that is still running are minutes old at most.
            if (Directory.GetCreationTimeUtc(scratch) < DateTime.UtcNow.AddHours(-1)) TryDeleteDirectory(scratch);
        }

        foreach (var old in InstalledVersions(root)
                     .Where(v => v != active && File.Exists(Path.Combine(versions, v, InstalledMarker)))
                     .Skip(KeepVersions - 1))
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
