using System.Globalization;
using System.Text;

namespace DLSSGManager;

/// <summary>Outcome of running a plan, and the deployment record the game should carry afterwards.</summary>
public sealed record ExecutionResult(OpResult Result, bool RecordChanged, DeploymentInfo? Record)
{
    /// <summary>Applies the new record to the game, if the run produced one.</summary>
    public void ApplyTo(GameEntry game)
    {
        if (RecordChanged) game.Deployment = Record;
    }
}

/// <summary>
/// Performs a <see cref="DeployPlan"/>. The only code that writes into game folders.
///
/// It takes no decisions about ownership — the planner already did — but it re-checks the two things
/// that can change between planning and running: a file scheduled for deletion must still have the
/// bytes that were judged ours, and no path may resolve outside the render directory or through a
/// link inside it.
/// </summary>
public static class PlanExecutor
{
    public const string TempSuffix = ".dlssgtmp";

    public static ExecutionResult Execute(DeployPlan plan, DeploymentInfo? previous, IPlanJournal? journal = null)
    {
        journal ??= NullPlanJournal.Instance;
        return plan.Kind == PlanKind.Deploy
            ? RunDeploy(plan, previous, journal)
            : RunRestore(plan, journal);
    }

    private static ExecutionResult RunDeploy(DeployPlan plan, DeploymentInfo? prev, IPlanJournal journal)
    {
        var r = new OpResult();
        var backups = new List<BackupItem>();
        var written = new List<(DeployedFile File, FileRole Role)>();
        string? tmp = null;

        try
        {
            // Check every slot that will be replaced before touching any of them. A locked file must not
            // leave an upgrade half-applied with the old installation's proxies already removed.
            foreach (var op in plan.Ops)
            {
                var target = op switch
                {
                    WriteFileOp w => w.RelativePath,
                    WriteTextOp t => t.RelativePath,
                    _ => null,
                };
                if (target is null) continue;
                var path = Resolve(plan.RenderDir, target);
                if (!File.Exists(path)) continue;
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            journal.Begin(plan);

            foreach (var op in plan.Ops)
            {
                switch (op)
                {
                    case BackupOp b:
                    {
                        var item = Backup(plan, b.RelativePath);
                        backups.Add(item);
                        journal.BackedUp(plan, item);
                        r.Note(Loc.T("Deploy.BackupFile", item.FileName, item.Size / 1024));
                        break;
                    }
                    case WriteFileOp w:
                    {
                        var dest = Resolve(plan.RenderDir, w.RelativePath);
                        journal.Writing(plan, w.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        tmp = dest + TempSuffix;
                        File.Copy(w.SourcePath, tmp, overwrite: true);
                        // Check the copy, not the source: what lands in the game folder is what must match.
                        if (w.ExpectedSha256.Length > 0 &&
                            !DeploymentService.Sha256(tmp).Equals(w.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException(Loc.T("Deploy.SourceChanged", w.RelativePath));
                        File.Move(tmp, dest, overwrite: true);
                        tmp = null;
                        DeploymentService.Unblock(dest);
                        written.Add((Describe(dest, w.RelativePath), w.Role));
                        break;
                    }
                    case WriteTextOp t:
                    {
                        var dest = Resolve(plan.RenderDir, t.RelativePath);
                        journal.Writing(plan, t.RelativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        tmp = dest + TempSuffix;
                        File.WriteAllText(tmp, t.Content, new UTF8Encoding(false));
                        File.Move(tmp, dest, overwrite: true);
                        tmp = null;
                        DeploymentService.Unblock(dest);
                        written.Add((Describe(dest, t.RelativePath), t.Role));
                        break;
                    }
                    case RecordExistingOp e:
                        written.Add((Describe(Resolve(plan.RenderDir, e.RelativePath), e.RelativePath), e.Role));
                        break;
                    case DeleteFileOp d:
                        DeleteOwned(plan, d, r, journal);
                        break;
                    case NoteOp n:
                        r.Note(n.Note.Render());
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported step in a deploy plan: " + op.GetType().Name);
                }
            }

            var record = BuildRecord(plan, prev, backups, written);
            journal.Complete(plan);
            r.Message = Loc.T("Deploy.Success", plan.ProxyName);
            return new ExecutionResult(r, true, record);
        }
        catch (Exception ex)
        {
            // A failed copy/move (locked file, full disk) must not leave a stray temporary file in the
            // game folder — nothing else ever recognises or cleans those.
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
            }

            r.Fail(Loc.T("Deploy.Failed", ex.Message));

            // A multi-file deployment may fail after writing some files. Keep their hashes and the
            // displaced originals so a retry or a restore can still account for every write.
            if (written.Count == 0 && backups.Count == 0)
            {
                journal.Complete(plan);
                return new ExecutionResult(r, false, prev);
            }

            var partial = Clone(prev) ?? new DeploymentInfo { ProxyName = plan.ProxyName };
            partial.AppliedConfiguration = "";
            partial.Files = partial.Files
                .Where(f => written.All(w => !Same(w.File.FileName, f.FileName)))
                .Concat(written.Select(w => w.File))
                .ToList();
            partial.Backups = partial.Backups
                .Where(b => backups.All(n => !Same(n.FileName, b.FileName)))
                .Concat(backups)
                .ToList();
            if (backups.Count > 0) partial.RestoreFolder = plan.RestoreFolder;
            journal.Complete(plan);
            return new ExecutionResult(r, true, partial);
        }
    }

    private static DeploymentInfo BuildRecord(DeployPlan plan, DeploymentInfo? prev, List<BackupItem> backups,
        List<(DeployedFile File, FileRole Role)> written)
    {
        var proxy = written.Last(w => w.Role == FileRole.Proxy).File;
        var ini = written.Last(w => w.Role == FileRole.Ini).File;

        // Carry the previous record's backups into the new one. Without this, a redeploy (a settings
        // change, or a batch deploy over a deployed game) silently drops the only references to
        // displaced foreign files. New backups win on a name clash.
        var carried = (prev?.Backups ?? new List<BackupItem>())
            .Where(b => backups.All(n => !Same(n.FileName, b.FileName)))
            .Concat(backups)
            .ToList();

        return new DeploymentInfo
        {
            RuntimeModel = plan.RuntimeModel,
            AppliedConfiguration = plan.AppliedConfiguration,
            ProxyName = plan.ProxyName,
            ModVersion = plan.ModVersion,
            DeployedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ProxySha256 = proxy.Sha256,
            IniSha256 = ini.Sha256,
            // Restore works from each item's StoredPath, not this string; it names the folder this
            // deploy wrote into, or the previous one's when nothing new was displaced.
            RestoreFolder = backups.Count > 0 ? plan.RestoreFolder : prev?.RestoreFolder ?? "",
            Backups = carried,
            // Every file this deployment is responsible for: proxy, INI, runtime components, and any
            // standby proxies left in place. The record is what lets later checks recognise files
            // that carry no signature this project can vouch for.
            Files = new[] { proxy, ini }
                .Concat(written.Where(w => w.Role == FileRole.Runtime).Select(w => w.File))
                .Concat(written.Where(w => w.Role == FileRole.Standby).Select(w => w.File))
                .ToList(),
        };
    }

    private static ExecutionResult RunRestore(DeployPlan plan, IPlanJournal journal)
    {
        var r = new OpResult();
        var removed = 0;

        try
        {
            journal.Begin(plan);

            foreach (var op in plan.Ops)
            {
                switch (op)
                {
                    case DeleteFileOp d:
                        if (DeleteOwned(plan, d, r, journal)) removed++;
                        break;
                    case RestoreBackupOp b:
                    {
                        if (!File.Exists(b.Backup.StoredPath))
                        {
                            r.Note(Loc.T("Restore.BackupLost", b.Backup.FileName));
                            break;
                        }

                        var dest = Resolve(plan.RenderDir, b.RelativePath);
                        if (b.SkipIfExists && File.Exists(dest))
                        {
                            r.Note(Loc.T("Restore.ProxyKept", b.Backup.FileName));
                            break;
                        }

                        // The restore folder is user-writable: put back only the bytes recorded when the
                        // original was set aside. Checked on the copy, so a swap during the copy is caught too.
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        var restoreTmp = dest + TempSuffix;
                        File.Copy(b.Backup.StoredPath, restoreTmp, overwrite: true);
                        if (b.Backup.Sha256.Length > 0 &&
                            !DeploymentService.Sha256(restoreTmp).Equals(b.Backup.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(restoreTmp);
                            r.Note(Loc.T("Restore.BackupChanged", b.Backup.FileName));
                            break;
                        }
                        File.Move(restoreTmp, dest, overwrite: true);
                        removed++;
                        r.Note(Loc.T("Restore.BackupRestored", b.Backup.FileName));
                        break;
                    }
                    case DeleteDirectoryOp dir:
                    {
                        var path = Resolve(plan.RenderDir, dir.RelativePath);
                        if (Directory.Exists(path))
                        {
                            Directory.Delete(path, recursive: true);
                            if (dir.Note is not null) r.Note(dir.Note.Render());
                        }
                        break;
                    }
                    case PruneEmptyDirectoriesOp prune:
                        foreach (var relative in prune.RelativePaths)
                        {
                            var path = Resolve(plan.RenderDir, relative);
                            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                                Directory.Delete(path);
                        }
                        break;
                    case NoteOp n:
                        r.Note(n.Note.Render());
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported step in a restore plan: " + op.GetType().Name);
                }
            }

            journal.Complete(plan);
            r.Message = removed > 0 ? Loc.T("Restore.Done", removed) : Loc.T("Restore.None");
            r.Note(r.Message);
            return new ExecutionResult(r, true, null);
        }
        catch (Exception ex)
        {
            journal.Complete(plan);
            r.Fail(Loc.T("Restore.Failed", ex.Message));
            return new ExecutionResult(r, false, null);
        }
    }

    /// <summary>Deletes a file only while it still has the bytes the planner judged ours.</summary>
    private static bool DeleteOwned(DeployPlan plan, DeleteFileOp d, OpResult r, IPlanJournal journal)
    {
        var path = Resolve(plan.RenderDir, d.RelativePath);
        try
        {
            if (!File.Exists(path)) return false;
            if (!DeploymentService.Sha256(path).Equals(d.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                r.Note(Loc.T("Restore.ProxyKept", d.RelativePath));
                return false;
            }

            journal.Deleting(plan, d.RelativePath);
            File.Delete(path);
            if (d.Note is not null) r.Note(d.Note.Render());
            return true;
        }
        catch (Exception ex) when (d.BestEffort)
        {
            r.Note(Loc.T(d.FailureNoteKey ?? "Deploy.Failed", Path.GetFileName(path), ex.Message));
            return false;
        }
    }

    private static BackupItem Backup(DeployPlan plan, string relative)
    {
        var source = Resolve(plan.RenderDir, relative);
        var stored = Path.Combine(plan.RestoreFolder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
        File.Copy(source, stored, overwrite: true);
        return new BackupItem
        {
            FileName = relative,
            StoredPath = stored,
            Sha256 = DeploymentService.Sha256(stored),
            Size = new FileInfo(stored).Length,
        };
    }

    private static DeployedFile Describe(string path, string relative) => new()
    {
        FileName = relative,
        Sha256 = DeploymentService.Sha256(path),
        Size = new FileInfo(path).Length,
    };

    /// <summary>
    /// Turns a plan path into a full path under the render directory. Rejects anything that escapes it,
    /// and any existing directory on the way that is a link: writing through a junction planted in a
    /// game folder would put files somewhere the user never chose.
    /// </summary>
    internal static string Resolve(string renderDir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidOperationException("Invalid path in plan: " + relative);

        var root = Path.GetFullPath(renderDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the game folder: " + relative);

        // Every directory from the target's parent up to and including the render directory itself:
        // the root is checked too, so swapping it for a junction after planning is caught here.
        var dir = Path.GetDirectoryName(full);
        while (dir is not null && dir.Length >= root.Length - 1)
        {
            if (Directory.Exists(dir) && (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Directory is a link: " + dir);
            dir = Path.GetDirectoryName(dir);
        }
        // The target itself must not be a link either: replacing it would write through to the link's target.
        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("File is a link: " + full);

        return full;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static DeploymentInfo? Clone(DeploymentInfo? info) => info is null
        ? null
        : System.Text.Json.JsonSerializer.Deserialize<DeploymentInfo>(System.Text.Json.JsonSerializer.Serialize(info));
}

/// <summary>
/// Receives progress while a plan runs, so an interrupted run can be detected and undone on the next
/// start. See <see cref="FilePlanJournal"/>.
/// </summary>
public interface IPlanJournal
{
    void Begin(DeployPlan plan);
    void BackedUp(DeployPlan plan, BackupItem backup);
    void Writing(DeployPlan plan, string relativePath);
    void Deleting(DeployPlan plan, string relativePath);
    void Complete(DeployPlan plan);
}

public sealed class NullPlanJournal : IPlanJournal
{
    public static NullPlanJournal Instance { get; } = new();
    public void Begin(DeployPlan plan) { }
    public void BackedUp(DeployPlan plan, BackupItem backup) { }
    public void Writing(DeployPlan plan, string relativePath) { }
    public void Deleting(DeployPlan plan, string relativePath) { }
    public void Complete(DeployPlan plan) { }
}
