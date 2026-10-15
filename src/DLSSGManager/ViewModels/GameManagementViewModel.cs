using CommunityToolkit.Mvvm.ComponentModel;

namespace DLSSGManager.ViewModels;

/// <summary>
/// The saving and applying state of the selected game: unsaved edits, configuration still to be
/// applied, the payload versions involved, and whether saving the library itself is failing.
///
/// Also the record of what was last saved, which is what lets closing the window tell whether
/// anything would be lost. The view feeds it the selection and the payload source; it touches no
/// control.
/// </summary>
public sealed partial class GameManagementViewModel : ObservableObject
{
    private readonly Dictionary<GameEntry, string> _saved = new();
    private string _savedLibrary = "";

    [ObservableProperty]
    private string _configurationStatus = "";

    [ObservableProperty]
    private bool _canSave;

    [ObservableProperty]
    private string _applyButtonText = Loc.T("Action.Deploy");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBuildWarning))]
    private string _buildWarning = "";

    [ObservableProperty]
    private string _payloadStatus = "";

    [ObservableProperty]
    private string _guidance = "";

    /// <summary>The message of the last failed save, or null while saving works.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveFailure))]
    [NotifyPropertyChangedFor(nameof(SaveFailureText))]
    private string? _saveFailure;

    public bool HasBuildWarning => BuildWarning.Length > 0;

    public bool HasSaveFailure => SaveFailure is not null;

    public string SaveFailureText => SaveFailure is null ? "" : Loc.T("Manage.SaveFailed", SaveFailure);

    /// <summary>Takes the library as it is now as the saved state.</summary>
    public void RememberSaved(AppData data)
    {
        _savedLibrary = LibraryStore.Serialize(data);
        _saved.Clear();
        foreach (var game in data.Games) _saved[game] = ConfigurationState.Saved(game);
    }

    /// <summary>Drops a game that left the library, so its snapshot is not kept alive.</summary>
    public void Forget(GameEntry game) => _saved.Remove(game);

    public bool HasUnsavedChanges(AppData data) => _savedLibrary != LibraryStore.Serialize(data);

    /// <summary>
    /// Recomputes the panel for <paramref name="game"/>; <paramref name="source"/> is the payload a
    /// deployment of it would use.
    /// </summary>
    public void Refresh(GameEntry game, ModSource source, bool busy)
    {
        var unsaved = !_saved.TryGetValue(game, out var saved) || saved != ConfigurationState.Saved(game);
        var pending = ConfigurationState.NeedsApply(game) || game.Status is GameStatus.Modified or GameStatus.Missing;

        ConfigurationStatus = Loc.T(unsaved ? "Manage.Unsaved" : pending ? "Manage.Pending" : "Manage.Applied");
        CanSave = !busy && (unsaved || SaveFailure is not null);
        ApplyButtonText = Loc.T(game.Deployment is null ? "Action.Deploy" : "Manage.Apply");
        BuildWarning = source.ConfigurationError(game.Profile) ?? "";
        PayloadStatus = Loc.T("Manage.PayloadVersions", game.Profile.RuntimeModel,
            source.IsValid ? source.Version : Loc.T("Manage.NotDownloaded"),
            game.Deployment?.RuntimeModel is { Length: > 0 } build ? build : Loc.T("Common.Unknown"),
            game.Deployment?.ModVersion ?? "—");
        Guidance = GameGuidance.Describe(game);
    }

    /// <summary>Re-renders the text that does not depend on a selected game.</summary>
    public void RefreshText() => OnPropertyChanged(nameof(SaveFailureText));
}
