using System.Windows;

namespace DLSSGManager;

public partial class MainWindow
{
    private readonly List<GameEntry> _failedBatch = new();
    private bool _lastBatchRestore;

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in GameList.Items.Cast<GameEntry>()) game.BatchSelected = true;
    }

    private void ClearBatchSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in _data.Games) game.BatchSelected = false;
    }

    private async void RetryBatch_Click(object sender, RoutedEventArgs e) =>
        await RunBatchAsync(_failedBatch.Where(_data.Games.Contains).ToList(), _lastBatchRestore);

    private async Task RunBatchAsync(List<GameEntry> targets, bool restore)
    {
        if (GameWorkRunning || targets.Count == 0) return;
        if (_saveFailure is not null && !SaveLibrary()) return;
        var body = Loc.T(restore ? "Batch.RestoreConfirm" : "Batch.DeployConfirm", targets.Count,
            string.Join("\n", targets.Select(t => "· " + t.Name)));
        if (MessageBox.Show(this, body, Loc.T(restore ? "Batch.RestoreTitle" : "Batch.DeployTitle"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _busy = true;
        _failedBatch.Clear();
        _lastBatchRestore = restore;
        BatchResultsBox.Clear();
        BatchResultsPanel.Visibility = Visibility.Visible;
        var completed = 0;
        var success = 0;
        try
        {
            foreach (var game in targets)
            {
                OpResult result;
                try
                {
                    var allowProtected = false;
                    if (!restore)
                    {
                        game.Protection = await Task.Run(() => AntiCheat.Scan(game.RenderDir));
                        if (game.HasKernelAntiCheat)
                            allowProtected = MessageBox.Show(this,
                                Loc.T("Anti.OverrideBody", game.Name, game.Protection.Products, game.Protection.Evidence),
                                Loc.T("Anti.OverrideTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                                == MessageBoxResult.Yes;
                    }
                    var source = CurrentSource(game);
                    var removeLogs = RemoveLogsCheck.IsChecked == true;
                    result = await Task.Run(() => restore ? DeploymentService.Restore(game, removeLogs)
                        : DeploymentService.Deploy(game, source, allowProtected));
                }
                catch (Exception ex) { result = new OpResult { Ok = false, Message = ex.Message }; }

                var saved = SaveLibrary();
                if (!saved) { result.Ok = false; result.Message += " · " + Loc.T("Manage.RecordUnsaved"); }
                if (result.Ok) success++;
                else _failedBatch.Add(game);
                var line = $"{(result.Ok ? "✓" : "✗")} {game.Name}: {result.Message}";
                BatchResultsBox.AppendText(line + Environment.NewLine);
                _log.Write(line);
                completed++;
                BatchStatusText.Text = Loc.T("Batch.Progress", completed, targets.Count);
                try { DeploymentService.Apply(game, await Task.Run(() => DeploymentService.Evaluate(game))); }
                catch (Exception ex) { _log.Write(ex.Message); }
                if (!saved)
                {
                    foreach (var remaining in targets.Skip(completed))
                    {
                        _failedBatch.Add(remaining);
                        BatchResultsBox.AppendText($"— {remaining.Name}: {Loc.T("Manage.BatchNotRun")}{Environment.NewLine}");
                    }
                    break;
                }
            }
            _log.Write(Loc.T("Batch.Result", Loc.T(restore ? "Batch.Restore" : "Batch.Deploy"), success, targets.Count));
        }
        finally
        {
            BatchStatusText.Text = "";
            _busy = false;
            UpdateStatusCard();
        }
    }

    private void HideBatchResults_Click(object sender, RoutedEventArgs e) => BatchResultsPanel.Visibility = Visibility.Collapsed;

    private void SelectUpdates_Click(object sender, RoutedEventArgs e)
    {
        foreach (var game in _data.Games)
        {
            var source = CurrentSource(game);
            game.BatchSelected = game.Deployment is not null && source.IsValid &&
                (source.Version != game.Deployment.ModVersion || source.RuntimeModel != game.Deployment.RuntimeModel);
        }
        GameSearchBox.Clear();
        GameStatusFilter.SelectedIndex = 0;
        UpdateManagementState();
    }
}
