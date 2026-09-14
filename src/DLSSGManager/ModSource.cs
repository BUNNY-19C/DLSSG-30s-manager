using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>
/// A folder holding the project's published files: root <c>version.dll</c> + <c>dlssg_sm86.ini</c>,
/// the four alternates under <c>altnative\</c>, and the two presets under <c>config\presets\</c>.
/// Proxies the user adds themselves also live under <c>altnative\</c>, beside the published ones.
/// </summary>
public sealed class ModSource
{
    /// <summary>
    /// Entry points the project's own builds ship, in the order they are worth trying.
    ///
    /// The order follows upstream's own guidance: the safe names first (version, winmm, dbghelp, dinput8
    /// sit off the D3D12 render path), the render-path proxies (dxgi, d3d12) last, because those are
    /// called every frame and their load order is sensitive. 0.3.0 replaced winhttp.dll with d3d12.dll
    /// and dbghelp.dll.
    /// </summary>
    public static readonly string[] ProxyCandidates =
        { "version.dll", "winmm.dll", "dinput8.dll", "dbghelp.dll", "dxgi.dll", "d3d12.dll" };

    /// <summary>
    /// Every name a proxy can legitimately occupy: the six above plus <c>winhttp.dll</c>, which 0.3.0
    /// dropped but an installation made with an older release may still have in place.
    ///
    /// This is the *scanning* set — cleanup, quarantine detection and the multiple-proxy check — and it is
    /// what decides whether a file the user adds is a plausible entry name. Deployment picks from
    /// <see cref="AvailableProxies"/> instead: the project's own names plus whatever has actually been
    /// added, so a name is only deployable when a DLL for it exists.
    /// </summary>
    public static readonly string[] KnownProxyNames =
        ProxyCandidates.Concat(new[] { "winhttp.dll" }).ToArray();

    /// <summary>True when a file name is one a proxy of this kind can take; the check is case-insensitive.</summary>
    public static bool IsKnownProxyName(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        KnownProxyNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    public const string IniName = "dlssg_sm86.ini";
    public const string LogDirName = "dlssg_sm86";

    /// <summary>Folder holding the entry points other than version.dll, published and imported alike.</summary>
    public const string AltDirName = "altnative";

    public string Root { get; }
    public bool IsValid { get; }
    public string Version { get; } = Loc.T("ModSource.UnknownVersion");
    public List<string> Proxies { get; } = new();
    public string ValidationMessage { get; } = "";

    /// <summary>Proxy DLLs in <c>altnative\</c> that this project does not ship — added by the user.</summary>
    public IReadOnlyList<string> ImportedProxies { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Entry names this source can deploy: the project's own five first, then imported ones. Auto-pick
    /// walks this order, so a game only lands on an imported entry when the classic names are taken.
    /// </summary>
    public IReadOnlyList<string> AvailableProxies { get; private set; } = ProxyCandidates;

    public ModSource(string root)
    {
        Root = root ?? "";
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ValidationMessage = Loc.T("ModSource.DirMissing");
            return;
        }

        var iniPath = Path.Combine(root, IniName);
        if (!File.Exists(iniPath)) ValidationMessage = Loc.T("ModSource.IniMissing", IniName);

        foreach (var name in ProxyCandidates)
        {
            if (File.Exists(ResolveDllPath(root, name))) Proxies.Add(name);
        }

        ImportedProxies = ImportedProxyNames(root);
        AvailableProxies = ProxyCandidates.Concat(ImportedProxies).ToArray();

        if (Proxies.Count == 0 && ImportedProxies.Count == 0)
            ValidationMessage = ValidationMessage.Length > 0
                ? Loc.T("ModSource.IniMissingAndNoDll", IniName)
                : Loc.T("ModSource.NoDll");

        IsValid = (Proxies.Count > 0 || ImportedProxies.Count > 0) && File.Exists(iniPath);
        if (IsValid) Version = ReadReleaseLabel(root) ?? Loc.T("ModSource.UnknownVersion");
    }

    /// <summary>
    /// Release label of a local payload: the INI's banner while releases carry one, otherwise the marker
    /// file the downloader writes (the banner disappeared in 0.3.0).
    /// </summary>
    private static string? ReadReleaseLabel(string root)
    {
        var fromIni = ReadVersion(Path.Combine(root, IniName));
        if (fromIni is not null) return fromIni;

        try
        {
            var marker = Path.Combine(root, ModFetcher.VersionMarkerName);
            if (!File.Exists(marker)) return null;

            var text = File.ReadAllText(marker).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    public static string ResolveDllPath(string root, string proxyName) =>
        string.Equals(proxyName, "version.dll", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(root, "version.dll")
            : Path.Combine(root, AltDirName, proxyName);

    public string DllPath(string proxyName) => ResolveDllPath(Root, proxyName);

    public string IniPath => Path.Combine(Root, IniName);

    /// <summary>
    /// True when the payload's INI uses the 0.2.x schema (Router / KernelImage / HardwareBilinear).
    /// 0.3.0 replaced those with Enabled / Optimized / Preset, and the configuration panel shows whichever
    /// set the local payload actually reads — writing the other set would be writing dead keys.
    /// </summary>
    public bool IsLegacySchema => IniText.Contains("Router=", StringComparison.OrdinalIgnoreCase);

    public string IniText
    {
        get
        {
            try { return File.ReadAllText(IniPath, Encoding.UTF8); }
            catch { return ""; }
        }
    }

    public string? PresetPath(string preset) => new[]
    {
        Path.Combine(Root, "config", "presets", preset),
        Path.Combine(Root, "config", "presets", preset + ".ini"),
        Path.Combine(Root, preset),
    }.FirstOrDefault(File.Exists);

    /// <summary>Reads the "; Native 0.2.3." banner the shipped INI opens with.</summary>
    public static string? ReadVersion(string iniPath)
    {
        try
        {
            foreach (var line in File.ReadLines(iniPath).Take(6))
            {
                var version = ReadVersionFromText(line);
                if (version is not null) return version;
            }
        }
        catch
        {
            // A version banner is cosmetic; an unreadable INI is reported by IsValid instead.
        }

        return null;
    }

    /// <summary>
    /// Pulls the version out of INI text. Split from <see cref="ReadVersion"/> so the same rule serves
    /// both the local file and the version probe, which only ever sees a few hundred bytes of the
    /// published INI.
    /// </summary>
    public static string? ReadVersionFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = Regex.Match(text, @"Native\s+([0-9]+(?:\.[0-9]+)+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Proxy DLLs under <c>altnative\</c> beyond the five the project ships, i.e. files the user added.
    /// The file name is the entry name — it is the DLL name the game resolves — so it is the whole
    /// contract, and nothing else about the file is assumed here.
    /// </summary>
    private static List<string> ImportedProxyNames(string root)
    {
        var result = new List<string>();

        try
        {
            var dir = Path.Combine(root, AltDirName);
            if (!Directory.Exists(dir)) return result;

            foreach (var path in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var name = Path.GetFileName(path);
                if (ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(name);
            }
        }
        catch
        {
            // An unreadable folder simply yields no imported entries.
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>
    /// Adds a proxy DLL the user supplies — a community build such as <c>d3d12.dll</c>, which this
    /// project does not ship — so it can be deployed like the bundled entry points.
    ///
    /// The file is copied into <c>altnative\</c> under its own name, because that name *is* the entry
    /// name: it is the DLL name the game resolves. That is also why it cannot be one of the project's
    /// own names — an import would shadow a build whose signature every ownership check relies on.
    ///
    /// Nothing about the file is verified: the manager did not download it and cannot vouch for it. What
    /// it can do is copy it faithfully, record its hash when deploying, and remove exactly what it wrote.
    /// </summary>
    public static OpResult ImportProxy(string root, string sourceFile)
    {
        var r = new OpResult();

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            r.Fail(Loc.T("Proxy.AddNeedSource"));
            return r;
        }

        var name = Path.GetFileName(sourceFile ?? "");
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            r.Fail(Loc.T("Proxy.AddNeedDll"));
            return r;
        }

        if (ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            r.Fail(Loc.T("Proxy.AddReserved", name));
            return r;
        }

        if (!File.Exists(sourceFile))
        {
            r.Fail(Loc.T("Proxy.AddFileMissing", sourceFile ?? ""));
            return r;
        }

        try
        {
            var dest = ResolveDllPath(root, name);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var replaced = File.Exists(dest);
            var tmp = dest + ".dlssgtmp";
            File.Copy(sourceFile, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
            DeploymentService.Unblock(dest);

            if (replaced) r.Note(Loc.T("Proxy.AddReplaced", name));
            r.Note(Loc.T("Proxy.AddedFile", name, new FileInfo(dest).Length / 1024));

            // Say what the file is, since nothing else about it is known: whether it carries an intact
            // signature is the one fact the manager can establish on its own.
            using (var cert = DeploymentService.ReadSignerCertificate(dest, out var signatureIntact))
            {
                r.Note(signatureIntact && cert is not null
                    ? Loc.T("Proxy.AddedSigner", cert.Subject ?? "")
                    : Loc.T("Proxy.AddedUnsigned"));
            }

            r.Message = Loc.T("Proxy.Added", name, root);
        }
        catch (Exception ex)
        {
            r.Fail(Loc.T("Proxy.AddFailed", ex.Message));
        }

        return r;
    }
}
