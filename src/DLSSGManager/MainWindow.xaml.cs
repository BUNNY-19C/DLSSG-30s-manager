using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using DLSSGManager.ViewModels;
using Microsoft.Win32;

namespace DLSSGManager;

public partial class MainWindow : Window
{
    /// <summary>Broadcast when a system setting changes; "ImmersiveColorSet" names the app colour mode.</summary>
    private const int WM_SETTINGCHANGE = 0x001A;

    private readonly AppData _data;
    private OutputLog _log = null!;
    private ListCollectionView? _gamesView;
    private CancellationTokenSource? _scanCts;

    /// <summary>Cancel + pause handles for the running mod-file download, when one is in flight.</summary>
    private CancellationTokenSource? _fetchCts;
    private ModFetcher.DownloadGate? _fetchGate;


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

    /// <summary>Saving and applying state of the selected game.</summary>
    public GameManagementViewModel Management { get; } = new();

    /// <summary>Batch deployment over the ticked games.</summary>
    public BatchViewModel Batch { get; }

    /// <summary>Anti-cheat banner and notices for the selected game.</summary>
    public ProtectionViewModel Protection { get; } = new();

    public MainWindow()
    {
        // Loaded once and reused: the language has to be applied before the first XAML string is
        // resolved (otherwise the window renders in the default language and switches a moment later),
        // and the rest of the setup needs the same data.
        _data = LibraryStore.Load();

        Loc.SetLanguage(_data.InterfaceLanguage);
        Theme.ApplyPreference(_data.InterfaceTheme);

        // Created before InitializeComponent: the XAML binds to these properties, and a binding that
        // first sees null would not notice the assignment.
        Batch = new BatchViewModel(this, _data, () => GameList.Items.Cast<GameEntry>());

        InitializeComponent();

        AppPaths.EnsureCreated();
        _log = new OutputLog(OutputBox, LastLogText);

        VersionText.Text = AppVersion.Label;
        Title = Loc.T("App.Name") + "  " + AppVersion.Label;

        BuildLocalizedCombos();
        BuildLanguageCombo();
        BuildThemeCombo();
        BuildProxyCombo();

        // Filter the view, never the persisted collection or the batch-operation targets.
        _gamesView = new ListCollectionView(_data.Games)
        {
            Filter = MatchesGameFilters,
        };
        _gamesView.LiveFilteringProperties.Add(nameof(GameEntry.Name));
        _gamesView.LiveFilteringProperties.Add(nameof(GameEntry.Status));
        _gamesView.IsLiveFiltering = true;
        ((System.Collections.Specialized.INotifyCollectionChanged)_gamesView).CollectionChanged += (_, _) => UpdateManagementState();
        GameList.ItemsSource = _gamesView;
        if (_data.Games.Count > 0) GameList.SelectedIndex = 0;

        Loaded += OnLoaded;
        InitializeManagement();
    }

    private GameEntry? Selected => GameList.SelectedItem as GameEntry;

    /// <summary>
    /// Fills the numeric dropdowns. Built once: the entries re-read their labels on a language
    /// change (see <see cref="Choice{T}"/>), so the selection is never disturbed.
    /// </summary>
    private void BuildLocalizedCombos()
    {
        FrameCombo.ItemsSource = new[]
        {
            Choice<int>.Localized(1, "Detail.Frames2X"),
            Choice<int>.Localized(2, "Detail.Frames3X"),
            Choice<int>.Localized(3, "Detail.Frames4X"),
            Choice<int>.Localized(4, "Detail.Frames5X"),
            Choice<int>.Localized(5, "Detail.Frames6X"),
        };

        LogCombo.ItemsSource = new[]
        {
            Choice<int>.Localized(0, "Detail.Log0"),
            Choice<int>.Localized(1, "Detail.Log1"),
            Choice<int>.Localized(2, "Detail.Log2"),
            Choice<int>.Localized(3, "Detail.Log3"),
        };

        // The 0.3.3 consistency tier; the selected value is the plain 0-3.
        TierCombo.ItemsSource = new[]
        {
            Choice<int>.Localized(0, "Detail.Tier0"),
            Choice<int>.Localized(1, "Detail.Tier1"),
            Choice<int>.Localized(2, "Detail.Tier2"),
            Choice<int>.Localized(3, "Detail.Tier3"),
        };
    }

    /// <summary>Fills the language picker without triggering the change handler.</summary>
    private void BuildLanguageCombo()
    {
        _suppressLanguageChange = true;
        LanguageCombo.ItemsSource = Languages.All
            .Select(code => Choice<string>.Fixed(code, Languages.DisplayName(code)))
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
            Choice<string>.Localized(ThemeKeys.SystemPreference, "Theme.System"),
            Choice<string>.Localized(ThemeKeys.StorageValue(AppTheme.Dark), "Theme.Dark"),
            Choice<string>.Localized(ThemeKeys.StorageValue(AppTheme.Light), "Theme.Light"),
        };
        ThemeCombo.SelectedValuePath = "Value";
        // The item template binds the closed selection directly to Choice.Text.
        // DisplayMemberPath lets the selection box cache the old translated string.
        ThemeCombo.SelectedValue = Theme.Preference;
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
            var imported = CurrentSource().ImportedProxies.ToArray();

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
        if (ThemeCombo.SelectedItem is not Choice<string> choice) return;
        if (choice.Value == Theme.Preference) return;

        var before = Theme.Current;
        Theme.ApplyPreference(choice.Value);
        if (Theme.Current != before) AfterThemeSwitch();

        _data.InterfaceTheme = Theme.Preference;
        SaveLibrary();
    }

    /// <summary>Repaints what the dictionary swap cannot reach on its own.</summary>
    private void AfterThemeSwitch()
    {
        ColorFromCode();

        // The list's status dots and status text bind through ThemeBrushConverter, which only
        // re-evaluates when the source notifies — the dictionary swap alone leaves them in the
        // previous theme's colours.
        foreach (var game in _data.Games) game.RaiseThemeColors();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WatchSystemTheme);
    }

    /// <summary>
    /// Follows the Windows app mode while the preference is "system". Windows announces the change
    /// with WM_SETTINGCHANGE naming "ImmersiveColorSet"; the registry is re-read rather than trusting
    /// the message, and <see cref="Theme.FollowSystemChange"/> ignores the repeats Windows sends.
    /// </summary>
    private IntPtr WatchSystemTheme(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
            Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (Theme.FollowSystemChange()) AfterThemeSwitch();
            });
        }

        return IntPtr.Zero;
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
        if (LanguageCombo.SelectedItem is not Choice<string> choice) return;

        var language = choice.Value;

        // WPF raises SelectionChanged again while the dropdown's item containers are realised, and
        // that second event is not distinguishable from a user pick. Without this guard the handler
        // would run again with the same value, and — because it used to act unconditionally — it also
        // persisted the result, so a startup-time spurious event silently overwrote the saved
        // language. Acting only on a genuine change makes repeated events harmless.
        if (string.Equals(language, Loc.Current, StringComparison.Ordinal)) return;

        // One pass on the UI thread, so the window repaints once, already in the new language: bound
        // text and the localized dropdown entries follow SetLanguage's notification, and status details
        // are messages rendered on read — no game needs checking again for its text to switch.
        Loc.SetLanguage(language);

        // Text set from code does not follow the bindings, so it is re-applied here.
        BuildProxyCombo();
        RefreshCodeText();
        RefreshModSource();
        Protection.Refresh();
        Management.RefreshText();
        UpdateStatusCard();

        _data.InterfaceLanguage = language;
        SaveLibrary();
    }

    /// <summary>
    /// Re-applies the interface text that is assigned from code rather than bound in XAML, so a
    /// language change does not leave part of the window in the previous language.
    /// </summary>
    private void RefreshCodeText()
    {
        // The version is language-independent, but the title text is not — and the code-assigned
        // title has replaced the loc binding from XAML, so re-apply it here.
        Title = Loc.T("App.Name") + "  " + AppVersion.Label;
        DownloadTargetText.Text = Loc.T("Manage.DownloadTarget", ModFetcher.SupportedRelease, BuildCatalog.BundledRuntimeVersion);

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
    private async void FetchModFilesOnFirstRun()
    {
        if (_busy) return;

        _data.ModSourcePath = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
        var target = BuildCatalog.SourceDirectory(_data.ModSourcePath, _data.DownloadBuild);
        _log.Write(Loc.T("Fetch.AutoStart", target));

        _busy = true;

        OpResult result;
        try
        {
            result = await RunModFetchAsync(target, ModFetcher.AutoSourceId);
        }
        catch (OperationCanceledException)
        {
            _busy = false;
            BatchStatusText.Text = "";
            _log.Write(Loc.T("Fetch.Cancelled"));
            return;
        }
        catch (Exception ex)
        {
            _busy = false;
            BatchStatusText.Text = "";
            // Kept non-blocking on purpose: the toolbar button and the status banner both point at
            // the same retry, so a failed first run still leaves a usable window.
            _log.Write(Loc.T("Fetch.AutoFailed", ex.Message));
            AppPaths.Log("首次获取 Mod 文件失败: " + ex);
            return;
        }

        _busy = false;
        BatchStatusText.Text = "";
        _log.Details(result.Lines);
        _log.Result(result.Ok, result.Message);

        RefreshModSource();
        if (result.Ok && !_closeWhenIdle)
        {
            // The body already starts with the result message (its {0}) — do not repeat it.
            MessageBox.Show(this, Loc.T("Fetch.DoneBodyFirst", result.Message),
                Loc.T("Fetch.DoneTitleFirst"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!result.Ok)
        {
            _log.Write(Loc.T("Fetch.AutoFailed", result.Message));
        }
    }

    /// <summary>
    /// Views and edits the GPU display name.
    ///
    /// Some games gate features such as frame generation on the reported model, which is the reason the
    /// edit exists. Only the registry display name is written — the hardware id, the driver and its
    /// capabilities are untouched — and "restore" puts back the name bound to the physical device, which
    /// is what undoes a spoof even when the spoofing tool is gone.
    /// </summary>
    private void GpuName_Click(object sender, RoutedEventArgs e)
    {
        var adapter = Gpu.NvidiaAdapter();
        if (adapter is null)
        {
            _log.Write(Loc.T("GpuName.NoGpu"));
            return;
        }

        var registryName = Gpu.RegistryDisplayName(adapter.DeviceId, adapter.Name);
        var realName = Gpu.PnpDeviceDescription(adapter.DeviceInstancePath);

        var dialog = new GpuNameDialog(adapter.Name, registryName, realName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var target = dialog.Restore ? realName : dialog.NewName;
        if (string.IsNullOrWhiteSpace(target))
        {
            _log.Write("✗ " + Loc.T("GpuName.NoRealName"));
            return;
        }

        var error = dialog.Restore ? null : Gpu.InvalidDisplayNameReason(target);
        if (error is not null)
        {
            _log.Write("✗ " + error);
            return;
        }

        var message = Gpu.WriteRegistryDisplayName(adapter.DeviceId, adapter.Name, target);
        if (message is not null)
        {
            _log.Write("✗ " + message + "  " + Loc.T("GpuName.AdminHint"));
            MessageBox.Show(this, message + "\n\n" + Loc.T("GpuName.AdminHint"),
                Loc.T("GpuName.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _log.Write(dialog.Restore ? Loc.T("GpuName.Restored", target) : Loc.T("GpuName.Changed", target));

        // The running process may still report the old name until it restarts; re-probe anyway so the
        // toolbar refreshes whenever Windows decides to answer with the new value.
        ProbeGpuInBackground();
    }

    private async void ProbeGpuInBackground()
    {
        _pendingChecks++;
        try
        {
            var info = await Task.Run(Gpu.Probe);
            _gpuInfo = info;

            _data.GpuName = info.Name;
            _data.GpuDriver = info.Driver;
            _data.RecommendedRouter = info.Router;
            SaveLibrary();

            ApplyGpuText();
            _log.Write(Loc.T("Toolbar.Gpu") + " " + info.Name + " · " + info.Advice);
        }
        catch (Exception ex)
        {
            // The toolbar simply stays empty; a probe failure is not worth interrupting the user.
            AppPaths.Log("显卡探测失败: " + ex);
        }
        finally { _pendingChecks--; }
    }

    /// <summary>
    /// Renders the GPU lines. Called both when the probe finishes and after a language change, since
    /// the advice text is built in code and would otherwise stay in the previous language.
    ///
    /// The row shows a one-line summary; the full advice (the long mismatch explanation, the
    /// machine-match note) goes on the tooltip, and the startup log already carries it too.
    /// </summary>
    private void ApplyGpuText()
    {
        if (_gpuInfo is null) return;

        GpuText.Text = _gpuInfo.Name;
        RouterHintText.Text = Gpu.CompactAdvice(_gpuInfo);
        RouterHintText.ToolTip = Gpu.AdviceFor(_gpuInfo);
    }

    // ---- mod source ---------------------------------------------------------

    /// <summary>
    /// Where mod files are read from. Resolved through ModSourceLocator so a source checkout run from
    /// bin\ still finds the repository's mod\ folder.
    /// </summary>
    private string SourcePath =>
        ModSourceLocator.FindExisting(_data.ModSourcePath) ??
        (string.IsNullOrWhiteSpace(_data.ModSourcePath) ? AppPaths.UserModDir : _data.ModSourcePath);

    private ModSource CurrentSource(GameEntry? game = null) => new(BuildCatalog.SourceDirectory(SourcePath,
        (game ?? Selected)?.Profile.RuntimeModel ?? BuildCatalog.Default));

    private bool HasModSource => ModSourceLocator.FindExisting(_data.ModSourcePath) is not null ||
        ModSourceLocator.LooksLikeSource(BuildCatalog.SourceDirectory(SourcePath, _data.DownloadBuild));

    private void RefreshModSource()
    {
        var selectedRoot = CurrentSource().Root;
        var existing = ModSourceLocator.LooksLikeSource(selectedRoot) ? selectedRoot : null;

        if (existing is null)
        {
            // Show where a download would land, and say plainly that files are missing.
            ModSourceText.Text = selectedRoot;
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

        ApplySettingsSchema(source.IsLegacySchema, source.UsesRuntimeDirectory);

        if (!source.IsValid) _log.Write(Loc.T("Fetch.IncompleteLog", source.ValidationMessage));
    }

    /// <summary>
    /// Shows the configuration controls the local payload actually reads.
    ///
    /// Upstream changed the INI schema in 0.3.0 (Enabled / Optimized / Preset replaced Router /
    /// KernelImage / HardwareBilinear), and the manager only writes keys the template defines. Leaving
    /// the other set on screen would offer controls that silently do nothing.
    /// </summary>
    private void ApplySettingsSchema(bool legacy, bool runtimeDirectory = false)
    {
        ModernSettings.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
        LegacySettings.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
        Runtime040Settings.Visibility = runtimeDirectory ? Visibility.Visible : Visibility.Collapsed;
        Runtime040Advanced.Visibility = runtimeDirectory ? Visibility.Visible : Visibility.Collapsed;
        PresetCOption.Visibility = runtimeDirectory ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMod_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _log.Write(Loc.T("Scan.Busy")); return; }

        var sourceRoot = ModSourceLocator.ResolveTarget(_data.ModSourcePath);
        var target = BuildCatalog.SourceDirectory(sourceRoot, _data.DownloadBuild);

        // The dialog runs the download or offline import itself, with its own progress, pause, skip
        // and cancel; the window only records what it did once it closes.
        var dialog = new SourcePickerDialog(target, _data.DownloadBuild) { Owner = this };
        dialog.ShowDialog();

        foreach (var result in dialog.Results)
        {
            _log.Details(result.Lines);
            _log.Result(result.Ok, result.Message);
        }

        if (!dialog.Installed) return;

        _data.ModSourcePath = sourceRoot;
        SaveLibrary();

        // Re-read the profile defaults and entry names from the freshly installed payload.
        RefreshModSource();
        BuildProxyCombo();
        UpdateStatusCard();
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

        var found = CurrentSource().Root;
        if (!ModSourceLocator.LooksLikeSource(found))
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
    /// The first-run fetch: probe, then download with the progress panel in the log strip visible.
    /// The panel carries a byte-level progress bar, the transfer speed, pause and cancel. Nothing is
    /// written to the mod folder until verification passes, so an aborted download leaves the previous
    /// payload intact; the toolbar's download dialog is where another source can then be picked.
    /// </summary>
    private async Task<OpResult> RunModFetchAsync(string target, string sourceId)
    {
        _fetchCts = new CancellationTokenSource();
        _fetchGate = new ModFetcher.DownloadGate();
        var ct = _fetchCts.Token;

        FetchPauseButton.Content = Loc.T("Fetch.Pause");
        FetchProgressBar.Value = 0;
        FetchProgressBar.IsIndeterminate = false;
        FetchProgressSpeed.Text = "";
        // 进度行替换日志标题行：日志条高度不变，正文不跳动。
        FetchProgressPanel.Visibility = Visibility.Visible;
        LogHeader.Visibility = Visibility.Collapsed;

        var textProgress = UiProgress();
        var byteProgress = new Progress<ModFetcher.FetchProgress>(UpdateFetchProgress);

        try
        {
            var detected = await ModFetcher.DetectLatestVersionAsync(ct);
            if (detected is not null) textProgress.Report(Loc.T("Fetch.AutoDetected", detected));

            var result = await ModFetcher.DownloadIntoAsync(target, textProgress, ct, sourceId,
                                                           byteProgress, _fetchGate, _data.DownloadBuild);
            if (result.Ok) SaveLibrary();
            return result;
        }
        finally
        {
            FetchProgressPanel.Visibility = Visibility.Collapsed;
            LogHeader.Visibility = Visibility.Visible;
            _fetchCts.Dispose();
            _fetchCts = null;
            _fetchGate = null;
        }
    }

    /// <summary>Applies one byte-progress tick. Runs on the UI thread (Progress&lt;T&gt; posts).</summary>
    private void UpdateFetchProgress(ModFetcher.FetchProgress p)
    {
        if (p.Phase != ModFetcher.FetchPhase.Downloading)
        {
            FetchProgressText.Text = ModFetcher.DescribePhase(p);
            FetchProgressBar.IsIndeterminate = true;
            FetchProgressSpeed.Text = "";
            return;
        }

        FetchProgressText.Text = p.FileTotal <= 1
            ? Loc.T("Fetch.ProgressArchive", p.SourceName)
            : Loc.T("Fetch.ProgressFiles", p.SourceName, p.FileIndex, p.FileTotal);

        if (p.BytesTotal > 0)
        {
            FetchProgressBar.IsIndeterminate = false;
            FetchProgressBar.Value = Math.Clamp(p.BytesDone * 100.0 / p.BytesTotal, 0, 100);
        }
        else
        {
            // No Content-Length from this endpoint: the bar cannot show a fraction.
            FetchProgressBar.IsIndeterminate = true;
        }

        FetchProgressSpeed.Text = ModFetcher.FormatSpeed(p.SpeedBps);
    }

    private void FetchPause_Click(object sender, RoutedEventArgs e)
    {
        var gate = _fetchGate;
        if (gate is null) return;

        if (gate.Paused)
        {
            gate.Resume();
            FetchPauseButton.Content = Loc.T("Fetch.Pause");
        }
        else
        {
            gate.Pause();
            FetchPauseButton.Content = Loc.T("Fetch.Resume");
        }
    }

    private void FetchCancel_Click(object sender, RoutedEventArgs e) => _fetchCts?.Cancel();

    // ---- game list ----------------------------------------------------------

    private bool MatchesGameFilters(object item)
    {
        if (item is not GameEntry game ||
            !game.Name.Contains(GameSearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        return ((GameStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "deployed" => game.Status == GameStatus.Deployed,
            "attention" => game.Status is GameStatus.Modified or GameStatus.Missing or GameStatus.Unknown,
            _ => true,
        };
    }

    private void GameSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshGameFilters();

    private void GameStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshGameFilters();

    private void RefreshGameFilters()
    {
        if (_gamesView is null) return;
        var selected = Selected;
        _gamesView.Refresh();
        if (selected is not null && _gamesView.Contains(selected)) GameList.SelectedItem = selected;
        else GameList.SelectedIndex = GameList.Items.Count > 0 ? 0 : -1;
        UpdateManagementState();
    }

    private void ClearGameSearch_Click(object sender, RoutedEventArgs e)
    {
        GameSearchBox.Clear();
        GameSearchBox.Focus();
    }

    private void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var game = Selected;
        NoSelectionText.Visibility = game is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = game is null ? Visibility.Collapsed : Visibility.Visible;
        if (game is null) { Protection.Track(null); UpdateManagementState(); return; }

        DataContext = game;
        Protection.Track(game);
        ProxyCombo.SelectedValue = game.PreferredProxy;
        RefreshModSource();
        BuildProxyCombo();
        UpdateStatusCard();
        DetailScrollViewer.ScrollToTop();
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
        OpenLogButton.IsEnabled = game.RenderDir is not null &&
                                  Directory.Exists(Path.Combine(game.RenderDir, ModSource.LogDirName, "logs"));

        // Deployment stays available on games with a kernel anti-cheat (the button's tooltip, from
        // Protection, says so); RunDeploy warns and asks for confirmation first. The scan cannot know
        // whether this game's protection lets a given entry name survive — a community entry such as
        // d3d12.dll exists precisely because some do — so the decision is the user's, not the scan's.
        UpdateManagementState();
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
        if (_busy) return;
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

        GameSearchBox.Clear();
        GameStatusFilter.SelectedIndex = 0;
        _data.Games.Add(game);
        GameList.SelectedItem = game;
        _log.Write(Loc.T("List.AddedMessage", folder));

        // AttachFolder fills in the render directory, runs the anti-cheat scan and warns if needed.
        AttachFolder(game, folder);
    }

    private void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var game = Selected;
        if (game is null) return;

        var body = Loc.T("List.RemoveConfirm", game.Name);
        if (game.Deployment is not null)
            body += Loc.T("List.RemoveWarnDeployed");

        if (MessageBox.Show(body, Loc.T("List.RemoveTitle"), MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        _data.Games.Remove(game);
        SaveLibrary();
        GameList.SelectedIndex = GameList.Items.Count > 0 ? 0 : -1;
    }

    // ---- status -------------------------------------------------------------

    private void ApplyGameCheck(GameEntry game, DeploymentService.GameCheck check)
    {
        var adopted = game.Deployment is null && check.Adoption is not null;
        DeploymentService.Apply(game, check);
        if (adopted)
        {
            _log.Write(Loc.T("Adopt.Done", game.Name));
            _log.Write(Loc.T("Adopt.Note"));
        }
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        var game = Selected;
        if (game is null || GameWorkRunning) return;
        _busy = true;
        try
        {
            var check = await Task.Run(() => DeploymentService.Evaluate(game));
            ApplyGameCheck(game, check);
            SaveLibrary();
            UpdateStatusCard();
            _log.Write(Loc.T("Status.CheckResult", game.Name, game.StatusText, game.StatusDetail));
        }
        catch (Exception ex) { _log.Write(Loc.T("Status.RefreshFailed", ex.Message)); }
        finally { _busy = false; }
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
        if (_statusRefreshRunning || _busy) return;
        _statusRefreshRunning = true;

        try
        {
            var games = _data.Games.ToList();
            var results = await Task.Run(() =>
                games.Select(g => (Game: g, Check: DeploymentService.Evaluate(g))).ToList());

            foreach (var (game, check) in results) ApplyGameCheck(game, check);

            SaveLibrary();
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
