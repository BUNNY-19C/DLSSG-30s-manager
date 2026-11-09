using System.IO;

namespace DLSSGManager;

/// <summary>
/// Works out where the mod files live.
///
/// Two situations have to work: a shipped folder where <c>mod\</c> sits beside the executable, and a
/// source checkout run via <c>dotnet run</c>, where the executable is buried in
/// <c>bin\Debug\net8.0-windows\</c> and <c>mod\</c> belongs at the repository root several levels up.
/// Without the upward search a fresh clone would look in (and download into) its own bin folder.
/// </summary>
public static class ModSourceLocator
{
    /// <summary>How far above the executable to look for a repository checkout.</summary>
    private const int MaxLevelsUp = 6;

    /// <summary>A folder counts as a mod source only if it holds the INI the project ships.</summary>
    private const string MarkerFile = ModSource.IniName;

    /// <summary>Files that mark a repository root. The project ships no .sln, so several are checked.</summary>
    private static readonly string[] RepoMarkers = { ".git", ".gitignore", "*.sln", "*.slnx" };

    /// <summary>
    /// The folder to read mod files from, or null when none exists yet. A user-configured path wins;
    /// otherwise the folder beside the executable; otherwise the nearest ancestor that looks like a
    /// checkout holding <c>mod\</c>.
    /// </summary>
    public static string? FindExisting(string? configuredPath)
    {
        if (LooksLikeSource(configuredPath)) return configuredPath;

        // Searching every candidate means an installed copy still finds files a portable copy
        // downloaded earlier (and the reverse), instead of silently re-downloading 75 MB.
        foreach (var candidate in CandidateFolders())
        {
            if (LooksLikeSource(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// True when this copy was put in place by the installer rather than unzipped by hand.
    ///
    /// Inno Setup always writes <c>unins000.exe</c> beside the program, so its presence is a reliable
    /// signal. The distinction matters for where data goes: an installed copy keeps user data (game
    /// list, backups, downloaded mod files) under %APPDATA% so uninstalling the program never throws
    /// away a 75 MB download, while a portable copy stays self-contained.
    /// </summary>
    public static bool IsInstalledCopy() => IsInstalledCopyIn(AppContext.BaseDirectory);

    /// <summary>Checks whether a specific folder contains an installed copy.</summary>
    public static bool IsInstalledCopyIn(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, "unins*.exe").Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Where downloads should be written.
    ///
    /// The mod files belong beside the program so a copy stays self-contained, whether it was
    /// installed or unzipped by hand. A checkout keeps them in the repository instead, so a clean of
    /// <c>bin\</c> does not throw away a 75 MB download. Only a read-only location (Program Files
    /// without elevation) pushes them into the user profile.
    /// </summary>
    public static string ResolveTarget(string? configuredPath)
    {
        if (LooksLikeSource(configuredPath)) return configuredPath!;
        if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath)) return configuredPath!;

        var existing = FindExisting(null);
        if (existing is not null) return existing;

        if (!IsInstalledCopy())
        {
            var repo = FindRepositoryRoot();
            if (repo is not null) return Path.Combine(repo, "mod");
        }

        return PreferWritable(AppPaths.BundledModDir, AppPaths.UserModDir);
    }

    /// <summary>
    /// Picks the folder beside the program when it can be written to, otherwise the per-user
    /// fallback according to directory permissions.
    /// </summary>
    public static string PreferWritable(string besideProgram, string userFallback) =>
        IsWritable(besideProgram) ? besideProgram : userFallback;

    /// <summary>
    /// True when the folder can be written to, creating it first if needed. Used to decide whether
    /// this copy is portable or installed into a protected location.
    /// </summary>
    public static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            var probe = Path.Combine(directory, ".write_probe_" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Folders worth searching for existing mod files, in preference order.
    ///
    /// The program's own folder comes first so an installed copy keeps its mod files beside itself.
    /// A repository is only searched for non-installed copies — an installed program must not pick up
    /// a stray <c>mod\</c> from an unrelated parent directory. The per-user folder is last, serving
    /// both older installs and the fallback for a read-only location such as Program Files.
    /// </summary>
    private static IEnumerable<string> CandidateFolders()
    {
        yield return AppPaths.BundledModDir;

        if (!IsInstalledCopy())
        {
            var repo = FindRepositoryRoot();
            if (repo is not null) yield return Path.Combine(repo, "mod");
        }

        yield return AppPaths.UserModDir;
    }

    /// <summary>
    /// Walks up from the executable looking for a repository root, stopping before the drive root so
    /// we never treat an unrelated parent folder as one.
    /// </summary>
    public static string? FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var level = 0; level < MaxLevelsUp && dir is not null; level++)
        {
            if (IsRepositoryRoot(dir.FullName)) return dir.FullName;

            var parent = dir.Parent;
            if (parent is null || parent.FullName == dir.FullName) break;   // Reached the drive root.
            dir = parent;
        }

        return null;
    }

    private static bool IsRepositoryRoot(string path)
    {
        foreach (var marker in RepoMarkers)
        {
            try
            {
                if (marker.Contains('*'))
                {
                    if (Directory.EnumerateFiles(path, marker).Any()) return true;
                }
                else if (File.Exists(Path.Combine(path, marker)) || Directory.Exists(Path.Combine(path, marker)))
                {
                    return true;
                }
            }
            catch
            {
                // An unreadable folder is simply not a repository root.
            }
        }

        return false;
    }

    /// <summary>True when the folder exists and contains the file that identifies a mod source.</summary>
    public static bool LooksLikeSource(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        return File.Exists(Path.Combine(path, MarkerFile));
    }
}
