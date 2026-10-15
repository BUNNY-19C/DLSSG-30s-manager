using System.Windows;
using DLSSGManager.ViewModels;

namespace DLSSGManager;

/// <summary>
/// The window's side of batch operations: the dialogs, busy flag and status line that
/// <see cref="BatchViewModel"/> asks for. The batch logic itself lives in the view model.
/// </summary>
public partial class MainWindow : IBatchHost
{
    bool IBatchHost.IsWorkRunning => GameWorkRunning;

    void IBatchHost.SetBusy(bool busy) => _busy = busy;

    bool IBatchHost.EnsureSaved() => Management.SaveFailure is null || SaveLibrary();

    bool IBatchHost.SaveLibrary() => SaveLibrary();

    bool IBatchHost.Confirm(string title, string body) =>
        MessageBox.Show(this, body, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    bool IBatchHost.ConfirmProtectedDeploy(GameEntry game) =>
        MessageBox.Show(this, ProtectionViewModel.OverridePrompt(game), Loc.T("Anti.OverrideTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    ModSource IBatchHost.SourceFor(GameEntry game) => CurrentSource(game);

    bool IBatchHost.RemoveLogs => RemoveLogsCheck.IsChecked == true;

    void IBatchHost.ApplyCheck(GameEntry game, DeploymentService.GameCheck check) => ApplyGameCheck(game, check);

    void IBatchHost.Log(string line) => _log.Write(line);

    void IBatchHost.ShowProgress(string text) => BatchStatusText.Text = text;

    void IBatchHost.BatchFinished() => UpdateStatusCard();

    void IBatchHost.ClearFilters()
    {
        GameSearchBox.Clear();
        GameStatusFilter.SelectedIndex = 0;
    }
}
