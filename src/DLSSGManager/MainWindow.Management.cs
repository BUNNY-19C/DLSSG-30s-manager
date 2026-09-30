using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DLSSGManager;

public partial class MainWindow
{
    private readonly Dictionary<GameEntry, string> _savedConfigurations = new();
    private readonly HashSet<GameEntry> _observedGames = new();
    private string _savedLibrary = "";
    private bool _managementReady;
    private bool _closeWhenIdle;
    private bool _operationBusy;
    private int _pendingChecks;
    private string? _saveFailure;
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
            DownloadBuildCombo.IsEnabled = !value;
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
        DownloadBuildCombo.SelectedValue = BuildCatalog.Normalize(_data.DownloadBuild);
        ObserveGames();
        RememberSaved();
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
            _savedConfigurations.Remove(game);
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

    private void RememberSaved()
    {
        _savedLibrary = LibraryStore.Serialize(_data);
        foreach (var game in _data.Games) _savedConfigurations[game] = ConfigurationState.Saved(game);
    }

    private bool SaveLibrary()
    {
        var result = LibraryStore.Save(_data);
        _saveFailure = result.Ok ? null : result.Message;
        if (result.Ok) RememberSaved();
        else _log.Result(false, Loc.T("Manage.SaveFailed", result.Message));
        UpdateManagementState();
        return result.Ok;
    }

    private void SaveConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (SaveLibrary()) _log.Result(true, Loc.T("Manage.SavedMessage"));
    }

    private void UpdateManagementState()
    {
        if (!_managementReady) return;
        SaveFailureBanner.Visibility = _saveFailure is null ? Visibility.Collapsed : Visibility.Visible;
        SaveFailureText.Text = _saveFailure is null ? "" : Loc.T("Manage.SaveFailed", _saveFailure);
        var selectedCount = GameList.Items.Cast<GameEntry>().Count(g => g.BatchSelected);
        BatchSelectionText.Text = Loc.T("Manage.SelectedCount", selectedCount,
            _data.Games.Count(g => g.BatchSelected && !GameList.Items.Contains(g)));
        DeploySelectedButton.Content = Loc.T("Manage.DeploySelected", selectedCount);
        RestoreSelectedButton.Content = Loc.T("Manage.RestoreSelected", selectedCount);
        RetryBatchButton.IsEnabled = _failedBatch.Count > 0;
        var game = Selected;
        if (game is null) return;
        var unsaved = !_savedConfigurations.TryGetValue(game, out var saved) || saved != ConfigurationState.Saved(game);
        var pending = ConfigurationState.NeedsApply(game) || game.Status is GameStatus.Modified or GameStatus.Missing;
        ConfigurationStatusText.Text = Loc.T(unsaved ? "Manage.Unsaved" : pending ? "Manage.Pending" : "Manage.Applied");
        SaveConfigurationButton.IsEnabled = !_busy && (unsaved || _saveFailure is not null);
        DeployButton.Content = Loc.T(game.Deployment is null ? "Action.Deploy" : "Manage.Apply");
        BuildWarningText.Text = BuildCatalog.ConfigurationError(game.Profile) ?? "";
        BuildWarningText.Visibility = BuildWarningText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var source = CurrentSource(game);
        PayloadStatusText.Text = Loc.T("Manage.PayloadVersions", game.Profile.RuntimeModel,
            source.IsValid ? source.Version : Loc.T("Manage.NotDownloaded"),
            game.Deployment?.RuntimeModel is { Length: > 0 } build ? build : Loc.T("Common.Unknown"),
            game.Deployment?.ModVersion ?? "—");
        GuidanceText.Text = GameGuidance.Describe(game);
    }

    private void DownloadBuild_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_managementReady || DownloadBuildCombo.SelectedValue is not string build) return;
        _data.DownloadBuild = build;
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
        if (_saveFailure is not null)
        {
            if (!SaveLibrary()) e.Cancel = true;
            return;
        }
        if (_savedLibrary == LibraryStore.Serialize(_data)) return;
        var answer = MessageBox.Show(this, Loc.T("Manage.CloseUnsaved"), Loc.T("App.Name"),
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        e.Cancel = answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveLibrary());
    }
}
