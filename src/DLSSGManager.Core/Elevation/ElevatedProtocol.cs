using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>A plan sent to the elevated helper.</summary>
/// <param name="DataRoot">
/// The manager's data folder, for information only. The helper never trusts it: its restore folder,
/// journal and mod store are derived on the elevated side.
/// </param>
public sealed record ElevatedRequest(DeployPlan Plan, DeploymentInfo? Previous, string DataRoot);

/// <summary>What the helper reports back: the run's log and the record the game should carry.</summary>
public sealed record ElevatedResponse(bool Ok, string Message, List<string> Lines, bool RecordChanged, DeploymentInfo? Record);

/// <summary>What the elevated side trusts, derived by the helper itself and never taken from a request.</summary>
public sealed record ElevationTrust(string RestoreRoot, IReadOnlyList<string> SourceRoots)
{
    /// <summary>From this process's own profile and install location.</summary>
    public static ElevationTrust ForCurrentProcess() => new(AppPaths.RestoreRoot, ElevatedHost.SourceRoots());
}

/// <summary>
/// The wire format between the manager and its elevated helper, and the checks the helper applies.
///
/// The manager stays unelevated. When a game folder needs administrator rights, it starts a second copy
/// of itself through UAC in helper mode (<see cref="Shell.ElevatedSwitch"/>) with a random pipe name,
/// sends one plan, reads one result, and the helper exits. Anything able to start the helper can send
/// it a plan, so the helper treats every request as hostile: see <see cref="Validate"/>.
/// </summary>
public static partial class ElevatedProtocol
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new();

    /// <summary>A pipe the current user, and only the current user, may connect to.</summary>
    public static NamedPipeServerStream CreateServer(string pipeName)
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length > MaxMessageBytes) throw new InvalidDataException("Message too large.");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes) throw new InvalidDataException("Bad message length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidDataException("Empty message.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);

    /// <summary>The process at the other end of a connected server pipe.</summary>
    public static int? ClientProcessId(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) ? (int)id : null;

    /// <summary>The process that created a pipe this client is connected to.</summary>
    public static int? ServerProcessId(NamedPipeClientStream pipe) =>
        GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) ? (int)id : null;

    // ---- validation --------------------------------------------------------------------------

    /// <summary>
    /// Checks a plan before it runs with administrator rights. Returns null when acceptable, otherwise
    /// the reason it was refused.
    ///
    /// Nothing in the request is trusted. The target must be an ordinary drive folder whose real path
    /// (links resolved) is what the request names and lies outside Windows; every step must be one the
    /// planner produces, on a path the manager owns there (the INI, the runtime directory, proxy entry
    /// names, quarantine copies, the log folder); every copied file must be a release file from a known
    /// manifest or a file in the helper's own mod store; deletions only remove release files, the INI or
    /// stored copies of mod-store files; backups come only from the helper's own restore folder.
    /// </summary>
    public static string? Validate(ElevatedRequest request, ElevationTrust trust)
    {
        var plan = request.Plan;
        if (plan is null) return "missing plan";

        var root = ValidateRoot(plan.RenderDir);
        if (root.Error is not null) return root.Error;

        var shape = ValidateShape(plan);
        if (shape is not null) return shape;

        var restoreRoot = WithSlash(Path.GetFullPath(trust.RestoreRoot));
        var sourceRoots = trust.SourceRoots.Select(r => WithSlash(Path.GetFullPath(r))).ToArray();
        var storeHashes = new Lazy<HashSet<string>>(() => ModStoreHashes(sourceRoots));

        if (plan.Kind == PlanKind.Deploy && !IsUnder(Path.GetFullPath(plan.RestoreFolder), restoreRoot))
            return "restore folder outside the helper's data folder";

        foreach (var op in plan.Ops)
        {
            switch (op)
            {
                case WriteFileOp w:
                {
                    var source = Path.GetFullPath(w.SourcePath);
                    if (!sourceRoots.Any(r => IsUnder(source, r))) return "source outside the mod store: " + w.SourcePath;
                    if (!PayloadCatalog.IsReleaseFile(w.ExpectedSha256) && !storeHashes.Value.Contains(w.ExpectedSha256))
                        return "write of a file that is neither a release file nor in the mod store: " + w.RelativePath;
                    break;
                }
                case DeleteFileOp d:
                    if (!string.Equals(d.RelativePath, ModSource.IniName, StringComparison.OrdinalIgnoreCase) &&
                        !PayloadCatalog.IsReleaseFile(d.ExpectedSha256) &&
                        !ModFetcher.KnownCommunityBuildHashes.Contains(d.ExpectedSha256, StringComparer.OrdinalIgnoreCase) &&
                        !storeHashes.Value.Contains(d.ExpectedSha256))
                        return "deletion of a file that is not provably the manager's: " + d.RelativePath;
                    break;
                case RestoreBackupOp b:
                    if (!IsUnder(Path.GetFullPath(b.Backup.StoredPath), restoreRoot))
                        return "backup outside the helper's restore folder: " + b.Backup.StoredPath;
                    if (b.Backup.Sha256.Length != 64) return "backup without a recorded hash: " + b.Backup.FileName;
                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// The structural checks every plan must pass before it runs unattended (elevated, or replayed from a
    /// journal after a crash): each step is one the planner produces for its kind, on a path the manager
    /// owns inside the render directory.
    /// </summary>
    public static string? ValidateShape(DeployPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.GameId) || plan.GameId.Length > 64 || plan.GameId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            return "invalid game id";
        if (plan.Kind == PlanKind.Deploy && string.IsNullOrWhiteSpace(plan.RestoreFolder))
            return "deploy plan without a restore folder";

        var deploy = plan.Kind == PlanKind.Deploy;
        foreach (var op in plan.Ops)
        {
            var error = op switch
            {
                BackupOp b when deploy => CheckPath(b.RelativePath, PathUse.Owned),
                WriteFileOp w when deploy => w.ExpectedSha256.Length == 64 ? CheckPath(w.RelativePath, PathUse.Owned) : "write step without a pinned hash",
                WriteTextOp t when deploy => string.Equals(t.RelativePath, ModSource.IniName, StringComparison.OrdinalIgnoreCase) ? null : "text write outside the INI",
                RecordExistingOp e when deploy => CheckPath(e.RelativePath, PathUse.Owned),
                DeleteFileOp d => d.ExpectedSha256.Length == 64 ? CheckPath(d.RelativePath, PathUse.Deletable) : "delete step without a hash",
                RestoreBackupOp b when !deploy => CheckPath(b.RelativePath, PathUse.Owned) ??
                    (string.Equals(b.RelativePath, b.Backup.FileName, StringComparison.OrdinalIgnoreCase) ? null : "backup restored under another name"),
                DeleteDirectoryOp dir when !deploy => string.Equals(dir.RelativePath, ModSource.LogDirName + "/logs", StringComparison.OrdinalIgnoreCase)
                    ? null : "directory removal outside the log folder",
                PruneEmptyDirectoriesOp prune when !deploy => prune.RelativePaths.All(p => PrunableDirectories.Contains(p)) ? null : "prune outside the runtime folders",
                NoteOp => null,
                _ => $"step {op.GetType().Name} is not allowed in a {plan.Kind} plan",
            };
            if (error is not null) return error;
        }
        return null;
    }

    private enum PathUse { Owned, Deletable }

    private static readonly HashSet<string> PrunableDirectories = new(
        ModSource.RuntimeFiles.Select(p => p[..p.LastIndexOf('/')])
            .Append(ModSource.LogDirName + "/logs")
            .Append(ModSource.LogDirName),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Paths the manager may touch inside a game folder: the INI, the runtime files, and a proxy entry
    /// (a single <c>.dll</c> name at the top level — imported proxies keep their own names). Deletions may
    /// also remove a quarantined copy, which an anti-cheat leaves as <c>&lt;proxy&gt;.dll.&lt;digits&gt;</c>.
    /// </summary>
    private static string? CheckPath(string relative, PathUse use)
    {
        if (string.Equals(relative, ModSource.IniName, StringComparison.OrdinalIgnoreCase)) return null;
        if (ModSource.RuntimeFiles.Contains(relative, StringComparer.OrdinalIgnoreCase)) return null;
        if (TopLevelDll().IsMatch(relative)) return null;
        if (use == PathUse.Deletable && QuarantineCopy().IsMatch(relative)) return null;
        return "path the manager does not own: " + relative;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex TopLevelDll();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}\.dll\.[A-Za-z0-9_\-]{1,32}$", RegexOptions.IgnoreCase)]
    private static partial Regex QuarantineCopy();

    /// <summary>
    /// The render directory must be an existing local drive folder, not a drive root, not under Windows,
    /// and its real path (every junction and symbolic link on the way resolved) must be the path
    /// requested — so a link planted in any ancestor cannot redirect the writes.
    /// </summary>
    public static (string? Real, string? Error) ValidateRoot(string renderDir)
    {
        if (string.IsNullOrWhiteSpace(renderDir)) return (null, "missing render directory");
        if (renderDir.StartsWith(@"\\", StringComparison.Ordinal) || renderDir.StartsWith("//", StringComparison.Ordinal))
            return (null, "network or device paths are not accepted");
        if (!Path.IsPathFullyQualified(renderDir) || renderDir.Length < 4 || renderDir[1] != ':' || renderDir.Contains('~'))
            return (null, "render directory is not a plain drive path");

        string full;
        try { full = Path.GetFullPath(renderDir).TrimEnd('\\'); }
        catch { return (null, "invalid render directory"); }
        if (!Directory.Exists(full)) return (null, "render directory does not exist");
        if (full.Count(c => c == '\\') < 2) return (null, "render directory is too close to the drive root");

        string? real;
        try
        {
            using var handle = Native.OpenDirectoryPinned(full);
            real = Native.FinalPath(handle);
        }
        catch (Exception ex)
        {
            return (null, "render directory cannot be opened: " + ex.Message);
        }
        if (real is null) return (null, "render directory is not on a local drive");
        if (!string.Equals(real.TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase))
            return (null, "render directory is reached through a link");

        foreach (var system in ProtectedRoots())
        {
            if (IsUnder(WithSlash(real), WithSlash(system))) return (null, "render directory is a system folder");
        }

        return (real, null);
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    /// <summary>Hashes of the DLLs in the helper's own mod stores (imported proxies have no manifest entry).</summary>
    private static HashSet<string> ModStoreHashes(IEnumerable<string> roots)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
                {
                    if (new FileInfo(dll).Length > 512L * 1024 * 1024) continue;
                    result.Add(DeploymentService.Sha256(dll));
                }
            }
            catch
            {
                // An unreadable store contributes nothing; the request is then refused, not trusted.
            }
        }
        return result;
    }

    private static string WithSlash(string path) => path.TrimEnd('\\') + "\\";

    private static bool IsUnder(string path, string rootWithSlash) =>
        WithSlash(path).StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase);
}
