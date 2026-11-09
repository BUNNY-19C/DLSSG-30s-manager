using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace DLSSGManager;

/// <summary>A plan sent to the elevated helper.</summary>
public sealed record ElevatedRequest(DeployPlan Plan, DeploymentInfo? Previous, string DataRoot);

/// <summary>What the helper reports back: the run's log and the record the game should carry.</summary>
public sealed record ElevatedResponse(bool Ok, string Message, List<string> Lines, bool RecordChanged, DeploymentInfo? Record);

/// <summary>
/// The wire format between the manager and <c>DLSSGManager.Elevated.exe</c>.
///
/// The manager stays unelevated. When a game folder needs administrator rights, it starts a second
/// copy of itself through UAC in helper mode (<see cref="Shell.ElevatedSwitch"/>) with a random pipe
/// name, sends one plan, reads one result, and the helper exits without showing a window. The pipe
/// only admits the user who started it, and the helper accepts exactly one connection. Messages are
/// length-prefixed JSON with a size cap.
/// </summary>
public static class ElevatedProtocol
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

    /// <summary>
    /// Checks a plan received from the unelevated side before running it with administrator rights.
    ///
    /// The executor already confines every path to the render directory, refuses links, and pins every
    /// copied file to its planned hash. This adds what only matters across the privilege boundary: the
    /// render directory must be an ordinary existing folder, not a system location, and every source file
    /// must come from the mod store the manager uses — not from an arbitrary path chosen by whoever wrote
    /// the request.
    /// </summary>
    public static string? Validate(ElevatedRequest request, IReadOnlyList<string> allowedSourceRoots)
    {
        var plan = request.Plan;
        if (plan is null) return "missing plan";

        string root;
        try { root = Path.GetFullPath(plan.RenderDir); }
        catch { return "invalid render directory"; }
        if (!Path.IsPathFullyQualified(plan.RenderDir) || !Directory.Exists(root)) return "render directory does not exist";
        if (Path.GetPathRoot(root)?.TrimEnd('\\') == root.TrimEnd('\\')) return "render directory is a drive root";
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return "render directory is a link";

        foreach (var system in ProtectedRoots())
        {
            if (IsUnder(root, system)) return "render directory is a system folder";
        }

        var roots = allowedSourceRoots.Select(r => Path.GetFullPath(r).TrimEnd('\\') + "\\").ToArray();
        foreach (var op in plan.Ops)
        {
            switch (op)
            {
                case WriteFileOp w:
                    if (w.ExpectedSha256.Length != 64) return "write step without a pinned hash";
                    var source = Path.GetFullPath(w.SourcePath);
                    if (!roots.Any(r => source.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                        return "source outside the mod store: " + w.SourcePath;
                    break;
                case RestoreBackupOp b:
                    var stored = Path.GetFullPath(b.Backup.StoredPath);
                    var restoreRoot = Path.GetFullPath(Path.Combine(request.DataRoot, "restore")).TrimEnd('\\') + "\\";
                    if (!stored.StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase))
                        return "backup outside the restore folder: " + b.Backup.StoredPath;
                    break;
            }
        }

        if (plan.Kind == PlanKind.Deploy && plan.RestoreFolder.Length > 0)
        {
            var restoreRoot = Path.GetFullPath(Path.Combine(request.DataRoot, "restore")).TrimEnd('\\') + "\\";
            if (!Path.GetFullPath(plan.RestoreFolder).StartsWith(restoreRoot, StringComparison.OrdinalIgnoreCase))
                return "restore folder outside the data folder";
        }

        return null;
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
        {
            var path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(path)) yield return path;
        }
    }

    private static bool IsUnder(string path, string root) =>
        (path.TrimEnd('\\') + "\\").StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
