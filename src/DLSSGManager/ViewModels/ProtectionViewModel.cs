using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DLSSGManager.ViewModels;

/// <summary>
/// The anti-cheat side of the selected game — its warning banner and the deploy button's tooltip —
/// plus the wording of the notices raised when protected games are added or deployed to.
///
/// The scan result itself stays on <see cref="GameEntry.Protection"/>; this class decides how it is
/// presented, which used to live on the model as banner properties.
/// </summary>
public sealed partial class ProtectionViewModel : ObservableObject
{
    // Deployment assigns the scan result on a worker thread; the texts are refreshed on the UI thread.
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private GameEntry? _game;

    [ObservableProperty]
    private bool _showBanner;

    [ObservableProperty]
    private string _bannerTitle = "";

    [ObservableProperty]
    private string _bannerBody = "";

    [ObservableProperty]
    private string? _deployToolTip;

    /// <summary>Follows <paramref name="game"/>'s scan results until another game is selected.</summary>
    public void Track(GameEntry? game)
    {
        if (!ReferenceEquals(_game, game))
        {
            if (_game is not null) _game.PropertyChanged -= GameChanged;
            _game = game;
            if (_game is not null) _game.PropertyChanged += GameChanged;
        }

        Refresh();
    }

    /// <summary>Re-renders the texts; also called after a language switch.</summary>
    public void Refresh()
    {
        var report = _game?.Protection;

        // Only games the mod cannot work on get a banner. An "all clear" notice is noise, and any
        // wording next to a warning icon reads as bad news.
        ShowBanner = report?.HasKernelAntiCheat == true;
        BannerTitle = report is null ? "" : "⚠ " + report.Summary;
        BannerBody = ShowBanner ? Loc.T("Anti.BannerBody") : "";
        DeployToolTip = ShowBanner ? Loc.T("Deploy.BlockedTooltip") : null;
    }

    private void GameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GameEntry.Protection)) return;
        if (_dispatcher.CheckAccess()) Refresh();
        else _dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>The confirmation asked before deploying to a game with a kernel-mode anti-cheat.</summary>
    public static string OverridePrompt(GameEntry game) =>
        Loc.T("Anti.OverrideBody", game.Name, game.Protection?.Products ?? "", game.Protection?.Evidence ?? "");

    /// <summary>
    /// One notice for a batch of newly added games, so scanning a whole drive does not raise a prompt
    /// per protected title. Null when none of them is protected.
    /// </summary>
    public static (string Body, string LogLine)? BatchNotice(IReadOnlyCollection<GameEntry> added)
    {
        var blocked = added.Where(g => g.HasKernelAntiCheat).ToList();
        if (blocked.Count == 0) return null;

        var lines = string.Join("\n", blocked.Select(g => $"· {g.Name} — {g.Protection!.Products}"));
        return (Loc.T("Anti.BatchBody", blocked.Count, lines),
                Loc.T("Anti.BatchLog", blocked.Count, Loc.Join(blocked.Select(g => g.Name))));
    }
}
