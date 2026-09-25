using System.IO;
using System.Text.RegularExpressions;

namespace DLSSGManager;

public sealed class GameCandidate
{
    public string Name { get; set; } = "";
    public string RenderDir { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Source { get; set; } = "";
    public override string ToString() => $"{Name}  —  {RenderDir}";
}

/// <summary>
/// Finds games by two markers: the NVIDIA DLSS-G payload (<c>nvngx_dlssg.dll</c>) a game ships when
/// it supports frame generation natively, and this project's own <c>dlssg_sm86.ini</c> — since 0.3.x
/// the mod carries its own DLSS-G runtime, so it works on games that never shipped the payload, and
/// a hand-installed copy is as good a signpost as the game's own files.
/// </summary>
public static class Detection
{
    public const string DlssgMarker = "nvngx_dlssg.dll";

    /// <summary>
    /// This project's INI name, unique enough that no other product uses it: a folder holding it is
    /// a hand-installed mod, and it is where the proxy belongs.
    /// </summary>
    public const string ModIniMarker = "dlssg_sm86.ini";

    /// <summary>Every file name that marks a frame-generation folder.</summary>
    public static readonly string[] Markers = { DlssgMarker, ModIniMarker };

    private const int MaxDepth = 6;

    private static readonly string[] ExeNoise =
    {
        "unitycrashhandler", "crashhandler", "crashreport", "launcher", "unins", "setup",
        "vcredist", "dxsetup", "easyanticheat", "battleye", "be_service", "notification_helper",
        "installer", "updater", "report",
    };

    public static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        var candidates = new List<string>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
            foreach (var sub in new[] { "Steam", "SteamLibrary", @"Games\Steam", @"Program Files (x86)\Steam", @"Program Files\Steam" })
            {
                try { candidates.Add(Path.Combine(drive.RootDirectory.FullName, sub)); }
                catch { /* Not every drive letter accepts composition. */ }
            }
        }

        candidates.Add(@"C:\Program Files (x86)\Steam");

        foreach (var c in candidates)
        {
            if (File.Exists(Path.Combine(c, "steamapps", "libraryfolders.vdf"))) roots.Add(c);
        }

        foreach (var root in roots.ToList())
        {
            string text;
            try { text = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")); }
            catch { continue; }

            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
            {
                var path = m.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) roots.Add(path);
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Steam titles that ship DLSS-G, named from their appmanifest rather than the folder name.</summary>
    public static List<GameCandidate> ScanSteam(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<GameCandidate>();
        foreach (var lib in SteamLibraries())
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(steamApps)) continue;

            foreach (var acf in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                ct.ThrowIfCancellationRequested();
                string name, installDir;
                try
                {
                    var text = File.ReadAllText(acf);
                    name = AcfValue(text, "name") ?? Path.GetFileNameWithoutExtension(acf);
                    installDir = AcfValue(text, "installdir") ?? "";
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(installDir)) continue;
                var gameRoot = Path.Combine(steamApps, "common", installDir);
                if (!Directory.Exists(gameRoot)) continue;

                progress?.Report(Loc.T("Scan.Checking", name));
                var hit = FindRenderTarget(gameRoot, ct);
                if (hit is not null)
                {
                    hit.Name = name;
                    hit.Source = "Steam";
                    found.Add(hit);
                }
            }
        }

        return found;
    }

    private static string? AcfValue(string text, string key)
    {
        var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Scans an arbitrary folder tree for DLSS-G render directories.</summary>
    public static List<GameCandidate> ScanFolder(string root, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<GameCandidate>();
        if (!Directory.Exists(root)) return results;

        foreach (var dll in EnumerateMarkers(root, 0, ct))
        {
            ct.ThrowIfCancellationRequested();
            var hit = ResolveFromMarker(dll);
            if (hit is null) continue;
            hit.Source = Loc.T("Scan.SourceFolder");
            if (results.Any(x => string.Equals(x.RenderDir, hit.RenderDir, StringComparison.OrdinalIgnoreCase))) continue;
            progress?.Report(Loc.T("Scan.Found", hit.RenderDir));
            results.Add(hit);
        }

        return results;
    }

    private static IEnumerable<string> EnumerateMarkers(string dir, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth) yield break;

        foreach (var marker in Markers)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, marker); }
            catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;
        }

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch { subdirs = Array.Empty<string>(); }

        foreach (var sub in subdirs)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in EnumerateMarkers(sub, depth + 1, ct)) yield return f;
        }
    }

    public static GameCandidate? FindRenderTarget(string root, CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) return null;
        foreach (var dll in EnumerateMarkers(root, 0, ct))
        {
            var hit = ResolveFromMarker(dll);
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>
    /// Turns whatever folder the user picked into the directory that actually matters.
    ///
    /// A user often selects the game's root folder, but the proxy belongs beside the rendering
    /// executable, and the anti-cheat scan has to look at that same folder: pointing at
    /// <c>E:\Overwatch</c> instead of its <c>_retail_</c> folder would walk up past NeacSafe64.sys and
    /// wrongly declare the game unprotected.
    /// </summary>
    public static (string RenderDir, string? ExePath) ResolveRenderDir(string folder)
    {
        if (!Directory.Exists(folder)) return (folder, null);

        // Already the render directory: a game executable lives right here.
        var exe = PickMainExe(folder);
        if (exe is not null) return (folder, exe);

        var hit = FindRenderTarget(folder);
        if (hit is not null)
            return (hit.RenderDir, string.IsNullOrWhiteSpace(hit.ExePath) ? null : hit.ExePath);

        return (folder, null);
    }

    /// <summary>
    /// Walks up from the marker looking for the folder holding the rendering executable. Some titles
    /// keep the DLL a level down, e.g. Overwatch ships it in _retail_\sl while the EXE is in _retail_.
    /// </summary>
    private static GameCandidate? ResolveFromMarker(string dllPath)
    {
        var dllDir = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(dllDir)) return null;

        var dir = dllDir;
        for (var up = 0; up <= 3; up++)
        {
            var exe = PickMainExe(dir);
            if (exe is not null)
                return new GameCandidate { Name = FriendlyName(dir), RenderDir = dir, ExePath = exe };

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        return new GameCandidate { Name = FriendlyName(dllDir), RenderDir = dllDir, ExePath = "" };
    }

    /// <summary>
    /// Sub-folders such as "win64" or "_retail_" say nothing about the game, so the name is taken
    /// from the nearest ancestor that does.
    /// </summary>
    private static readonly string[] GenericFolderNames =
    {
        "win64", "win32", "binaries", "binary", "bin", "x64", "x86", "game", "games",
        "retail", "shipping", "main", "sl", "client", "app", "content", "build",
        "windowsnoeditor", "windowseditor",
    };

    public static string FriendlyName(string directory)
    {
        var dir = directory;
        for (var hop = 0; hop < 3; hop++)
        {
            var name = new DirectoryInfo(dir).Name;
            var normalized = name.Trim('_', '-', '.').ToLowerInvariant();

            if (normalized.Length > 0 && !GenericFolderNames.Contains(normalized) && !name.StartsWith('.'))
                return name;

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        return new DirectoryInfo(directory).Name;
    }

    public static string? PickMainExe(string dir)
    {
        List<FileInfo> exes;
        try
        {
            exes = new DirectoryInfo(dir).GetFiles("*.exe")
                .Where(f => !ExeNoise.Any(n => f.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(f => f.Length)
                .ToList();
        }
        catch { return null; }

        if (exes.Count == 0) return null;

        var folder = new DirectoryInfo(dir).Name;
        var preferred = exes.FirstOrDefault(f =>
            f.Name.Contains(folder, StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
            f.Name.Contains("Win64", StringComparison.OrdinalIgnoreCase));

        return (preferred ?? exes[0]).FullName;
    }
}
