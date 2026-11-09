using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace DLSSGManager;

/// <summary>
/// Window lifecycle around the library: watching games for edits, saving, and the close guard. What
/// the panel shows about saved and applied state comes from <see cref="Management"/>.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<GameEntry> _observedGames = new();
    private bool _managementReady;
    private bool _closeWhenIdle;
    private bool _operationBusy;
    private int _pendingChecks;
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool GameWorkRunning => _busy || _statusRefreshRunning || _pendingChecks > 0;

    private bool _busy
    {
        get => _operationBusy;
        set
        {
            _operationBusy = value;
            if (!_managementReady) return;
            // Keep the log, download controls and window-close action available.
            DetailPanel.IsEnabled = !value;
            GameList.IsEnabled = !value;
            GameSearchBox.IsEnabled = !value;
            GameStatusFilter.IsEnabled = !value;
            LanguageCombo.IsEnabled = !value;
            ThemeCombo.IsEnabled = !value;
            ToolsPanel.IsEnabled = !value;
            BatchControls.IsEnabled = !value;
            UpdateManagementState();
        }
    }

    private void InitializeManagement()
    {
        _managementReady = true;
        ObserveGames();
        Management.RememberSaved(_data);
        _data.Games.CollectionChanged += GamesChanged;
        Closing += WindowClosing;
        _closeTimer.Tick += (_, _) =>
        {
            if (_busy || _statusRefreshRunning || _pendingChecks > 0) return;
            _closeTimer.Stop();
            Close();
        };
        Closed += (_, _) => _closeTimer.Stop();
        UpdateManagementState();
    }

    private void GamesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveGames();
        UpdateManagementState();
    }

    private void ObserveGames()
    {
        foreach (var game in _observedGames.Where(g => !_data.Games.Contains(g)).ToArray())
        {
            game.PropertyChanged -= GameEdited;
            game.Profile.PropertyChanged -= ProfileEdited;
            _observedGames.Remove(game);
            Management.Forget(game);
        }
        foreach (var game in _data.Games)
            if (_observedGames.Add(game))
            {
                game.PropertyChanged += GameEdited;
                game.Profile.PropertyChanged += ProfileEdited;
            }
    }

    private void GameEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(UpdateManagementState); return; }
        UpdateManagementState();
    }

    private void ProfileEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameProfile.RuntimeModel) && Dispatcher.CheckAccess())
        {
            RefreshModSource();
            BuildProxyCombo();
        }
        GameEdited(sender, e);
    }

    private LibraryStore.Writer? BeginLibraryWrite(bool saveEdits = false)
    {
        LibraryStore.Writer writer;
        try { writer = LibraryStore.BeginWrite(_data); }
        catch (Exception ex)
        {
            ReportLibrarySaveFailure(ex.Message);
            return null;
        }

        if (!saveEdits || !Management.HasUnsavedChanges(_data) && Management.SaveFailure is null)
            return writer;
        if (SaveLibrary(writer)) return writer;
        writer.Dispose();
        return null;
    }

    private void ReportLibrarySaveFailure(string message)
    {
        Management.SaveFailure = message;
        _log.Result(false, Loc.T("Manage.SaveFailed", message));
        UpdateManagementState();
    }

    private bool SaveLibrary(LibraryStore.Writer? writer = null)
    {
        var result = writer is null ? LibraryStore.Save(_data) : writer.Save();
        if (!result.Ok)
        {
            ReportLibrarySaveFailure(result.Message);
            return false;
        }
        Management.SaveFailure = null;
        Management.RememberSaved(_data);
        UpdateManagementState();
        return true;
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SaveLibrary()) _log.Result(true, Loc.T("Manage.SavedMessage"));
    }

    private void UpdateManagementState()
    {
        if (!_managementReady) return;
        Batch.UpdateSelection();
        if (Selected is { } game) Management.Refresh(game, CurrentSource(game), _busy);
    }

    private void UpdateStatusBar()
    {
        var total = _data.Games.Count;
        var visible = _gamesView?.Cast<GameEntry>().Count() ?? 0;
        var selected = Batch.SelectedCount;
        var deployed = _data.Games.Count(g => g.Deployment is not null);

        var parts = new List<string>();
        
        if (total == 0)
        {
            parts.Add(Loc.T("Status.NoGames"));
        }
        else
        {
            if (visible < total)
                parts.Add(Loc.T("Status.Filtered", visible, total));
            else
                parts.Add(Loc.T("Status.Total", total));

            parts.Add(Loc.T("Status.Deployed", deployed));
            
            if (selected > 0)
                parts.Add(Loc.T("Status.Selected", selected));
        }

        StatusBarText.Text = string.Join("  •  ", parts);
        
        // Right side: batch operation progress (if running)
        StatusBarRight.Text = Batch.IsRunning 
            ? Loc.T("Status.BatchProgress", Batch.CompletedCount, Batch.SelectedCount)
            : "";
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_busy || _statusRefreshRunning || _pendingChecks > 0)
        {
            e.Cancel = true;
            if (!_closeWhenIdle && MessageBox.Show(this, Loc.T(_fetchCts is null ? "Manage.CloseWait" : "Manage.CloseDownload"),
                Loc.T("App.Name"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _closeWhenIdle = true;
                _fetchCts?.Cancel();
                _fetchGate?.Resume();
                _scanCts?.Cancel();
                _closeTimer.Start();
            }
            return;
        }
        _closeWhenIdle = false;
        if (!Management.HasUnsavedChanges(_data)) return;
        var answer = MessageBox.Show(this, Loc.T("Manage.CloseUnsaved"), Loc.T("App.Name"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        e.Cancel = answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveLibrary());
    }
}
