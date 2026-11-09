using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DLSSGManager;

/// <summary>What a payload file is for.</summary>
public enum PayloadFileKind
{
    /// <summary>A proxy DLL, deployed under an entry name the game loads.</summary>
    Proxy,
    /// <summary>The INI template.</summary>
    Config,
    /// <summary>A file of the <c>dlssg_sm86/</c> runtime directory.</summary>
    Runtime,
    /// <summary>Documentation; optional, never deployed.</summary>
    Doc,
}

/// <summary>Who signs a payload file.</summary>
public enum PayloadSigner
{
    None,
    /// <summary>The upstream project's self-signed certificate (<see cref="ProjectSigning"/>).</summary>
    Project,
    /// <summary>NVIDIA, chaining to a trusted root. Such a file is not proof the manager wrote it.</summary>
    Nvidia,
}

/// <summary>Which download a file belongs to.</summary>
public enum PayloadPackageKind
{
    /// <summary>Frame generation and XeSS; present in every download.</summary>
    Base,
    /// <summary>Super-resolution kernels, only in the release archive.</summary>
    Full,
}

/// <summary>One file of a release, with the exact bytes it must have.</summary>
public sealed record PayloadFile
{
    /// <summary>Path inside the upstream repository and release archive.</summary>
    public string Source { get; init; } = "";

    /// <summary>Path in the manager's local layout (0.3.0 renamed <c>altnative/</c>; the local name stayed).</summary>
    public string Destination { get; init; } = "";

    public PayloadFileKind Kind { get; init; }
    public PayloadSigner Signer { get; init; }
    public PayloadPackageKind Package { get; init; }
    public long Size { get; init; }
    public string Sha256 { get; init; } = "";

    /// <summary>Must be present for the payload to be usable.</summary>
    [JsonIgnore]
    public bool Required => Package == PayloadPackageKind.Base && Kind != PayloadFileKind.Doc;
}

/// <summary>What a release's INI and layout support; replaces sniffing the template text where known.</summary>
public sealed record PayloadCapabilities
{
    /// <summary>The INI schema generation: "0.2", "0.3", "0.4.0" or "0.4.1".</summary>
    public string IniSchema { get; init; } = "";

    /// <summary>The release deploys the <c>dlssg_sm86/</c> runtime directory beside the proxy.</summary>
    public bool RuntimeDirectory { get; init; }

    /// <summary>Exactly one proxy may live in a game folder; extra ones of ours are removed on deploy.</summary>
    public bool SingleProxy { get; init; }

    [JsonIgnore] public bool LegacySchema => IniSchema.StartsWith("0.2", StringComparison.Ordinal);
    [JsonIgnore] public bool Schema041 => IniSchema == "0.4.1";
}

/// <summary>
/// The exact contents of one upstream release.
///
/// Every file is identified by SHA-256, so a download is accepted only when it is byte-for-byte the
/// release, whichever mirror served it, and a file in a game folder is recognised as the manager's when
/// its bytes are a release file — no certificate name involved.
/// </summary>
public sealed record PayloadManifest
{
    public string Version { get; init; } = "";
    public string RuntimeModel { get; init; } = BuildCatalog.Default;
    public PayloadCapabilities Capabilities { get; init; } = new();
    public IReadOnlyList<PayloadFile> Files { get; init; } = Array.Empty<PayloadFile>();

    public PayloadFile? Ini => Files.FirstOrDefault(f => f.Kind == PayloadFileKind.Config);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static PayloadManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<PayloadManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty payload manifest.");
        manifest.Validate();
        return manifest;
    }

    /// <summary>Rejects a manifest that could not describe a usable, safely installable payload.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version)) throw new InvalidDataException("Manifest has no version.");
        if (Files.Count == 0) throw new InvalidDataException($"Manifest {Version} lists no files.");
        if (Ini is null) throw new InvalidDataException($"Manifest {Version} has no INI.");

        foreach (var file in Files)
        {
            if (file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Manifest {Version}: bad hash for {file.Source}.");
            foreach (var path in new[] { file.Source, file.Destination })
            {
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
                    path.Split('/', '\\').Any(part => part is "" or "." or ".."))
                    throw new InvalidDataException($"Manifest {Version}: unsafe path {path}.");
            }
        }

        var duplicates = Files.GroupBy(f => f.Destination, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0) throw new InvalidDataException($"Manifest {Version}: duplicate destination {duplicates[0].Key}.");
    }
}

/// <summary>
/// The releases this build of the manager knows, read from the manifests embedded in the assembly
/// (<c>Payload/Manifests/*.json</c>). Adding support for a release that keeps the same layout and INI
/// schema is a new manifest file, not a code change.
/// </summary>
public static class PayloadCatalog
{
    private static readonly Lazy<IReadOnlyList<PayloadManifest>> Embedded = new(LoadEmbedded);
    private static readonly object TestLock = new();
    private static IReadOnlyList<PayloadManifest> _extra = Array.Empty<PayloadManifest>();

    public static IReadOnlyList<PayloadManifest> All
    {
        get
        {
            lock (TestLock) return _extra.Count == 0 ? Embedded.Value : Embedded.Value.Concat(_extra).ToArray();
        }
    }

    /// <summary>The release downloads fetch.</summary>
    public static PayloadManifest Supported =>
        Embedded.Value.FirstOrDefault(m => m.Version == ModFetcher.SupportedRelease)
        ?? throw new InvalidOperationException("No manifest embedded for release " + ModFetcher.SupportedRelease);

    public static PayloadManifest? Find(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : All.FirstOrDefault(m => m.Version == version.Trim().TrimStart('v'));

    /// <summary>The release whose INI template has exactly these bytes, if any.</summary>
    public static PayloadManifest? FindByIniHash(string? sha256) =>
        string.IsNullOrEmpty(sha256) ? null
            : All.FirstOrDefault(m => string.Equals(m.Ini?.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the bytes are a file this project builds (not NVIDIA's runtime, which games also ship,
    /// and not documentation or the INI template, which users edit).
    /// </summary>
    public static bool IsProjectBuild(string sha256) =>
        All.SelectMany(m => m.Files)
           .Any(f => f.Signer == PayloadSigner.Project &&
                     string.Equals(f.Sha256, sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a manifest for the duration of a test. Hash-based lookups see it; downloads do not.</summary>
    internal static IDisposable AddForTesting(PayloadManifest manifest)
    {
        lock (TestLock) _extra = _extra.Append(manifest).ToArray();
        return new Removal(manifest);
    }

    private sealed class Removal(PayloadManifest manifest) : IDisposable
    {
        public void Dispose()
        {
            lock (TestLock) _extra = _extra.Where(m => !ReferenceEquals(m, manifest)).ToArray();
        }
    }

    private static IReadOnlyList<PayloadManifest> LoadEmbedded()
    {
        var assembly = typeof(PayloadCatalog).Assembly;
        var result = new List<PayloadManifest>();
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(".Payload.Manifests.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            result.Add(PayloadManifest.Parse(reader.ReadToEnd()));
        }
        return result;
    }
}
