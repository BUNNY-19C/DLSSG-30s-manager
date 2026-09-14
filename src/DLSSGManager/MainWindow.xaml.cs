using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DLSSGManager;

public partial class MainWindow : Window
{
    private readonly AppData _data;
    private OutputLog _log = null!;
    private CancellationTokenSource? _scanCts;
    private bool _busy;

    /// <summary>Keeps overlapping status refreshes from racing over the same game properties.</summary>
    private bool _statusRefreshRunning;

    /// <summary>Prevents the language handler firing while the picker is being populated.</summary>
    private bool _suppressLanguageChange;

    /// <summary>Same, for the theme picker.</summary>
    private bool _suppressThemeChange;

    /// <summary>
    /// Set while the entry-name picker is being rebuilt: replacing the items raises SelectionChanged
    /// with a value the user never chose, which would otherwise overwrite the game's preference.
    /// </summary>
    private bool _suppressProxyChange;

    /// <summary>
    /// Whether the mod file source is usable, or null when nothing has been fetched yet. Kept so the
    /// badge colours can be re-applied after a theme switch, since they are set from code.
    /// </summary>
    private bool? _modSourceValid;

    /// <summary>
    /// Last GPU probe result, kept so the advice line can be re-rendered after a language change.
    /// The text is produced from code, so it does not follow the XAML bindings.
    /// </summary>
    private GpuInfo? _gpuInfo;

    public MainWindow()
    {
        // Loaded once and reused: the language has to be applied before the first XAML string is
        // resolved (otherwise the window renders in the default language and switches a moment later),
        // and the rest of the setup needs the same data.
        _data = LibraryStore.Load();

        Loc.SetLanguage(_data.InterfaceLanguage);
        Theme.Apply(_data.InterfaceTheme);

        InitializeComponent();

        AppPaths.EnsureCreated();
        _log = new OutputLog(OutputBox);

        BuildLocalizedCombos();
        BuildLanguageCombo();
        BuildThemeCombo();
        BuildProxyCombo();

        // Bound straight to the persisted collection: no copy can drift out of sync with the file.
        GameList.ItemsSource = _data.Games;
        if (_data.Games.Count > 0) GameList.SelectedIndex = 0;

        Loaded += OnLoaded;
    }

    private GameEntry? Selected => GameList.SelectedItem as GameEntry;

    /// <summary>
    /// Fills the numeric dropdowns from the string table. Called again after a language change, since
    /// these items carry display text rather than a binding.
    /// </summary>
    private void BuildLocalizedCombos()
    {
        var frames = FrameCombo.SelectedValue;
        FrameCombo.ItemsSource = new[]
        {
            new Choice(1, Loc.T("Detail.Frames2X")),
            new Choice(2, Loc.T("Detail.Frames3X")),
            new Choice(3, Loc.T("Detail.Frames4X")),
            new Choice(4, Loc.T("Detail.Frames5X")),
            new Choice(5, Loc.T("Detail.Frames6X")),
        };
        if (frames is not null) FrameCombo.SelectedValue = frames;

        var level = LogCombo.SelectedValue;
        LogCombo.ItemsSource = new[]
        {
            new Choice(0, Loc.T("Detail.Log0")),
            new Choice(1, Loc.T("Detail.Log1")),
            new Choice(2, Loc.T("Detail.Log2")),
            new Choice(3, Loc.T("Detail.Log3")),
        };
        if (level is not null) LogCombo.SelectedValue = level;
    }

    /// <summary>Fills the language picker without triggering the change handler.</summary>
    private void BuildLanguageCombo()
    {
        _suppressLanguageChange = true;
        LanguageCombo.ItemsSource = Languages.All
            .Select(code => new TextChoice(code, Languages.DisplayName(code)))
            .ToList();
        LanguageCombo.SelectedValuePath = "Value";
        LanguageCombo.DisplayMemberPath = "Text";
        LanguageCombo.SelectedValue = Loc.Current;
        _suppressLanguageChange = false;
    }

    /// <summary>Fills the theme picker without triggering the change handler.</summary>
    private void BuildThemeCombo()
    {
        _suppressThemeChange = true;
        ThemeCombo.ItemsSource = new[]
        {
            new TextChoice(ThemeKeys.StorageValue(AppTheme.Dark), Loc.T("Theme.Dark")),
            new TextChoice(ThemeKeys.StorageValue(AppTheme.Light), Loc.T("Theme.Light")),
        };
        ThemeCombo.SelectedValuePath = "Value";
        ThemeCombo.DisplayMemberPath = "Text";
        ThemeCombo.SelectedValue = ThemeKeys.StorageValue(Theme.Current);
        _suppressThemeChange = false;
    }

    /// <summary>
    /// Fills the entry-name picker from the current mod source: the project's own five, then any proxy
    /// DLLs the user has added.
    ///
    /// Rebuilt rather than declared in XAML because the list changes when a file is added, and because
    /// an imported entry carries a suffix that has to follow the interface language. The value written
    /// back for a pick is the entry name itself — never the label, which is translated.
    /// </summary>
    private void BuildProxyCombo()
    {
        var wanted = Selected?.PreferredProxy ?? ProxyCombo.SelectedValue as string;

        _suppressProxyChange = true;
        try
        {
            var found = ModSourceLocator.FindExisting(_data.ModSourcePath);
            var imported = found is null ? Array.Empty<string>() : new ModSource(found).ImportedProxies.ToArray();

            ProxyCombo.Items.Clear();
            ProxyCombo.Items.Add(new ComboBoxItem { Content = Loc.T("Common.Auto"), Tag = DeploymentService.AutoProxy });

            foreach (var name in ModSource.ProxyCandidates.Concat(imported))
            {
                var label = ModSource.ProxyCandidates.Contains(name, StringComparer.OrdinalIgnoreCase)
                    ? name
                    : name + Loc.T("Proxy.ImportedSuffix");

                ProxyCombo.Items.Add(new ComboBoxItem { Content = label, Tag = name });
            }

            // A preference this source can no longer provide (its DLL was deleted from the mod folder)
            // leaves the picker empty; the deploy path reports it rather than silently choosing another.
            if (wanted is not null) ProxyCombo.SelectedValue = wanted;
        }
        finally
        {
            _suppressProxyChange = false;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeChange) return;
        if (ThemeCombo.SelectedItem is not TextChoice choice) return;

        var wanted = ThemeKeys.Parse(choice.Value);
        if (wanted == Theme.Current) return;

        Theme.Apply(wanted);
        ColorFromCode();

        _data.InterfaceTheme = ThemeKeys.StorageValue(wanted);
        LibraryStore.Save(_data);
    }

    /// <summary>
    /// Re-applies the colours that are assigned from code. These sit outside the DynamicResource
    /// mechanism, so a theme switch would otherwise leave the mod-source badge and status heading in
    /// the previous theme's colours.
    /// </summary>
    private void ColorFromCode()
    {
        var badgeKey = _modSourceValid switch
        {
            true => Palette.BadgeOk,
            false => ThemeKeys.DangerBackground,
            null => Palette.BadgeWarn,
        };

        ModSourceBadge.Background = Theme.Brush(badgeKey);
        ModSourceBadgeText.Foreground = Theme.Brush(_modSourceValid switch
        {
            true => "BadgeOkText",
            false => "DangerText",
            null => "BadgeWarnText",
        });

        UpdateStatusCard();
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLanguageChange) return;
        if (LanguageCombo.SelectedItem is not TextChoice choice) return;

        var language = choice.Value;

        // WPF raises SelectionChanged again while the dropdown's item containers are realised, and
        // that second event is not distinguishable from a user pick. Without this guard the handler
        // would run again with the same value, and — because it used to act unconditionally — it also
        // persisted the result, so a startup-time spurious event silently overwrote the saved
        // language. Acting only on a genuine change makes repeated events harmless.
        if (string.Equals(language, Loc.Current, StringComparison.Ordinal)) return;

        Loc.SetLanguage(language);

        // Text set from code does not follow the bindings, so it is re-applied here.
        BuildLocalizedCombos();
        BuildProxyCombo();
        RefreshCodeText();
        RefreshModSource();
        UpdateStatusCard();
        RefreshAllStatus();

        _data.InterfaceLanguage = language;
        LibraryStore.Save(_data);
    }

    /// <summary>
    /// Re-applies the interface text that is assigned from code rather than bound in XAML, so a
    /// language change does not leave part of the window in the previous language.
    /// </summary>
    private void RefreshCodeText()
    {
        AdminButton.Content = Loc.T(Native.IsElevated() ? "Toolbar.AlreadyAdmin" : "Toolbar.RestartAdmin");

        // The game rows bind to computed properties on GameEntry, which the language change cannot
        // reach on its own — see RaiseLocalizedText.
        foreach (var game in _data.Games) game.RaiseLocalizedText();

        ApplyGpuText();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AdminButton.IsEnabled = !Native.IsElevated();
        RefreshCodeText();

        RefreshModSource();
        RefreshAllStatus();

        _log.Write(Loc.T("Status.DataDir", AppPaths.Root));
        if (!Native.IsElevated())
            _log.Write(Loc.T("Status.NoPermissionHint"));

        ProbeGpuInBackground();

        // A fresh install has no mod files: the installer deliberately does not download them (no
        // network access during setup, no half-finished downloads). Fetch them here instead, without
        // asking — it is the one thing a new install has to do before anything else works.
        if (!HasModSource) FetchModFilesOnFirstRun();
    }

    /// <summary>
    /// First run: detect which version upstream is publishing, then download it.
    ///
    /// The version probe is advisory — it reads the few hundred bytes of the published INI and reports
    /// the banner, so the log says which version was fetched rather than only how many files arrived.
    /// If the probe fails (a blocked endpoint, no network yet) the download runs anyway: detection must
    /// never be the reason a fresh install cannot get its files.
    /// </summary>
    private void FetchModFilesOnFirstRun()
    {
        if (_busy) return;

        var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
        _log.Write(Loc.T("Fetch.AutoStart", target));

        _busy = true;
        var progress = UiProgress();

        Task.Run(async () =>
        {
            var detected = await ModFetcher.DetectLatestVersionAsync(CancellationToken.None).ConfigureAwait(false);
            if (detected is not null) progress.Report(Loc.T("Fetch.AutoDetected", detected));

            return await ModFetcher.DownloadIntoAsync(target, progress, CancellationToken.None,
                                                      versionLabel: detected).ConfigureAwait(false);
        }).ContinueWith(t =>
        {
            _busy = false;
            var result = t.Result;
            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
            BatchStatusText.Text = "";

            RefreshModSource();
            if (result.Ok)
            {
                MessageBox.Show(this,
                    result.Message + Loc.T("Fetch.DoneBodyFirst", result.Message),
                    Loc.T("Fetch.DoneTitleFirst"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // Kept non-blocking on purpose: the toolbar button and the status banner both point at
                // the same retry, so a failed first run still leaves a usable window.
                _log.Write(Loc.T("Fetch.AutoFailed", result.Message));
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ProbeGpuInBackground()
    {
        var ui = TaskScheduler.FromCurrentSynchronizationContext();

        Task.Run(Gpu.Probe).ContinueWith(t =>
        {
            var info = t.Result;
            _gpuInfo = info;

            _data.GpuName = info.Name;
            _data.GpuDriver = info.Driver;
            _data.RecommendedRouter = info.Router;
            LibraryStore.Save(_data);

            ApplyGpuText();
            _log.Write(Loc.T("Toolbar.Gpu") + " " + info.Name + " · " + info.Advice);
        }, ui);
    }

    /// <summary>
    /// Renders the GPU lines. Called both when the probe finishes and after a language change, since
    /// the advice text is built in code and would otherwise stay in the previous language.
    /// </summary>
    private void ApplyGpuText()
    {
        if (_gpuInfo is null) return;

        GpuText.Text = string.IsNullOrWhiteSpace(_gpuInfo.Driver)
            ? _gpuInfo.Name
            : Loc.T("Gpu.NameWithDriver", _gpuInfo.Name, _gpuInfo.Driver);

        // Regenerated rather than reused: the advice is assembled in code, so the stored string from
        // the probe is in whatever language was active at probe time.
        RouterHintText.Text = Gpu.AdviceFor(_gpuInfo);
    }

    // ---- mod source ---------------------------------------------------------

    /// <summary>
    /// Where mod files are read from. Resolved through ModSourceLocator so a source checkout run from
    /// bin\ still finds the repository's mod\ folder.
    /// </summary>
    private string SourcePath =>
        ModSourceLocator.FindExisting(_data.ModSourcePath) ?? _data.ModSourcePath;

    private ModSource CurrentSource() => new(SourcePath);

    private bool HasModSource => ModSourceLocator.FindExisting(_data.ModSourcePath) is not null;

    private void RefreshModSource()
    {
        var existing = ModSourceLocator.FindExisting(_data.ModSourcePath);

        if (existing is null)
        {
            // Show where a download would land, and say plainly that files are missing.
            var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
            ModSourceText.Text = target;
            ModSourceBadgeText.Text = Loc.T("Toolbar.ModNotReady");
            _modSourceValid = null;
            ColorFromCode();
            ModSourceText.ToolTip = Loc.T("Toolbar.ModMissingTip");
            ApplySettingsSchema(legacy: false);
            _log.Write(Loc.T("Fetch.NotReadyLog"));
            return;
        }

        var source = new ModSource(existing);
        ModSourceText.Text = existing;
        ModSourceBadgeText.Text = source.IsValid ? Loc.T("Toolbar.ModReady", source.Version) : Loc.T("Toolbar.ModIncomplete");
        _modSourceValid = source.IsValid;
        ColorFromCode();
        ModSourceText.ToolTip = source.IsValid
            ? Loc.T("Toolbar.ModReadyTip", Loc.Join(source.AvailableProxies))
            : source.ValidationMessage;

        ApplySettingsSchema(source.IsLegacySchema);

        if (!source.IsValid) _log.Write(Loc.T("Fetch.IncompleteLog", source.ValidationMessage));
    }

    /// <summary>
    /// Shows the configuration controls the local payload actually reads.
    ///
    /// Upstream changed the INI schema in 0.3.0 (Enabled / Optimized / Preset replaced Router /
    /// KernelImage / HardwareBilinear), and the manager only writes keys the template defines. Leaving
    /// the other set on screen would offer controls that silently do nothing.
    /// </summary>
    private void ApplySettingsSchema(bool legacy)
    {
        ModernSettings.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
        LegacySettings.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var target = ModSourceLocator.ResolveTarget(_data.ModSourcePath);

        // The picker replaces a plain confirmation dialog: choosing where to download from is the one
        // decision worth surfacing here, and it doubles as the confirmation step.
        var picker = new SourcePickerDialog { Owner = this };
        if (picker.ShowDialog() != true) return;

        DownloadModFiles(target, picker.SelectedSourceId);
    }

    /// <summary>
    /// Adds a proxy DLL the user picked to the entry-name list, so it can be deployed like the bundled
    /// entries — the case this exists for is a community build such as d3d12.dll, which this project
    /// does not ship.
    ///
    /// The manager did not download the file and cannot vouch for it. What it does is copy it
    /// faithfully, say what signature (if any) it carries, and warn when the name is one games never
    /// load, because then the deployment would simply do nothing.
    /// </summary>
    private void AddProxy_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var found = ModSourceLocator.FindExisting(_data.ModSourcePath);
        if (found is null)
        {
            _log.Write(Loc.T("Proxy.AddNeedSource"));
            MessageBox.Show(this, Loc.T("Proxy.AddNeedSource"), Loc.T("Proxy.AddTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog { Title = Loc.T("Proxy.AddTitle"), Filter = Loc.T("Proxy.AddFilter") };
        if (dialog.ShowDialog() != true) return;

        var name = Path.GetFileName(dialog.FileName);

        // The entry name is the DLL name the game resolves, so a name nothing loads means the mod never
        // runs. The file is the user's, which makes the decision theirs too; the manager only makes the
        // consequence explicit before the file is copied in.
        if (!ModSource.IsKnownProxyName(name))
        {
            if (MessageBox.Show(this, Loc.T("Proxy.AddUnknownName", name), Loc.T("Proxy.AddTitle"),
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        var result = ModSource.ImportProxy(found, dialog.FileName);
        _log.Details(result.Lines);
        _log.Result(result.Ok, result.Message);

        if (!result.Ok) return;

        RefreshModSource();
        BuildProxyCombo();
        UpdateStatusCard();
    }

    /// <summary>
    /// Downloads the mod files into <paramref name="target"/>, for the toolbar button.
    /// </summary>
    /// <param name="sourceId">
    /// A specific source id, or <see cref="ModFetcher.AutoSourceId"/> to try each in turn.
    /// </param>
    private void DownloadModFiles(string target, string sourceId = ModFetcher.AutoSourceId)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        _busy = true;
        _log.Write(Loc.T("Fetch.StartLog"));

        var progress = UiProgress();

        Task.Run(async () =>
        {
            // Same probe as the first run, so an update also names the release it fetched.
            var detected = await ModFetcher.DetectLatestVersionAsync(CancellationToken.None).ConfigureAwait(false);
            if (detected is not null) progress.Report(Loc.T("Fetch.AutoDetected", detected));

            return await ModFetcher.DownloadIntoAsync(target, progress, CancellationToken.None, sourceId, detected)
                .ConfigureAwait(false);
        })
            .ContinueWith(t =>
            {
                _busy = false;
                var result = t.Result;
                _log.Details(result.Lines);
                _log.Result(result.Ok, result.Message);
                BatchStatusText.Text = "";

                // Re-read the profile defaults from the freshly downloaded INI text.
                RefreshModSource();
                if (result.Ok)
                {
                    // The body already starts with the result message (its {0}), so it is not repeated here.
                    MessageBox.Show(this,
                        Loc.T("Fetch.DoneBody", result.Message),
                        Loc.T("Fetch.DoneTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ---- game list ----------------------------------------------------------

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var game = Selected;
        NoSelectionText.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        if (game is null) return;

        DataContext = game;
        ProxyCombo.SelectedValue = game.PreferredProxy;
        UpdateStatusCard();
    }

    private void ProxyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProxyChange) return;
        if (Selected is null) return;
        if (ProxyCombo.SelectedValue is string value) Selected.PreferredProxy = value;
    }

    private void UpdateStatusCard()
    {
        var game = Selected;
        if (game is null) return;

        StatusTitle.Text = game.StatusText + " — " + game.StatusDetail;
        StatusTitle.Foreground = Theme.Brush(game.StatusColor);
        StatusBody.Text = StatusDetailText(game);

        var deployed = game.Deployment is not null;
        RestoreButton.IsEnabled = deployed;
        AdoptButton.IsEnabled = !deployed || game.Status == GameStatus.Modified;
        OpenLogButton.IsEnabled = game.RenderDir is not null &&
                                  Directory.Exists(Path.Combine(game.RenderDir, ModSource.LogDirName));

        // Deployment stays available on games with a kernel anti-cheat; RunDeploy warns and asks for
        // confirmation first. The scan cannot know whether this game's protection lets a given entry
        // name survive — a community entry such as d3d12.dll exists precisely because some do — so the
        // decision is the user's, not the scan's.
        DeployButton.ToolTip = game.HasKernelAntiCheat ? Loc.T("Deploy.BlockedTooltip") : null;
    }

    private static string StatusDetailText(GameEntry game)
    {
        if (game.Deployment is null)
            return Loc.T("Status.DeployHint");

        var parts = new List<string>
        {
            game.Deployment.ProxyName,
            "Mod " + game.Deployment.ModVersion,
            game.Deployment.DeployedAt,
        };

        if (game.Deployment.Backups.Count > 0)
            parts.Add(Loc.T("Detail.BackupCount", game.Deployment.Backups.Count));

        return string.Join(" · ", parts);
    }

    private void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("List.AddFolderTitle") };
        if (!string.IsNullOrWhiteSpace(_data.LastScanRoot) && Directory.Exists(_data.LastScanRoot))
            dialog.InitialDirectory = _data.LastScanRoot;
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        _data.LastScanRoot = folder;

        var game = new GameEntry
        {
            Name = Detection.FriendlyName(folder),
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile { Router = _data.RecommendedRouter },
        };

        _data.Games.Add(game);
        GameList.SelectedItem = game;
        _log.Write(Loc.T("List.AddedMessage", folder));

        // AttachFolder fills in the render directory, runs the anti-cheat scan and warns if needed.
        AttachFolder(game, folder);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var body = Loc.T("List.RemoveConfirm", game.Name);
        if (game.Deployment is not null)
            body += Loc.T("List.RemoveWarnDeployed");

        if (MessageBox.Show(body, Loc.T("List.RemoveTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _data.Games.Remove(game);
        LibraryStore.Save(_data);
        GameList.SelectedIndex = _data.Games.Count > 0 ? 0 : -1;
    }

    // ---- status -------------------------------------------------------------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null) return;

        var check = await Task.Run(() => DeploymentService.Evaluate(game));
        DeploymentService.Apply(game, check);

        UpdateStatusCard();
        _log.Write(Loc.T("Status.CheckResult", game.Name, game.StatusText, game.StatusDetail));
    }

    private void RefreshAll_Click(object sender, RoutedEventArgs e) => RefreshAllStatus();

    /// <summary>
    /// Re-reads every game's state.
    ///
    /// The inspection runs on the thread pool because it is expensive: each candidate entry name is
    /// verified with WinVerifyTrust over a ~15 MB DLL, and a deployed game is hashed again. Doing
    /// that inline froze the window for seconds once a few games were listed. Results are applied
    /// back here, since assigning those properties is what raises the change notifications the list
    /// binds to.
    /// </summary>
    private async void RefreshAllStatus()
    {
        // Called at startup, after batch operations, and by the refresh button, so two runs can
        // otherwise overlap and fight over the same properties.
        if (_statusRefreshRunning) return;
        _statusRefreshRunning = true;

        try
        {
            var games = _data.Games.ToList();
            var results = await Task.Run(() =>
                games.Select(g => (Game: g, Check: DeploymentService.Evaluate(g))).ToList());

            foreach (var (game, check) in results) DeploymentService.Apply(game, check);

            LibraryStore.Save(_data);
            UpdateStatusCard();
        }
        catch (Exception ex)
        {
            _log.Write(Loc.T("Status.RefreshFailed", ex.Message));
        }
        finally
        {
            _statusRefreshRunning = false;
        }
    }
}
