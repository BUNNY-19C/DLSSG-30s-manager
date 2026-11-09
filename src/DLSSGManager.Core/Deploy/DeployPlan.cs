using System.Text.Json.Serialization;

namespace DLSSGManager;

/// <summary>Whether a plan installs into a game folder or takes an installation back out.</summary>
public enum PlanKind
{
    Deploy,
    Restore,
}

/// <summary>What a file written into a game folder is, which decides where its hash is recorded.</summary>
public enum FileRole
{
    Proxy,
    Ini,
    Runtime,
    /// <summary>An extra proxy of ours left in place as a standby forwarder (0.3.3+ payloads).</summary>
    Standby,
}

/// <summary>
/// A log line kept as a string-table key plus arguments, rendered when the plan runs. Arguments are
/// strings so a plan can be serialized (for the elevated helper and the crash journal).
/// </summary>
public sealed record NoteSpec(string Key, string[] Args)
{
    public static NoteSpec Of(string key, params string[] args) => new(key, args);

    public string Render() => Args.Length == 0 ? Loc.T(Key) : Loc.T(Key, Args.Cast<object?>().ToArray());
}

/// <summary>
/// One step of a plan. Paths are relative to the plan's render directory and use '/' separators; the
/// executor resolves them and refuses anything that would leave that directory.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(BackupOp), "backup")]
[JsonDerivedType(typeof(WriteFileOp), "write-file")]
[JsonDerivedType(typeof(WriteTextOp), "write-text")]
[JsonDerivedType(typeof(RecordExistingOp), "record")]
[JsonDerivedType(typeof(DeleteFileOp), "delete")]
[JsonDerivedType(typeof(RestoreBackupOp), "restore-backup")]
[JsonDerivedType(typeof(DeleteDirectoryOp), "delete-dir")]
[JsonDerivedType(typeof(PruneEmptyDirectoriesOp), "prune")]
[JsonDerivedType(typeof(NoteOp), "note")]
public abstract record FileOp;

/// <summary>Copies a file that is not ours into the plan's restore folder before it is replaced.</summary>
public sealed record BackupOp(string RelativePath) : FileOp;

/// <summary>
/// Copies <paramref name="SourcePath"/> into the game folder through a temporary file. The copy must
/// have <paramref name="ExpectedSha256"/>: the payload folder is user-writable, so a file swapped after
/// planning (or between an unelevated plan and its elevated execution) is refused rather than installed.
/// </summary>
public sealed record WriteFileOp(string RelativePath, string SourcePath, FileRole Role, string ExpectedSha256 = "") : FileOp;

/// <summary>Writes text (the rendered INI) into the game folder through a temporary file.</summary>
public sealed record WriteTextOp(string RelativePath, string Content, FileRole Role) : FileOp;

/// <summary>Records a file already in place as part of the deployment without changing it.</summary>
public sealed record RecordExistingOp(string RelativePath, FileRole Role) : FileOp;

/// <summary>
/// Deletes a file that was proven ours while planning. The file is only deleted if its bytes still
/// match <paramref name="ExpectedSha256"/>; anything that changed since the check is left alone.
/// </summary>
public sealed record DeleteFileOp(
    string RelativePath,
    string ExpectedSha256,
    NoteSpec? Note = null,
    bool BestEffort = false,
    string? FailureNoteKey = null) : FileOp;

/// <summary>Puts a displaced original back. Runtime files are skipped when something already occupies the slot.</summary>
public sealed record RestoreBackupOp(BackupItem Backup, string RelativePath, bool SkipIfExists) : FileOp;

/// <summary>Removes a directory and everything in it (the mod's own log folder).</summary>
public sealed record DeleteDirectoryOp(string RelativePath, NoteSpec? Note = null) : FileOp;

/// <summary>Removes the listed directories when they are empty, in order.</summary>
public sealed record PruneEmptyDirectoriesOp(string[] RelativePaths) : FileOp;

/// <summary>A decision taken while planning that the user should see in the log.</summary>
public sealed record NoteOp(NoteSpec Note) : FileOp;

/// <summary>
/// Everything a deploy or restore will do to one game folder, decided before anything is touched.
///
/// Planning reads the folder and the deployment record; executing performs the steps in order. The
/// split keeps every ownership decision in one place that writes nothing, lets the steps be inspected
/// (or handed to an elevated helper) before they run, and gives the crash journal something concrete
/// to replay.
/// </summary>
public sealed record DeployPlan(PlanKind Kind, string GameId, string RenderDir, IReadOnlyList<FileOp> Ops)
{
    /// <summary>Entry name the proxy is installed under (deploy only).</summary>
    public string ProxyName { get; init; } = "";

    public string ModVersion { get; init; } = "";
    public string RuntimeModel { get; init; } = "";

    /// <summary>Configuration snapshot recorded with the deployment, see <see cref="ConfigurationState"/>.</summary>
    public string AppliedConfiguration { get; init; } = "";

    /// <summary>Folder that receives this run's backups; only created when something is displaced.</summary>
    public string RestoreFolder { get; init; } = "";
}

/// <summary>Result of planning: a plan, or the reason there is none.</summary>
public sealed record PlanOutcome(DeployPlan? Plan, string? Error)
{
    /// <summary>The plan is complete, but this process cannot write into the game folder.</summary>
    public bool NeedsElevation { get; init; }

    /// <summary>Anti-cheat scan made while planning a deploy, so the caller can update the banner.</summary>
    public ProtectionReport? Protection { get; init; }

    public static PlanOutcome Success(DeployPlan plan) => new(plan, null);
    public static PlanOutcome Failure(string error) => new(null, error);
}
