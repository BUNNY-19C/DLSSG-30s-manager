using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DLSSGManager;

public partial class App : Application
{
    /// <summary>Downloads the mod files and exits, without showing the window.</summary>
    private const string FetchSwitch = "--fetch";

    /// <summary>Suppresses dialogs and writes progress to the manager log instead.</summary>
    private const string SilentSwitch = "--silent";

    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.EnsureCreated();

        // The installer runs the program with --fetch so a fresh install can arrive with its mod
        // files already in place. Returning before base.OnStartup keeps StartupUri from opening the
        // window, which would otherwise appear behind the installer.
        if (HasSwitch(e.Args, FetchSwitch))
        {
            Shutdown(RunFetch(HasSwitch(e.Args, SilentSwitch)));
            return;
        }

        AppPaths.Log($"===== 启动 DLSSG 30 系管理器 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");

        // A stray exception in a click handler should surface, not silently kill the window.
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppPaths.Log("未处理异常: " + args.ExceptionObject);

        // A fault in a background task that nobody awaits would vanish without a trace — log it,
        // and mark it observed so it cannot escalate into a process crash at finalisation.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppPaths.Log("未观察任务异常: " + args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static bool HasSwitch(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fetches the mod files, returning a process exit code so the installer can tell success from
    /// failure. Progress goes to the log because a silent run has no console to write to.
    /// </summary>
    private static int RunFetch(bool silent)
    {
        var target = ModSourceLocator.ResolveTarget(null);
        AppPaths.Log($"[fetch] 目标目录: {target}");

        try
        {
            var progress = new Progress<string>(text => AppPaths.Log("[fetch] " + text));

            // Same probe the UI paths run, so the version marker written beside the payload names
            // the release actually fetched — without it the badge keeps showing the old number.
            var detected = ModFetcher.DetectLatestVersionAsync(CancellationToken.None)
                .GetAwaiter().GetResult();
            AppPaths.Log("[fetch] 探测上游版本: " + (detected ?? "(未知)"));

            var result = ModFetcher.DownloadIntoAsync(target, progress, CancellationToken.None,
                                                      versionLabel: detected)
                .GetAwaiter().GetResult();

            foreach (var line in result.Lines) AppPaths.Log("[fetch] " + line);

            if (result.Ok)
            {
                AppPaths.Log($"[fetch] 成功: {result.Message}");
                return 0;
            }

            AppPaths.Log($"[fetch] 失败: {result.Message}");
            if (!silent)
                MessageBox.Show(Loc.T("Error.FetchFailed", result.Message),
                    Loc.T("Error.UnhandledTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);

            return 1;
        }
        catch (Exception ex)
        {
            AppPaths.Log("[fetch] 异常: " + ex);
            if (!silent)
                MessageBox.Show(Loc.T("Error.FetchException", ex.Message),
                    Loc.T("Error.UnhandledTitle"), MessageBoxButton.OK, MessageBoxImage.Error);

            return 2;
        }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppPaths.Log("界面异常: " + e.Exception);
        MessageBox.Show(
            Loc.T("Error.Unhandled", e.Exception.Message),
            Loc.T("Error.UnhandledTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
