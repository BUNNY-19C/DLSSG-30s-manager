using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

public enum ProtectionLevel
{
    /// <summary>No anti-cheat component found next to the game.</summary>
    None,
    /// <summary>Something was found, but it does not run in kernel mode.</summary>
    User,
    /// <summary>A kernel-mode anti-cheat driver is present.</summary>
    Kernel,
}

public sealed record ProtectionFinding(string ProductKey, string Evidence, bool Kernel)
{
    /// <summary>Vendor name in the current interface language.</summary>
    public string Product => Loc.T(ProductKey);
}

public sealed class ProtectionReport
{
    public ProtectionLevel Level { get; init; } = ProtectionLevel.None;
    public List<ProtectionFinding> Findings { get; init; } = new();
    public bool ScanFailed { get; init; }
    public string ScannedDir { get; init; } = "";

    public bool IsProtected => Level != ProtectionLevel.None;
    public bool HasKernelAntiCheat => Level == ProtectionLevel.Kernel;

    /// <summary>Separator between product names; uses the comma style of the active language.</summary>
    private static string ListSeparator => Loc.Current == Languages.English ? ", " : "、";

    public string Products => string.Join(ListSeparator, Findings.Select(f => f.Product).Distinct());

    public string Evidence => string.Join(ListSeparator, Findings.Select(f => f.Evidence));

    public string Summary
    {
        get
        {
            if (ScanFailed) return Loc.T("Anti.ScanFailed");
            if (!IsProtected) return Loc.T("Anti.LevelNone");

            return HasKernelAntiCheat
                ? Loc.T("Anti.LevelKernel", Products)
                : Loc.T("Anti.LevelUser", Products);
        }
    }
}

/// <summary>
/// Looks for anti-cheat components beside a game, because a DLL proxy is exactly the shape of thing
/// they are built to catch.
///
/// A kernel-mode anti-cheat (HoYoKProtect, ACE, EAC, BattlEye, Vanguard, GameGuard) scans the game
/// folder itself and will quarantine or delete a proxy DLL before the game even loads it, so the mod
/// cannot work there — and a recorded detection can put the account at risk. Deploying to those games
/// is therefore blocked unless the user explicitly overrides, and the restore path knows how to clean
/// up the renamed copies such a driver leaves behind.
/// </summary>
public static class AntiCheat
{
    /// <summary>
    /// A named anti-cheat component.
    ///
    /// <paramref name="ProductKey"/> is a lookup key rather than display text, so the vendor name
    /// follows the interface language (these are proper nouns, so most translations differ only in the
    /// vendor's own spelling — "米哈游" vs "miHoYo").
    ///
    /// <paramref name="Patterns"/> are matched against both files AND directories: Tencent ACE ships a
    /// bare <c>AntiCheatExpert\</c> folder in the game root, which a file-only check never sees.
    /// </summary>
    private sealed record Signature(string ProductKey, string[] Patterns, bool Kernel);

    private static readonly Signature[] Signatures =
    {
        new("Anti.Hoyo",
            new[] { "HoYoKProtect.sys", "mhypbase.dll", "mhyprot*.sys", "mhyprot*.dll" }, true),

        new("Anti.Tencent",
            new[]
            {
                "ACE-BASE.sys", "ACE-GAME.sys", "ACE-ADVT.sys", "ACE-CORE*.sys", "ACE-BOOT.sys",
                "ACE-Service64.exe", "ACE-Setup64.exe", "ACE-Base64.dll", "ACE-CSI64.dll",
                "AntiCheatExpert*", "SGuard*.sys", "SGuard64.exe", "SGuardSvc*.exe",
            }, true),

        // NetEase's anti-cheat (Overwatch's Chinese client, Naraka, etc.).
        new("Anti.Neac",
            new[] { "NeacSafe*.sys", "NeacInterface.dll", "NeacLoader.exe", "NeacClient.exe", "OWNeacClient.exe", "Neac*.sys" }, true),

        new("Anti.Eac",
            new[] { "EasyAntiCheat*.sys", "EasyAntiCheat.exe", "EasyAntiCheat_EOS*.sys", "EasyAntiCheat*.dll", "start_protected_game.exe" }, true),

        new("Anti.BattlEye",
            new[] { "BEService*.exe", "BEClient*.dll", "BattlEye*.sys", "BEService*" }, true),

        new("Anti.GameGuard",
            new[] { "GameGuard.des", "npgg*.des", "npggNT*.des", "nProtect*.sys", "GameMon.des" }, true),

        new("Anti.Vanguard",
            new[] { "vgk.sys", "vgc.exe", "vgk*.sys" }, true),

        new("Anti.Xigncode",
            new[] { "x3.xem", "xigncode*", "XIGNCODE*" }, true),
    };

    /// <summary>
    /// Any kernel driver sitting in a game folder. Normal games do not ship one, so its mere presence
    /// is worth reporting even when the product is not in the table above.
    /// </summary>
    private const string DriverPattern = "*.sys";

    /// <summary>How far above the render directory to look; anti-cheat files often sit in the game root.</summary>
    private const int MaxAncestors = 3;

    /// <summary>
    /// Windows' own files at a volume root. Walking up from a game folder can land on a drive root,
    /// where these would otherwise trip the generic driver rule and report nonsense like "pagefile.sys".
    /// </summary>
    private static readonly string[] SystemFiles =
    {
        "pagefile.sys", "swapfile.sys", "hiberfil.sys", "dumpstack.log.tmp", "dumpstack.log",
    };

    public static ProtectionReport Scan(string renderDir)
    {
        if (string.IsNullOrWhiteSpace(renderDir) || !Directory.Exists(renderDir))
            return new ProtectionReport { ScanFailed = true, ScannedDir = renderDir ?? "" };

        var findings = new List<ProtectionFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = renderDir;

        for (var hop = 0; hop <= MaxAncestors; hop++)
        {
            // A volume root holds no game and no anti-cheat, only Windows' own files.
            if (!IsVolumeRoot(dir)) ScanDirectory(dir, findings, seen);

            var parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parent) || parent == dir) break;
            dir = parent;
        }

        var level = findings.Count == 0
            ? ProtectionLevel.None
            : findings.Any(f => f.Kernel) ? ProtectionLevel.Kernel : ProtectionLevel.User;

        return new ProtectionReport { Level = level, Findings = findings, ScannedDir = renderDir };
    }

    private static bool IsVolumeRoot(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ScanDirectory(string dir, List<ProtectionFinding> findings, HashSet<string> seen)
    {
        // Enumerate once per directory rather than once per pattern: game folders hold thousands of
        // entries and this runs on every status check.
        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToList();
        }
        catch
        {
            return; // An unreadable folder simply yields no findings.
        }

        if (entries.Count == 0) return;

        foreach (var signature in Signatures)
        {
            foreach (var pattern in signature.Patterns)
            {
                var hit = entries
                    .Where(e => MatchesWildcard(e, pattern))
                    .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();

                if (hit is null) continue;
                if (!seen.Add(signature.ProductKey + "|" + hit)) continue;

                findings.Add(new ProtectionFinding(signature.ProductKey, hit, signature.Kernel));
            }
        }

        // A kernel driver in a game folder is abnormal on its own — no shipping game needs one except
        // to watch the process. Reported even when the vendor is not in the table above, but skipped
        // when a named signature already accounted for it.
        var driver = entries
            .Where(e => e.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
            .Where(e => !SystemFiles.Contains(e, StringComparer.OrdinalIgnoreCase))
            .Where(e => !seen.Any(k => k.EndsWith("|" + e, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (driver is not null && seen.Add("kernel-driver|" + driver))
            findings.Add(new ProtectionFinding("Anti.UnknownDriver", driver, Kernel: true));
    }

    /// <summary>Case-insensitive wildcard match over a single file or folder name.</summary>
    private static bool MatchesWildcard(string name, string pattern)
    {
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Explains why the mod cannot be used on a game, for the prompt shown when the game is added or
    /// when a deploy is attempted anyway. The wording is shared by the relevant prompts.
    ///
    /// The game name is quoted with the punctuation of the active language, so the sentence reads
    /// naturally in both.
    /// </summary>
    public static string BuildUnsupportedNotice(string gameName, ProtectionReport report)
    {
        var name = string.IsNullOrWhiteSpace(gameName)
            ? Loc.T("Anti.ThisGame")
            : Loc.T("Anti.QuotedName", gameName);

        return Loc.T("Anti.BlockBody", name, report.Products, report.Evidence);
    }

    /// <summary>
    /// Finds copies of our proxy that an anti-cheat driver renamed instead of deleting, e.g.
    /// <c>version.dll.3787982156</c>. Only files that are provably ours are reported.
    /// </summary>
    public static List<string> FindQuarantinedCopies(string renderDir, string proxyName, string? recordedHash)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(renderDir) || !Directory.Exists(renderDir)) return result;
        if (string.IsNullOrWhiteSpace(proxyName)) return result;

        var baseName = Path.GetFileNameWithoutExtension(proxyName);
        var extension = Path.GetExtension(proxyName);

        try
        {
            foreach (var file in Directory.EnumerateFiles(renderDir, baseName + ".*"))
            {
                var name = Path.GetFileName(file);
                if (string.Equals(name, proxyName, StringComparison.OrdinalIgnoreCase)) continue;

                // Only the project's own bytes are ever reported, so an unrelated "version.dll.bak"
                // belonging to another tool is never touched.
                var looksRenamed = name.StartsWith(proxyName + ".", StringComparison.OrdinalIgnoreCase) ||
                                   name.StartsWith(baseName + extension + ".", StringComparison.OrdinalIgnoreCase);
                if (!looksRenamed) continue;

                if (DeploymentService.IsOurs(file, recordedHash)) result.Add(file);
            }
        }
        catch
        {
            // Unable to inspect the folder; report nothing rather than guessing.
        }

        return result;
    }
}
