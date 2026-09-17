using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DLSSGManager;

/// <summary>Deployment, restore and scan handlers.</summary>
/// <remarks>
/// Every operation here follows the same shape — set the busy flag, await the expensive work off
/// the UI thread, apply results back on it, and always release the flag in a finally. That shape
/// is deliberate: the synchronous originals froze the window (deployment enumerates every process,
/// hashes and trust-verifies a ~15 MB DLL, and scans the game folder), and the first async
/// conversion used ContinueWith chains whose t.Result access swallowed faults silently and could
/// leave the busy flag stuck — which locked every button until restart.
/// </remarks>
public partial class MainWindow
{
    // ---- single-game deployment --------------------------------------------

    private void Deploy_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (string.IsNullOrWhiteSpace(game.RenderDir) || !Directory.Exists(game.RenderDir))
        {
            _log.Write("✗ " + Loc.T("Deploy.NeedDir"));
            return;
        }

        RunDeploy(game);
    }

    /// <summary>
    /// Scans for anti-cheat off-thread, asks the user if needed, then deploys. The confirmation has
    /// to precede any file write, so the scan runs first and the dialog appears after it returns.
    /// </summary>
    private async void RunDeploy(GameEntry game)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Deploy.Starting", game.Name);

        try
        {
            var protection = await Task.Run(() => AntiCheat.Scan(game.RenderDir));
            game.Protection = protection;

            if (protection.HasKernelAntiCheat)
            {
                var body = Loc.T("Anti.OverrideBody", game.Name, protection.Products, protection.Evidence);

                var answer = MessageBox.Show(body, Loc.T("Anti.OverrideTitle"), MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    _log.Write(Loc.T("Anti.CancelLog", game.Name, protection.Summary, protection.Evidence));
                    return;
                }

                _log.Write(Loc.T("Anti.OverrideLog", game.Name, protection.Summary));
            }

            var source = CurrentSource();
            var result = await Task.Run(() => DeploymentService.Deploy(game, source,
                allowProtected: protection.HasKernelAntiCheat));

            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);

            LibraryStore.Save(_data);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("部署失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    /// <summary>
    /// Re-reads the game from disk and repaints its row. Without the re-check the list would keep
    /// showing the status from before the operation (Loc.T("Status.Missing") after a successful
    /// restore, etc.). The evaluation hashes and trust-verifies the deployed files, so it runs off
    /// the UI thread like every other status read; awaiting it keeps the busy flag held until the
    /// fresh state is applied, so a later evaluation cannot overwrite a newer one.
    /// </summary>
    private async Task FinishGameActionAsync(GameEntry game)
    {
        var check = await Task.Run(() => DeploymentService.Evaluate(game));
        DeploymentService.Apply(game, check);
        LibraryStore.Save(_data);
        UpdateStatusCard();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (game.Deployment is null)
        {
            var body = Loc.T("Restore.NoRecord", game.Name);
            if (MessageBox.Show(body, Loc.T("Restore.NoRecordTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        RunRestore(game);
    }

    private async void RunRestore(GameEntry game)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Restore.Starting", game.Name);

        try
        {
            var removeLogs = RemoveLogsCheck.IsChecked == true;
            var result = await Task.Run(() => DeploymentService.Restore(game, removeLogs));

            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("恢复失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    private async void Adopt_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var body = Loc.T("Adopt.Confirm");
        if (MessageBox.Show(body, Loc.T("Adopt.ConfirmTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        BatchStatusText.Text = Loc.T("Adopt.Starting", game.Name);

        try
        {
            // Adoption trust-verifies every candidate DLL in the game folder, so it belongs off the
            // UI thread like deploy.
            var result = await Task.Run(() => DeploymentService.Adopt(game));
            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
            await FinishGameActionAsync(game);
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + Loc.T("Adopt.Failed", ex.Message));
            AppPaths.Log("接管失败: " + ex);
        }
        finally
        {
            _busy = false;
            BatchStatusText.Text = "";
        }
    }

    // ---- batch --------------------------------------------------------------

    private async void DeployAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var targets = _data.Games
            .Where(g => !string.IsNullOrWhiteSpace(g.RenderDir) && Directory.Exists(g.RenderDir))
            .ToList();

        if (targets.Count == 0) { _log.Write(Loc.T("Batch.NothingToDeploy")); return; }

        var protectedGames = targets.Where(g => g.HasKernelAntiCheat).ToList();

        // Protected games stay in the list: the user asked for a batch, and the scan cannot know whether
        // this game's protection lets the chosen entry name survive. They are flagged as a risk and
        // deployed on the strength of this one confirmation.
        var body = protectedGames.Count == 0
            ? Loc.T("Batch.DeployConfirm", targets.Count,
                string.Join("\n", targets.Select(t => "· " + t.Name)))
            : Loc.T("Batch.DeployConfirmWithRisk",
                targets.Count,
                string.Join("\n", targets.Select(t => "· " + t.Name)),
                protectedGames.Count,
                string.Join("\n", protectedGames.Select(t => $"· {t.Name} — {t.Protection!.Products}")));

        if (MessageBox.Show(body, Loc.T("Batch.DeployTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _log.Write(Loc.T("Batch.DeployStart", targets.Count));

        _busy = true;
        var progress = UiProgress();

        try
        {
            var ok = await Task.Run(() =>
            {
                var count = 0;
                var source = CurrentSource();

                foreach (var game in targets)
                {
                    // The confirmation above covers the anti-cheat risk for every game in the list.
                    var result = DeploymentService.Deploy(game, source, allowProtected: game.HasKernelAntiCheat);
                    var mark = result.Ok ? "✓" : "✗";
                    var risk = game.HasKernelAntiCheat ? Loc.T("Batch.RiskMark") : "";
                    _log.Write($"  {mark}{risk} {game.Name}：{result.Message}");

                    if (result.Ok) count++;
                    progress.Report(Loc.T("Batch.Progress", count, targets.Count));
                }

                return count;
            });

            _log.Write(Loc.T("Batch.Result", Loc.T("Batch.Deploy"), ok, targets.Count));
            BatchStatusText.Text = Loc.T("Batch.LastDeploy", ok, targets.Count);
            LibraryStore.Save(_data);
            RefreshAllStatus();
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("批量部署失败: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var targets = _data.Games
            .Where(g => g.Deployment is not null && !string.IsNullOrWhiteSpace(g.RenderDir) && Directory.Exists(g.RenderDir))
            .ToList();

        if (targets.Count == 0) { _log.Write(Loc.T("Batch.NothingToRestore")); return; }

        var body = Loc.T("Batch.RestoreConfirm", targets.Count,
            string.Join("\n", targets.Select(t => "· " + t.Name)));
        if (MessageBox.Show(body, Loc.T("Batch.RestoreTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _log.Write(Loc.T("Batch.RestoreStart", targets.Count));

        _busy = true;
        var removeLogs = RemoveLogsCheck.IsChecked == true;
        var progress = UiProgress();

        try
        {
            var ok = await Task.Run(() =>
            {
                var count = 0;

                foreach (var game in targets)
                {
                    var result = DeploymentService.Restore(game, removeLogs);
                    var mark = result.Ok ? "✓" : "✗";
                    _log.Write($"  {mark} {game.Name}：{result.Message}");
                    if (result.Ok) count++;
                    progress.Report(Loc.T("Batch.Progress", count, targets.Count));
                }

                return count;
            });

            _log.Write(Loc.T("Batch.Result", Loc.T("Batch.Restore"), ok, targets.Count));
            BatchStatusText.Text = Loc.T("Batch.LastRestore", ok, targets.Count);
            LibraryStore.Save(_data);
            RefreshAllStatus();
        }
        catch (Exception ex)
        {
            _log.Write("✗ " + ex.Message);
            AppPaths.Log("批量恢复失败: " + ex);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---- scanning -----------------------------------------------------------

    private void ScanSteam_Click(object sender, RoutedEventArgs e)
    {
        var progress = UiProgress();
        StartScan(Loc.T("Scan.SteamLabel"), token => Detection.ScanSteam(progress, token));
    }

    private void ScanFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("Scan.FolderTitle") };
        if (!string.IsNullOrWhiteSpace(_data.LastScanRoot) && Directory.Exists(_data.LastScanRoot))
            dialog.InitialDirectory = _data.LastScanRoot;

        if (dialog.ShowDialog() != true) return;

        var root = dialog.FolderName;
        _data.LastScanRoot = root;
        LibraryStore.Save(_data);

        var progress = UiProgress();
        StartScan(root, token => Detection.ScanFolder(root, progress, token));
    }

    /// <summary>
    /// Progress sink for the scan workers. Must be created on the UI thread: Progress&lt;T&gt; captures
    /// the SynchronizationContext it is constructed on, so building it inside a background lambda
    /// would run the callback on a pool thread and touching a control there kills the process.
    /// The dispatcher check keeps the sink correct even if that ever changes.
    /// </summary>
    private IProgress<string> UiProgress() => new Progress<string>(text =>
    {
        if (Dispatcher.CheckAccess()) BatchStatusText.Text = text;
        else Dispatcher.Invoke(() => BatchStatusText.Text = text);
    });

    private async void StartScan(string label, Func<CancellationToken, List<GameCandidate>> scan)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        _scanCts = new CancellationTokenSource();
        _log.Write(Loc.T("Scan.Start", label));

        var token = _scanCts.Token;
        List<GameCandidate>? found = null;

        try
        {
            found = await Task.Run(() =>
            {
                try { return scan(token); }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    AppPaths.Log("扫描失败: " + ex);
                    return null;
                }
            });
        }
        finally
        {
            _busy = false;
            _scanCts?.Dispose();
            _scanCts = null;
            BatchStatusText.Text = "";
        }

        if (found is null) { _log.Write(Loc.T("Scan.Cancelled")); return; }
        MergeCandidates(found);
    }

    /// <summary>
    /// Folds scan results into the library and refreshes the affected rows. The per-game evaluation
    /// (hashing, trust verification) runs off the UI thread; the anti-cheat summary prompt waits for
    /// those results because it reads the protection they carry.
    /// </summary>
    private async void MergeCandidates(List<GameCandidate> found)
    {
        var added = 0;
        var touched = new List<GameEntry>();
        var newlyAdded = new List<GameEntry>();

        foreach (var candidate in found)
        {
            var existing = _data.Games.FirstOrDefault(g =>
                string.Equals(g.RenderDir.TrimEnd('\\'), candidate.RenderDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(candidate.ExePath)) existing.ExePath = candidate.ExePath;
                touched.Add(existing);
                continue;
            }

            var game = new GameEntry
            {
                Name = candidate.Name,
                RenderDir = candidate.RenderDir,
                ExePath = candidate.ExePath,
                PreferredProxy = DeploymentService.AutoProxy,
                Notes = candidate.Source,
                Profile = new GameProfile { Router = _data.RecommendedRouter },
            };

            _data.Games.Add(game);
            touched.Add(game);
            newlyAdded.Add(game);
            added++;
        }

        _log.Write(Loc.T("Scan.Done", found.Count, added));

        // Land on something useful instead of leaving the detail pane empty after a scan.
        if (GameList.SelectedItem is null && _data.Games.Count > 0)
            GameList.SelectedIndex = 0;

        var games = touched;
        try
        {
            var results = await Task.Run(() =>
                games.Select(g => (Game: g, Check: DeploymentService.Evaluate(g))).ToList());

            foreach (var (game, check) in results) DeploymentService.Apply(game, check);

            LibraryStore.Save(_data);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            _log.Write(Loc.T("Status.RefreshFailed", ex.Message));
            AppPaths.Log("扫描后状态检查失败: " + ex);
        }

        // One summary prompt for the whole scan rather than a dialog per protected title.
        WarnAboutProtected(newlyAdded);
    }

    // ---- path pickers -------------------------------------------------------

    private void BrowseRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var dialog = new OpenFolderDialog { Title = Loc.T("Detail.BrowseDirTitle") };
        if (!string.IsNullOrWhiteSpace(game.RenderDir) && Directory.Exists(game.RenderDir))
            dialog.InitialDirectory = game.RenderDir;

        if (dialog.ShowDialog() != true) return;

        // AttachFolder resolves the render directory and raises the anti-cheat warning.
        AttachFolder(game, dialog.FolderName);
    }

    private void DetectRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var root = game.RenderDir;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            _log.Write(Loc.T("Detail.DetectNeedFolder"));
            return;
        }

        _log.Write(Loc.T("Detail.DetectSearching", root));
        var hit = Detection.FindRenderTarget(root);
        if (hit is null)
        {
            _log.Write(Loc.T("Detail.DetectNoMarker"));
            return;
        }

        _log.Write(Loc.T("Detail.LocatedMessage", hit.RenderDir));
        AttachFolder(game, root);
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var dialog = new OpenFileDialog { Title = Loc.T("Detail.BrowseExeTitle"), Filter = Loc.T("Detail.ExeFilter") };
        if (!string.IsNullOrWhiteSpace(game.ExePath) && File.Exists(game.ExePath))
            dialog.InitialDirectory = Path.GetDirectoryName(game.ExePath);

        if (dialog.ShowDialog() != true) return;

        game.ExePath = dialog.FileName;
        LibraryStore.Save(_data);
    }

    // ---- open / launch ------------------------------------------------------

    private void OpenRenderDir_Click(object sender, RoutedEventArgs e)
    {
        var dir = Selected?.RenderDir;
        if (!Shell.OpenFolder(dir)) _log.Write(Loc.T("Error.DirOpenFailed"));
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        if (Shell.LaunchExecutable(game.ExePath))
            _log.Write(Loc.T("Error.Launched", Path.GetFileName(game.ExePath)));
        else
            _log.Write(Loc.T("Error.LaunchFailed"));
    }

    private void OpenModLog_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var latest = DeploymentService.LatestLogFile(game.RenderDir);
        if (latest is not null)
        {
            Shell.OpenDocument(latest);
            _log.Write(Loc.T("Error.LogOpened", Path.GetFileName(latest)));
            return;
        }

        var logsDir = Path.Combine(game.RenderDir, ModSource.LogDirName, "logs");
        if (Directory.Exists(logsDir))
        {
            Shell.OpenFolder(logsDir);
            return;
        }

        _log.Write(Loc.T("Error.NoModLog"));
    }

    private void OpenDataDir_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureCreated();
        if (!Shell.OpenFolder(AppPaths.Root)) _log.Write(Loc.T("Error.CannotOpenDataDir", AppPaths.Root));
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        if (Native.IsElevated()) return;

        if (Shell.RelaunchElevated())
            Application.Current.Shutdown();
        else
            _log.Write(Loc.T("Error.ElevationCancelled"));
    }
}
