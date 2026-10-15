using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DLSSGManager;

/// <summary>Chooses a payload and source, then downloads or imports it before returning to the window.</summary>
public partial class SourcePickerDialog : Window
{
    private readonly string _destination;
    private readonly string _runtimeModel;
    private readonly List<OpResult> _results = new();
    private IReadOnlyList<ModFetcher.SourceProbe>? _probes;
    private CancellationTokenSource? _probeCts;
    private CancellationTokenSource? _workCts;
    private ModFetcher.DownloadGate? _gate;
    private string _sourceId = ModFetcher.AutoSourceId;
    private bool _ready;
    private bool _busy;
    private bool _closing;
    private bool _closeWhenIdle;

    public IReadOnlyList<OpResult> Results => _results;
    public bool Installed { get; private set; }

    private PayloadPackage Package => BasePackageRadio.IsChecked == true ? PayloadPackage.Base : PayloadPackage.Full;
    private bool SupportedBuild => BuildCatalog.Normalize(_runtimeModel) == BuildCatalog.Default;

    public SourcePickerDialog(string destination, string runtimeModel)
    {
        _destination = destination;
        _runtimeModel = runtimeModel;
        InitializeComponent();
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 40);
        _ready = true;
        BuildList();
        SetBusy(false);
        if (!SupportedBuild)
        {
            ResultText.Text = Loc.T("Manage.RetiredBuild");
            ResultText.Visibility = Visibility.Visible;
        }
    }

    private Style? LookupStyle(string key) => TryFindResource(key) as Style;

    private void BuildList()
    {
        var sources = ModFetcher.AvailableSources.Where(s => s.Package == Package).ToArray();
        if (!sources.Any(s => s.Id == _sourceId)) _sourceId = ModFetcher.AutoSourceId;
        var order = ModFetcher.RankSourceIds(sources.Select(s => s.Id).ToArray(),
            _probes ?? Array.Empty<ModFetcher.SourceProbe>());
        OptionsPanel.Children.Clear();
        OptionsPanel.Children.Add(BuildOption(ModFetcher.AutoSourceId, Loc.T("Fetch.PickerAuto"),
            null, Loc.T("Fetch.PickerAutoNote")));
        foreach (var id in order)
        {
            var source = sources.First(s => s.Id == id);
            var probe = _probes?.FirstOrDefault(p => p.SourceId == id);
            var note = source.Note;
            if (probe is not null) note += "\n" + ModFetcher.DescribeProbe(probe);
            OptionsPanel.Children.Add(BuildOption(id, source.Name,
                Loc.T(source.Official ? "Fetch.PickerOfficial" : "Fetch.PickerMirror"), note));
        }
    }

    private RadioButton BuildOption(string id, string title, string? badge, string note)
    {
        var titleRow = new WrapPanel();
        titleRow.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        if (badge is not null)
        {
            var badgeText = new TextBlock { Text = badge };
            if (LookupStyle("PickerBadgeText") is { } textStyle) badgeText.Style = textStyle;
            var border = new Border { Child = badgeText };
            if (LookupStyle("PickerBadge") is { } badgeStyle) border.Style = badgeStyle;
            titleRow.Children.Add(border);
        }
        var noteText = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap };
        if (LookupStyle("PickerNoteText") is { } noteStyle) noteText.Style = noteStyle;
        var content = new StackPanel();
        content.Children.Add(titleRow);
        content.Children.Add(noteText);
        var radio = new RadioButton
        {
            GroupName = "source", Content = content,
            IsChecked = id == _sourceId, Margin = new Thickness(0, 4, 0, 4),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        if (LookupStyle("PickerRadio") is { } style) radio.Style = style;
        radio.Checked += (_, _) => _sourceId = id;
        return radio;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (SupportedBuild) await ProbeAsync();
    }

    private async void Package_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy || !SupportedBuild) return;
        _probes = null;
        _sourceId = ModFetcher.AutoSourceId;
        BuildList();
        await ProbeAsync();
    }

    private async void Retest_Click(object sender, RoutedEventArgs e) => await ProbeAsync();

    private async Task ProbeAsync()
    {
        if (_busy || _closing || !SupportedBuild) return;
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _probeCts = cts;
        var package = Package;
        ProbeStatusText.Text = Loc.T("Fetch.Probing");
        RetestButton.IsEnabled = false;
        try
        {
            var probes = await ModFetcher.ProbeSourcesAsync(package, cts.Token);
            if (_closing || _busy || cts.IsCancellationRequested || _probeCts != cts) return;
            _probes = probes;
            BuildList();
            ProbeStatusText.Text = Loc.T("Fetch.ProbeSummary", probes.Count(p => p.Reachable), probes.Count);
            foreach (var probe in probes) AppPaths.Log(ModFetcher.DescribeProbe(probe));
            var size = probes.FirstOrDefault(p => p.Reachable && p.TotalBytes > 0)?.TotalBytes;
            if (package == PayloadPackage.Full && size is > 0)
                FullPackageNote.Text = Loc.T("Fetch.PackageFullSize", ModFetcher.FormatSize(size.Value));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closing && _probeCts == cts) ProbeStatusText.Text = Loc.T("Fetch.Failed", ex.Message);
            AppPaths.Log("下载源测速失败: " + ex);
        }
        finally
        {
            if (_probeCts == cts)
            {
                _probeCts = null;
                if (!_closing) RetestButton.IsEnabled = !_busy && SupportedBuild;
            }
            cts.Dispose();
        }
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !SupportedBuild) return;
        var package = Package;
        var source = _sourceId;
        var probes = _probes;
        await RunAsync((text, bytes, ct) => ModFetcher.DownloadIntoAsync(_destination, text, ct,
            source, bytes, _gate, _runtimeModel, package, probes), importing: false);
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !SupportedBuild) return;
        var dialog = new OpenFileDialog { Filter = Loc.T("Fetch.ZipFilter"), CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName);
    }

    private Task ImportAsync(string path) => RunAsync((text, _, ct) =>
        ModFetcher.ImportArchiveAsync(path, _destination, text, ct, _runtimeModel), importing: true);

    private async Task RunAsync(
        Func<IProgress<string>, IProgress<ModFetcher.FetchProgress>, CancellationToken, Task<OpResult>> operation,
        bool importing)
    {
        if (_busy || _closing || !SupportedBuild) return;
        _probeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _workCts = cts;
        _gate = importing ? null : new ModFetcher.DownloadGate();
        SetBusy(true);
        ResultText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        TransferBar.IsIndeterminate = true;
        ProgressTitle.Text = Loc.T(importing ? "Fetch.Importing" : "Fetch.PickerDownload");
        ProgressDetail.Text = "";
        ProgressStatus.Text = "";
        PauseButton.Content = Loc.T("Fetch.Pause");
        PauseButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        var text = new Progress<string>(message =>
        {
            if (_workCts != cts || _closing) return;
            ProgressStatus.Text = message;
        });
        var bytes = new Progress<ModFetcher.FetchProgress>(progress =>
        {
            if (_workCts == cts && !_closing) UpdateProgress(progress);
        });
        OpResult result;
        try
        {
            result = await operation(text, bytes, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = new OpResult { Ok = false, Message = Loc.T("Fetch.Cancelled") };
        }
        catch (Exception ex)
        {
            result = new OpResult { Ok = false, Message = Loc.T("Fetch.Failed", ex.Message) };
            AppPaths.Log("下载或导入失败: " + ex);
        }
        finally
        {
            _gate?.Resume();
            _gate = null;
            _workCts = null;
            cts.Dispose();
            ProgressPanel.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
        _results.Add(result);
        Installed |= result.Ok;
        ResultText.Text = result.Message;
        ResultText.Visibility = Visibility.Visible;
        if (_closeWhenIdle) Close();
    }

    private void UpdateProgress(ModFetcher.FetchProgress progress)
    {
        var downloading = progress.Phase == ModFetcher.FetchPhase.Downloading;
        PauseButton.IsEnabled = downloading;
        SkipButton.IsEnabled = downloading && _sourceId == ModFetcher.AutoSourceId;
        if (!downloading)
        {
            ProgressTitle.Text = ModFetcher.DescribePhase(progress);
            TransferBar.IsIndeterminate = true;
            ProgressDetail.Text = "";
            return;
        }
        ProgressTitle.Text = progress.SourceName;
        TransferBar.IsIndeterminate = progress.BytesTotal <= 0;
        if (progress.BytesTotal > 0)
            TransferBar.Value = Math.Clamp(progress.BytesDone * 100.0 / progress.BytesTotal, 0, 100);
        var size = ModFetcher.FormatSize(progress.BytesDone);
        if (progress.BytesTotal > 0) size += " / " + ModFetcher.FormatSize(progress.BytesTotal);
        ProgressDetail.Text = $"{size}  ·  {ModFetcher.FormatSpeed(progress.SpeedBps)}";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        var available = !busy && SupportedBuild;
        PackagePanel.IsEnabled = available;
        OptionsPanel.IsEnabled = available;
        ConfirmButton.IsEnabled = available;
        BrowseButton.IsEnabled = available;
        RetestButton.IsEnabled = available && _probeCts is null;
        CloseButton.Content = Loc.T(busy ? "Fetch.Cancel" : "Common.Close");
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_gate is null) return;
        if (_gate.Paused) _gate.Resume();
        else _gate.Pause();
        PauseButton.Content = Loc.T(_gate.Paused ? "Fetch.Resume" : "Fetch.Pause");
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _gate?.SkipCurrentSource();
        PauseButton.Content = Loc.T("Fetch.Pause");
        SkipButton.IsEnabled = false;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _workCts?.Cancel();
        _gate?.Resume();
        StopButton.IsEnabled = false;
        ProgressStatus.Text = Loc.T("Fetch.Cancelling");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            _closeWhenIdle = true;
            Stop_Click(this, new RoutedEventArgs());
            return;
        }
        _closing = true;
        _probeCts?.Cancel();
    }

    private string? DroppedArchive(DragEventArgs e)
    {
        if (_busy || !SupportedBuild || !e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        return files is { Length: 1 } && string.Equals(Path.GetExtension(files[0]), ".zip", StringComparison.OrdinalIgnoreCase)
            ? files[0] : null;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        var supported = DroppedArchive(e) is not null;
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        DropZone.Opacity = supported ? 0.7 : 1;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => DropZone.Opacity = 1;

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        var path = DroppedArchive(e);
        DropZone.Opacity = 1;
        e.Handled = true;
        if (path is not null) await ImportAsync(path);
    }
}
