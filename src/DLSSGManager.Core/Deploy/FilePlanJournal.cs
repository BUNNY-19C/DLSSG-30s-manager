using System.Text.Json;

namespace DLSSGManager;

/// <summary>What happened to one game folder before a run was interrupted.</summary>
public sealed record JournalEntry
{
    public DeployPlan Plan { get; init; } = null!;

    /// <summary>The game's deployment record when the run started; restored if the run is rolled back.</summary>
    public DeploymentInfo? Previous { get; init; }

    public DateTime StartedAt { get; init; }

    /// <summary>Originals copied aside before being replaced, in order.</summary>
    public List<BackupItem> BackedUp { get; init; } = new();

    /// <summary>Relative paths the run began writing, in order.</summary>
    public List<string> Writing { get; init; } = new();

    /// <summary>Relative paths the run began deleting, in order.</summary>
    public List<string> Deleting { get; init; } = new();
}

/// <summary>
/// Write-ahead journal for plan execution, one file per game under <c>%APPDATA%\DLSSGManager\journal</c>.
///
/// Every step is recorded before it touches the game folder and the file is flushed to disk, so a run
/// cut short by a crash, a kill or a power loss leaves behind exactly what it was doing. On the next
/// start <see cref="PlanRecovery"/> reads these and undoes the partial run. A run that ends normally —
/// success or handled failure — deletes its journal.
/// </summary>
public sealed class FilePlanJournal : IPlanJournal
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly DeploymentInfo? _previous;
    private JournalEntry? _entry;

    public FilePlanJournal(DeploymentInfo? previous, string? directory = null)
    {
        _previous = previous;
        _directory = directory ?? DefaultDirectory;
    }

    public static string DefaultDirectory => Path.Combine(AppPaths.Root, "journal");

    public static string PathFor(string directory, string gameId) => Path.Combine(directory, gameId + ".json");

    public void Begin(DeployPlan plan)
    {
        lock (_sync)
        {
            _entry = new JournalEntry { Plan = plan, Previous = _previous, StartedAt = DateTime.Now };
            Flush(plan.GameId);
        }
    }

    public void BackedUp(DeployPlan plan, BackupItem backup) => Update(plan, e => e.BackedUp.Add(backup));

    public void Writing(DeployPlan plan, string relativePath) => Update(plan, e => e.Writing.Add(relativePath));

    public void Deleting(DeployPlan plan, string relativePath) => Update(plan, e => e.Deleting.Add(relativePath));

    public void Complete(DeployPlan plan)
    {
        lock (_sync)
        {
            _entry = null;
            try { File.Delete(PathFor(_directory, plan.GameId)); }
            catch (Exception ex) { AppPaths.Log("删除操作日志失败: " + ex.Message); }
        }
    }

    private void Update(DeployPlan plan, Action<JournalEntry> change)
    {
        lock (_sync)
        {
            if (_entry is null) return;
            change(_entry);
            Flush(plan.GameId);
        }
    }

    /// <summary>Writes the journal and forces it to disk before the step it describes runs.</summary>
    private void Flush(string gameId)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(_directory, gameId);
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _entry, Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Journals left behind by interrupted runs.</summary>
    public static IReadOnlyList<(string Path, JournalEntry? Entry)> Pending(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        if (!Directory.Exists(dir)) return Array.Empty<(string, JournalEntry?)>();

        var result = new List<(string, JournalEntry?)>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            JournalEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(file), Options); }
            catch (Exception ex) { AppPaths.Log("读取操作日志失败（" + file + "）: " + ex.Message); }
            result.Add((file, entry?.Plan is null ? null : entry));
        }
        return result;
    }
}

/// <summary>Outcome of undoing one interrupted run.</summary>
public sealed record RecoveryReport(string GameId, string RenderDir, bool RolledBack, IReadOnlyList<string> Lines)
{
    /// <summary>The deployment record the game should carry after recovery.</summary>
    public DeploymentInfo? Record { get; init; }

    /// <summary>Some step could not be undone (typically a folder that needs administrator rights).</summary>
    public bool Failed { get; init; }
}

/// <summary>
/// Undoes runs a crash interrupted, using their journals.
///
/// A deploy is rolled back: files it wrote are removed — but only while their bytes are still the ones it
/// was writing from the payload — and the originals it set aside are put back. The game keeps the record
/// it had before the run. A restore is finished instead of undone: everything it deletes was proven the
/// manager's when it was planned, so running the remaining steps again is safe and leaves the folder as
/// the user asked.
/// </summary>
public static class PlanRecovery
{
    public static IReadOnlyList<RecoveryReport> RecoverAll(string? directory = null)
    {
        var reports = new List<RecoveryReport>();
        foreach (var (path, entry) in FilePlanJournal.Pending(directory))
        {
            if (entry is null)
            {
                // Unreadable journal: keep it for inspection, do not guess.
                File.Move(path, path + ".unreadable", overwrite: true);
                continue;
            }

            // The journal folder is user-writable: replay only plans the planner could have produced.
            if (ElevatedProtocol.ValidateShape(entry.Plan) is { } invalid)
            {
                AppPaths.Log("[recovery] 忽略不合规的操作日志（" + path + "）: " + invalid);
                File.Move(path, path + ".rejected", overwrite: true);
                continue;
            }

            var report = entry.Plan.Kind == PlanKind.Deploy ? RollBackDeploy(entry) : FinishRestore(entry);
            foreach (var line in report.Lines) AppPaths.Log("[recovery] " + line);
            try
            {
                // A recovery that could not complete keeps its journal for inspection, under a name the
                // next start does not pick up again (it would fail the same way every time).
                if (report.Failed) File.Move(path, path + ".failed", overwrite: true);
                else File.Delete(path);
            }
            catch (Exception ex) { AppPaths.Log("处理操作日志失败: " + ex.Message); }
            reports.Add(report);
        }
        return reports;
    }

    private static RecoveryReport RollBackDeploy(JournalEntry entry)
    {
        var plan = entry.Plan;
        var lines = new List<string>();
        var failed = false;
        var sources = plan.Ops.OfType<WriteFileOp>().ToDictionary(w => w.RelativePath, w => w.SourcePath, StringComparer.OrdinalIgnoreCase);
        var texts = plan.Ops.OfType<WriteTextOp>().ToDictionary(w => w.RelativePath, w => w.Content, StringComparer.OrdinalIgnoreCase);

        foreach (var relative in entry.Writing.Distinct(StringComparer.OrdinalIgnoreCase).Reverse())
        {
            try
            {
                var path = PlanExecutor.Resolve(plan.RenderDir, relative);
                TryDelete(path + PlanExecutor.TempSuffix);
                if (!File.Exists(path)) continue;

                // Remove only what this run wrote: the bytes must equal the payload file or the text it was writing.
                var written = sources.TryGetValue(relative, out var source) && File.Exists(source) &&
                              DeploymentService.Sha256(source).Equals(DeploymentService.Sha256(path), StringComparison.OrdinalIgnoreCase)
                           || texts.TryGetValue(relative, out var text) &&
                              File.ReadAllText(path) == text;
                var previousHash = DeploymentService.RecordedHashFor(entry.Previous, relative)
                                   ?? (relative == ModSource.IniName ? entry.Previous?.IniSha256 : null);
                var wasThereBefore = previousHash is not null || entry.BackedUp.Any(b => Same(b.FileName, relative));

                // A file that existed before the run and was ours (a redeploy) has no backup — the
                // manager never backs up its own files — so it stays; the previous record no longer
                // matches it and the status check reports the game as modified until it is redeployed.
                if (written && !wasThereBefore)
                {
                    File.Delete(path);
                    lines.Add(Loc.T("Recovery.Removed", relative));
                }
            }
            catch (Exception ex)
            {
                failed = true;
                lines.Add(Loc.T("Recovery.StepFailed", relative, ex.Message));
            }
        }

        var unrestored = new List<BackupItem>();
        foreach (var backup in entry.BackedUp)
        {
            try
            {
                var path = PlanExecutor.Resolve(plan.RenderDir, backup.FileName);
                if (!File.Exists(backup.StoredPath)) { lines.Add(Loc.T("Restore.BackupLost", backup.FileName)); continue; }
                if (backup.Sha256.Length > 0 &&
                    !DeploymentService.Sha256(backup.StoredPath).Equals(backup.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(Loc.T("Restore.BackupChanged", backup.FileName));
                    unrestored.Add(backup);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(backup.StoredPath, path, overwrite: true);
                lines.Add(Loc.T("Restore.BackupRestored", backup.FileName));
            }
            catch (Exception ex)
            {
                failed = true;
                unrestored.Add(backup);
                lines.Add(Loc.T("Recovery.StepFailed", backup.FileName, ex.Message));
            }
        }

        lines.Insert(0, Loc.T("Recovery.DeployRolledBack", plan.RenderDir));
        return new RecoveryReport(plan.GameId, plan.RenderDir, true, lines)
        {
            Record = failed ? KeepTrackOf(entry, unrestored) : entry.Previous,
            Failed = failed,
        };
    }

    /// <summary>
    /// A rollback that could not finish (typically a folder needing administrator rights, after an
    /// elevated deploy was killed) must not forget what is still in the game folder: the previous record
    /// gains the originals that were set aside and not put back, and the files the run wrote that are
    /// still there. A later restore then removes ours and returns theirs.
    /// </summary>
    private static DeploymentInfo KeepTrackOf(JournalEntry entry, List<BackupItem> unrestored)
    {
        var record = entry.Previous is null
            ? new DeploymentInfo { ProxyName = entry.Plan.ProxyName }
            : System.Text.Json.JsonSerializer.Deserialize<DeploymentInfo>(System.Text.Json.JsonSerializer.Serialize(entry.Previous))!;

        record.AppliedConfiguration = "";
        foreach (var backup in unrestored.Where(b => record.Backups.All(x => !Same(x.FileName, b.FileName))))
            record.Backups.Add(backup);

        foreach (var relative in entry.Writing.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var path = PlanExecutor.Resolve(entry.Plan.RenderDir, relative);
                if (!File.Exists(path) || record.Files.Any(f => Same(f.FileName, relative))) continue;
                record.Files.Add(new DeployedFile { FileName = relative, Sha256 = DeploymentService.Sha256(path), Size = new FileInfo(path).Length });
            }
            catch
            {
                // Unreadable: the status check will report it.
            }
        }
        return record;
    }

    private static RecoveryReport FinishRestore(JournalEntry entry)
    {
        var lines = new List<string> { Loc.T("Recovery.RestoreFinished", entry.Plan.RenderDir) };
        var result = PlanExecutor.Execute(entry.Plan, entry.Previous);
        lines.AddRange(result.Result.Lines);
        return new RecoveryReport(entry.Plan.GameId, entry.Plan.RenderDir, false, lines)
        {
            Record = result.Result.Ok ? null : entry.Previous,
            Failed = !result.Result.Ok,
        };
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
