using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DLSSGManager.ViewModels;

/// <summary>
/// What a batch needs from the window: its dialogs, the shared busy flag, persistence, status
/// checks and the log. Kept to operations so the batch logic never reaches for a control.
/// </summary>
public interface IBatchHost
{
    /// <summary>True while any game operation or status check is running.</summary>
    bool IsWorkRunning { get; }

    void SetBusy(bool busy);

    /// <summary>Retries a previously failed save; false when the library still cannot be written.</summary>
    bool EnsureSaved();

    bool SaveLibrary();

    bool Confirm(string title, string body);

    /// <summary>Asks whether to deploy to a game whose kernel-mode anti-cheat was just detected.</summary>
    bool ConfirmProtectedDeploy(GameEntry game);

    ModSource SourceFor(GameEntry game);

    bool RemoveLogs { get; }

    void ApplyCheck(GameEntry game, DeploymentService.GameCheck check);

    void Log(string line);

    /// <summary>The one-line status in the log strip; empty clears it.</summary>
    void ShowProgress(string text);

    /// <summary>Called once a batch is over, to repaint the selected game.</summary>
    void BatchFinished();

    /// <summary>Clears the list's search and status filter, so every game is visible.</summary>
    void ClearFilters();
}

/// <summary>
/// Batch deployment and restore over the games ticked in the list, one at a time, with a result line
/// per game and a retry of the ones that failed.
///
/// A batch only covers ticked games that the list currently shows — a filter hiding a ticked game
/// excludes it, and the selection text says how many that leaves out.
/// </summary>
public sealed partial class BatchViewModel : ObservableObject
{
    private readonly IBatchHost _host;
    private readonly AppData _data;
    private readonly Func<IEnumerable<GameEntry>> _visibleGames;
    private readonly List<GameEntry> _failed = new();
    private bool _lastWasRestore;

    [ObservableProperty]
    private string _selectionText = "";

    [ObservableProperty]
    private string _deployText = "";

    [ObservableProperty]
    private string _restoreText = "";

    [ObservableProperty]
    private bool _resultsVisible;

    [ObservableProperty]
    private string _results = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryFailedCommand))]
    private bool _hasFailures;

    public BatchViewModel(IBatchHost host, AppData data, Func<IEnumerable<GameEntry>> visibleGames)
    {
        _host = host;
        _data = data;
        _visibleGames = visibleGames;
    }

    /// <summary>Recounts the selection. Also re-renders the texts after a language switch.</summary>
    public void UpdateSelection()
    {
        var visible = _visibleGames().ToHashSet();
        var selected = visible.Count(g => g.BatchSelected);

        SelectionText = Loc.T("Manage.SelectedCount", selected, _data.Games.Count(g => g.BatchSelected && !visible.Contains(g)));
        DeployText = Loc.T("Manage.DeploySelected", selected);
        RestoreText = Loc.T("Manage.RestoreSelected", selected);
    }

    [RelayCommand]
    private void SelectVisible()
    {
        foreach (var game in _visibleGames()) game.BatchSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var game in _data.Games) game.BatchSelected = false;
    }

    /// <summary>Ticks every deployed game whose installed payload differs from the local one.</summary>
    [RelayCommand]
    private void SelectUpdates()
    {
        foreach (var game in _data.Games)
        {
            var source = _host.SourceFor(game);
            game.BatchSelected = game.Deployment is not null && source.IsValid &&
                (source.Version != game.Deployment.ModVersion || source.RuntimeModel != game.Deployment.RuntimeModel);
        }

        _host.ClearFilters();
        UpdateSelection();
    }

    [RelayCommand]
    private Task DeploySelected() => RunAsync(TickedVisible(), restore: false);

    [RelayCommand]
    private Task RestoreSelected() => RunAsync(TickedVisible(), restore: true);

    [RelayCommand(CanExecute = nameof(HasFailures))]
    private Task RetryFailed() => RunAsync(_failed.Where(_data.Games.Contains).ToList(), _lastWasRestore);

    [RelayCommand]
    private void HideResults() => ResultsVisible = false;

    private List<GameEntry> TickedVisible() => _visibleGames().Where(g => g.BatchSelected).ToList();

    private async Task RunAsync(List<GameEntry> targets, bool restore)
    {
        if (_host.IsWorkRunning || targets.Count == 0) return;
        if (!_host.EnsureSaved()) return;

        var body = Loc.T(restore ? "Batch.RestoreConfirm" : "Batch.DeployConfirm", targets.Count,
            string.Join("\n", targets.Select(t => "· " + t.Name)));
        if (!_host.Confirm(Loc.T(restore ? "Batch.RestoreTitle" : "Batch.DeployTitle"), body)) return;

        _host.SetBusy(true);
        _failed.Clear();
        HasFailures = false;
        _lastWasRestore = restore;
        Results = "";
        ResultsVisible = true;

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
                        if (game.HasKernelAntiCheat) allowProtected = _host.ConfirmProtectedDeploy(game);
                    }

                    var source = _host.SourceFor(game);
                    var removeLogs = _host.RemoveLogs;
                    result = await Task.Run(() => restore ? DeploymentService.Restore(game, removeLogs)
                        : DeploymentService.Deploy(game, source, allowProtected));
                }
                catch (Exception ex)
                {
                    result = new OpResult { Ok = false, Message = ex.Message };
                }

                // Each game's record is written before the next one starts: a batch that dies part-way
                // must not leave deployed files the library does not know about.
                var saved = _host.SaveLibrary();
                if (!saved)
                {
                    result.Ok = false;
                    result.Message += " · " + Loc.T("Manage.RecordUnsaved");
                }

                if (result.Ok) success++;
                else _failed.Add(game);

                var line = $"{(result.Ok ? "✓" : "✗")} {game.Name}: {result.Message}";
                Results += line + Environment.NewLine;
                _host.Log(line);
                completed++;
                _host.ShowProgress(Loc.T("Batch.Progress", completed, targets.Count));

                try
                {
                    _host.ApplyCheck(game, await Task.Run(() => DeploymentService.Evaluate(game)));
                    saved = _host.SaveLibrary() && saved;
                }
                catch (Exception ex)
                {
                    _host.Log(ex.Message);
                }

                if (!saved)
                {
                    foreach (var remaining in targets.Skip(completed))
                    {
                        _failed.Add(remaining);
                        Results += $"— {remaining.Name}: {Loc.T("Manage.BatchNotRun")}{Environment.NewLine}";
                    }
                    break;
                }
            }

            _host.Log(Loc.T("Batch.Result", Loc.T(restore ? "Batch.Restore" : "Batch.Deploy"), success, targets.Count));
        }
        finally
        {
            HasFailures = _failed.Count > 0;
            _host.ShowProgress("");
            _host.SetBusy(false);
            _host.BatchFinished();
        }
    }
}
