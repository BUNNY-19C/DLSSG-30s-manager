using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DLSSGManager;

/// <summary>Minimal INotifyPropertyChanged base so the WPF panels can bind straight to the models.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public enum GameStatus
{
    /// <summary>Nothing of ours is in the render directory.</summary>
    NotDeployed,
    /// <summary>Proxy DLL and INI are present and match the recorded hashes.</summary>
    Deployed,
    /// <summary>Files are present but no longer match what we wrote.</summary>
    Modified,
    /// <summary>We recorded an installation, but the files are gone.</summary>
    Missing,
    /// <summary>Files are present but the render directory could not be verified.</summary>
    Unknown,
}

/// <summary>The five INI keys documented in docs/NATIVE_INI.md. Everything else in the shipped INI is a comment.</summary>
public sealed class GameProfile : Observable
{
    private string _router = "SM86";
    private string _kernelImage = "PTX";
    private bool _hardwareBilinear;
    private bool _enabled = true;
    private bool _optimized = true;
    private string _preset = "Auto";
    private int _maxGeneratedFrames = 5;
    private int _logLevel = 1;
    private bool _diagnostics;

    /// <summary>0.3.0: frame generation on (bundled runtime) or off (the game's own DLSSG loads).</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>0.3.0: use the project's optimized kernels instead of the runtime's stock numerics.</summary>
    public bool Optimized { get => _optimized; set => Set(ref _optimized, value); }

    /// <summary>0.3.0: DLSS-G render preset. Auto lets the game or the driver profile decide.</summary>
    public string Preset { get => _preset; set => Set(ref _preset, value); }

    /// <summary>0.2.x only: SM86 for RTX 30 series, SM75 for RTX 20 series.</summary>
    public string Router { get => _router; set => Set(ref _router, value); }

    /// <summary>0.2.x only: PTX (driver JIT), Auto, or Cubin (exact match only).</summary>
    public string KernelImage { get => _kernelImage; set => Set(ref _kernelImage, value); }

    /// <summary>0.2.x only: 0 = exact output, 1 = optional approximate sampling (SM86 only).</summary>
    public bool HardwareBilinear { get => _hardwareBilinear; set => Set(ref _hardwareBilinear, value); }

    /// <summary>Capability limit 1/2/3/4/5, mapping to 2X/3X/4X/5X/6X. 0.3.0 raised the ceiling to 5.</summary>
    public int MaxGeneratedFrames { get => _maxGeneratedFrames; set => Set(ref _maxGeneratedFrames, value); }

    /// <summary>0 = off, 1 = errors, 2 = diagnostics, 3 = verbose.</summary>
    public int LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }

    /// <summary>Adds the optional [Diagnostics] section used when profiling the GPU pipeline.</summary>
    public bool Diagnostics { get => _diagnostics; set => Set(ref _diagnostics, value); }

    public GameProfile Clone() => new()
    {
        Router = Router,
        KernelImage = KernelImage,
        HardwareBilinear = HardwareBilinear,
        Enabled = Enabled,
        Optimized = Optimized,
        Preset = Preset,
        MaxGeneratedFrames = MaxGeneratedFrames,
        LogLevel = LogLevel,
        Diagnostics = Diagnostics,
    };

    public void CopyFrom(GameProfile other)
    {
        Router = other.Router;
        KernelImage = other.KernelImage;
        HardwareBilinear = other.HardwareBilinear;
        Enabled = other.Enabled;
        Optimized = other.Optimized;
        Preset = other.Preset;
        MaxGeneratedFrames = other.MaxGeneratedFrames;
        LogLevel = other.LogLevel;
        Diagnostics = other.Diagnostics;
    }
}

/// <summary>A file we displaced in the game directory, stored under the app's restore root.</summary>
public sealed class BackupItem
{
    public string FileName { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>A file a deployment wrote, with the hash recorded at write time.</summary>
public sealed class DeployedFile
{
    public string FileName { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Record of what we put into a game directory, so restore can prove it is removing our own files.</summary>
public sealed class DeploymentInfo
{
    public string ProxyName { get; set; } = "";
    public string ModVersion { get; set; } = "";
    public string DeployedAt { get; set; } = "";
    public string ProxySha256 { get; set; } = "";
    public string IniSha256 { get; set; } = "";
    /// <summary>Folder under the restore root holding displaced originals, empty when nothing was displaced.</summary>
    public string RestoreFolder { get; set; } = "";
    public List<BackupItem> Backups { get; set; } = new();

    /// <summary>
    /// Every file this deployment wrote, with its hash.
    ///
    /// The signature check covers the entries this project builds, but a proxy the user added themselves
    /// — a community d3d12.dll, say — has no signature to lean on, so these hashes are what let the
    /// entry-name scan recognise it on later checks. Empty on records written before this field existed;
    /// those deployments used the published entries, which the signature check still covers.
    /// </summary>
    public List<DeployedFile> Files { get; set; } = new();
}

public sealed class GameEntry : Observable
{
    private string _name = "";
    private string _renderDir = "";
    private string _exePath = "";
    private string _preferredProxy = "自动";
    private string _notes = "";
    private GameStatus _status = GameStatus.Unknown;
    private string _statusDetail = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get => _name; set => Set(ref _name, value); }

    /// <summary>Directory holding the real rendering executable; the proxy DLL and INI go here.</summary>
    public string RenderDir { get => _renderDir; set => Set(ref _renderDir, value); }

    /// <summary>Optional executable used by the launch button.</summary>
    public string ExePath { get => _exePath; set => Set(ref _exePath, value); }

    /// <summary>"自动" or one of the five proxy entry names.</summary>
    public string PreferredProxy { get => _preferredProxy; set => Set(ref _preferredProxy, value); }

    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public GameProfile Profile { get; set; } = new();
    public DeploymentInfo? Deployment { get; set; }

    private ProtectionReport? _protection;

    /// <summary>
    /// Anti-cheat scan result, refreshed on every deploy and status check. Raises change
    /// notifications for itself and the banner properties derived from it.
    /// </summary>
    [JsonIgnore]
    public ProtectionReport? Protection
    {
        get => _protection;
        set
        {
            if (!Set(ref _protection, value)) return;
            Raise(nameof(HasKernelAntiCheat));
            Raise(nameof(ShowAntiCheatBanner));
            Raise(nameof(AntiCheatTitle));
            Raise(nameof(AntiCheatBody));
        }
    }

    [JsonIgnore]
    public bool HasKernelAntiCheat => Protection?.HasKernelAntiCheat == true;

    /// <summary>
    /// The banner only appears for games the mod cannot work on. A clean game gets no banner at all —
    /// an "all clear" notice is noise, and any wording next to a warning icon reads as bad news.
    ///
    /// Exposed as a bool rather than a Visibility so this model stays free of WPF types; the view
    /// converts it. That keeps the deployment logic testable from a plain console project.
    /// </summary>
    [JsonIgnore]
    public bool ShowAntiCheatBanner => Protection?.HasKernelAntiCheat == true;

    [JsonIgnore] public string AntiCheatTitle => Protection is null ? "" : "⚠ " + Protection.Summary;

    [JsonIgnore]
    public string AntiCheatBody => Protection?.HasKernelAntiCheat == true
        ? Loc.T("Anti.BannerBody")
        : "";

    [JsonIgnore] public GameStatus Status { get => _status; set { if (Set(ref _status, value)) { Raise(nameof(StatusText)); Raise(nameof(StatusColor)); } } }

    [JsonIgnore] public string StatusDetail { get => _statusDetail; set => Set(ref _statusDetail, value); }

    [JsonIgnore]
    public string StatusText => Status switch
    {
        GameStatus.Deployed => Loc.T("Status.Deployed"),
        GameStatus.Modified => Loc.T("Status.Modified"),
        GameStatus.Missing => Loc.T("Status.Missing"),
        GameStatus.NotDeployed => Loc.T("Status.NotDeployed"),
        _ => Loc.T("Status.NotChecked"),
    };

    /// <summary>
    /// Theme key for this status, resolved to a brush by <see cref="ThemeBrushConverter"/>.
    ///
    /// A key rather than a colour: the two themes need different values (the dark theme's green is
    /// unreadable on white), and going through the converter means a theme switch updates the list
    /// without the model knowing about brushes.
    /// </summary>
    [JsonIgnore]
    public string StatusColor => Status switch
    {
        GameStatus.Deployed => Palette.Ok,
        GameStatus.Modified => Palette.Warn,
        GameStatus.Missing => Palette.Bad,
        _ => Palette.Idle,
    };

    [JsonIgnore] public string Subtitle => string.IsNullOrWhiteSpace(RenderDir) ? Loc.T("Detail.NoPath") : RenderDir;

    [JsonIgnore]
    public string DeploymentSummary => Deployment is null
        ? Loc.T("Status.NotDeployedDetail")
        : $"{Deployment.ProxyName} · Mod {Deployment.ModVersion} · {Deployment.DeployedAt}";

    /// <summary>
    /// Re-raises the change notifications for properties whose text is produced from the string table.
    ///
    /// These are computed properties, so a language change does not reach them: the XAML binding for
    /// <see cref="StatusText"/> watches this object, not <see cref="Loc"/>, and would keep showing the
    /// text from the previous language. Called for every game when the language changes.
    /// </summary>
    public void RaiseLocalizedText()
    {
        Raise(nameof(StatusText));
        Raise(nameof(StatusDetail));
        Raise(nameof(Subtitle));
        Raise(nameof(DeploymentSummary));
        Raise(nameof(AntiCheatTitle));
        Raise(nameof(AntiCheatBody));
    }
}

/// <summary>Everything persisted to %APPDATA%\DLSSGManager\library.json.</summary>
public sealed class AppData
{
    /// <summary>
    /// The single source of truth for the game list: the UI binds to this very collection, so anything
    /// the user adds is guaranteed to be what <see cref="LibraryStore.Save"/> writes out.
    /// </summary>
    public ObservableCollection<GameEntry> Games { get; set; } = new();

    public string ModSourcePath { get; set; } = "";
    public string LastScanRoot { get; set; } = "";

    /// <summary>Interface language code; see <see cref="Languages"/>.</summary>
    public string InterfaceLanguage { get; set; } = Languages.ChineseSimplified;

    /// <summary>Colour scheme: "dark" or "light". See <see cref="Theme"/>.</summary>
    public string InterfaceTheme { get; set; } = "dark";
    /// <summary>Detected GPU name, cached so the UI shows something before the probe finishes.</summary>
    public string GpuName { get; set; } = "";
    public string GpuDriver { get; set; } = "";
    public string RecommendedRouter { get; set; } = "SM86";
}
