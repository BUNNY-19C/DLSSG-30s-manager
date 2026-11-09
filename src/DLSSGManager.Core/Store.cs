using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>App-owned locations. Nothing here ever lives inside a game directory except the two deployed files.</summary>
public static class AppPaths
{
    /// <summary>
    /// Overrides the data folder, keeping a separate library and log at the configured location.
    /// </summary>
    public const string RootOverrideVariable = "DLSSGMANAGER_HOME";

    /// <summary>
    /// Resolved on every read, so a process that sets <see cref="RootOverrideVariable"/> after start-up
    /// (the elevated helper, which receives the folder from the manager) uses it from then on.
    /// </summary>
    public static string Root => ResolveRoot();

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(RootOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            try
            {
                return Path.GetFullPath(overridden);
            }
            catch
            {
                // A malformed override falls back to the standard location.
            }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DLSSGManager");
    }

    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string RestoreRoot => Path.Combine(Root, "restore");
    public static string LogFile => Path.Combine(Root, "manager.log");

    /// <summary>Mod files shipped next to the app, used when this copy is portable.</summary>
    public static string BundledModDir => Path.Combine(AppContext.BaseDirectory, "mod");

    /// <summary>
    /// Per-user mod folder, used when the app is installed somewhere read-only such as
    /// <c>C:\Program Files</c>. Keeps the download working without requiring elevation.
    /// </summary>
    public static string UserModDir => Path.Combine(Root, "mod");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RestoreRoot);
    }

    private static readonly object FileLock = new();

    public static void Log(string message)
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.AppendAllText(LogFile,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never take the app down.
            }
        }
    }
}

/// <summary>What happened while reading the library, beyond the data itself.</summary>
public enum LibraryLoadOutcome
{
    /// <summary>The library was read normally.</summary>
    Loaded,
    /// <summary>No library existed yet; a new one starts empty.</summary>
    Created,
    /// <summary>
    /// The library could not be read; it was set aside and the previous good copy was loaded instead.
    /// </summary>
    RecoveredFromBackup,
    /// <summary>
    /// The library could not be read and was set aside; no usable backup existed, so the library
    /// starts empty. The unreadable file is kept for manual recovery.
    /// </summary>
    Quarantined,
    /// <summary>
    /// The library could not be read and could not be moved out of the way either. Saving is blocked
    /// for this session so the only copy of the deployment records is never overwritten.
    /// </summary>
    ReadOnly,
}

/// <summary>Result of <see cref="LibraryStore.LoadWithStatus"/>.</summary>
public sealed record LibraryLoadResult(AppData Data, LibraryLoadOutcome Outcome, string? Error = null, string? SetAsidePath = null);

public static class LibraryStore
{
    /// <summary>Suffix of the previous good copy, rotated on every successful save.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Reads the library from an explicit path or the app's default data location.
    /// </summary>
    public static AppData Load(string? path = null) => LoadWithStatus(path).Data;

    /// <summary>
    /// Reads the library and reports whether it had to be recovered.
    ///
    /// The library holds the only record of which files were written into game folders and where the
    /// displaced originals are stored. An unreadable file must therefore never be replaced by an empty
    /// library: it is renamed aside (and the last good copy loaded when there is one), and if it cannot
    /// even be renamed, saving is blocked for the session.
    /// </summary>
    public static LibraryLoadResult LoadWithStatus(string? path = null)
    {
        var file = path ?? AppPaths.LibraryFile;
        if (!File.Exists(file))
        {
            // A crash between writing the backup and replacing the file can leave only the backup.
            if (TryRead(file + BackupSuffix, out var orphan, out _) && orphan is not null)
                return new LibraryLoadResult(orphan, LibraryLoadOutcome.RecoveredFromBackup);
            return new LibraryLoadResult(new AppData(), LibraryLoadOutcome.Created);
        }

        if (TryRead(file, out var data, out var error) && data is not null)
            return new LibraryLoadResult(data, LibraryLoadOutcome.Loaded);

        AppPaths.Log("读取库文件失败: " + error);

        var aside = $"{file}.corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
        try
        {
            File.Move(file, aside);
            AppPaths.Log("已将无法读取的库文件另存为: " + aside);
        }
        catch (Exception ex)
        {
            AppPaths.Log("无法移开损坏的库文件，本次会话禁止保存: " + ex.Message);
            var locked = new AppData { SaveBlockedReason = error ?? ex.Message };
            return new LibraryLoadResult(locked, LibraryLoadOutcome.ReadOnly, error);
        }

        if (TryRead(file + BackupSuffix, out var backup, out _) && backup is not null)
            return new LibraryLoadResult(backup, LibraryLoadOutcome.RecoveredFromBackup, error, aside);

        return new LibraryLoadResult(new AppData(), LibraryLoadOutcome.Quarantined, error, aside);
    }

    private static bool TryRead(string file, out AppData? data, out string? error)
    {
        data = null;
        error = null;
        try
        {
            if (!File.Exists(file))
            {
                error = "missing";
                return false;
            }

            var json = File.ReadAllText(file, Encoding.UTF8);
            data = System.Text.Json.JsonSerializer.Deserialize<AppData>(json, JsonOptions);
            if (data is null)
            {
                error = "empty document";
                return false;
            }

            // Only the current runtime is selectable; deployment records still describe
            // the files actually installed so existing restore records remain accurate.
            data.DownloadBuild = BuildCatalog.Default;
            data.Games ??= new();
            foreach (var g in data.Games) Normalize(g);
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            error = ex.Message;
            return false;
        }
    }

    public static string Serialize(AppData data) => System.Text.Json.JsonSerializer.Serialize(data, JsonOptions);

    /// <summary>
    /// Writes the library atomically, keeping the previous version as <c>library.json.bak</c>.
    /// Refuses when the session loaded in read-only mode.
    /// </summary>
    public static OpResult Save(AppData data, string? path = null)
    {
        var result = new OpResult();
        if (data.SaveBlockedReason is not null)
        {
            result.Fail(Loc.T("Library.SaveBlocked", data.SaveBlockedReason));
            return result;
        }

        var file = path ?? AppPaths.LibraryFile;
        try
        {
            AppPaths.EnsureCreated();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = Serialize(data);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));

            if (File.Exists(file))
                File.Replace(tmp, file, file + BackupSuffix, ignoreMetadataErrors: true);
            else
                File.Move(tmp, file);
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存库文件失败: " + ex.Message);
            result.Fail(ex.Message);
        }
        return result;
    }

    private static void Normalize(GameEntry g)
    {
        g.Profile = g.Profile ?? new GameProfile();
        g.Profile.RuntimeModel = BuildCatalog.Default;
        g.Profile.Router = NormalizeRouter(g.Profile.Router);
        g.Profile.KernelImage = NormalizeKernel(g.Profile.KernelImage);
        g.Profile.Preset = NormalizePreset(g.Profile.Preset);
        g.Profile.Engine = string.Equals(g.Profile.Engine, "XeSS", StringComparison.OrdinalIgnoreCase) ? "XeSS" : "DLSS";
        g.Profile.FlowResolution = g.Profile.FlowResolution?.Trim().ToLowerInvariant() switch
        {
            "coarse" => "Coarse", "fine" => "Fine", "coarseat4k" => "CoarseAt4K", _ => "Middle",
        };
        g.Profile.HudlessMode = g.Profile.HudlessMode?.Trim().ToLowerInvariant() switch
        {
            "1" => "1", "0" => "0", _ => "Auto",
        };
        g.Profile.SuperResolutionMode = g.Profile.SuperResolutionMode?.Trim().ToUpperInvariant() switch
        {
            "M-INT8" => "M-INT8", "L-INT8" => "L-INT8", "M-FP16" => "M-FP16", "L-FP16" => "L-FP16",
            "ON" => "On", _ => "Off",
        };
        g.Profile.XeSSMaxGeneratedFrames = Math.Clamp(g.Profile.XeSSMaxGeneratedFrames, 1, 3);

        // Libraries written before 0.3.3 stored Optimized as a boolean; migrate once so the tier
        // becomes the single source of truth (true = bit-identical speedups = tier 1).
        if (g.Profile.Optimized is bool legacy)
        {
            g.Profile.OptimizedTier = legacy ? 1 : 0;
            g.Profile.Optimized = null;
        }
        g.Profile.OptimizedTier = Math.Clamp(g.Profile.OptimizedTier, 0, 3);

        g.Profile.MaxGeneratedFrames = Math.Clamp(g.Profile.MaxGeneratedFrames, 1, 5);
        g.Profile.LogLevel = Math.Clamp(g.Profile.LogLevel, 0, 3);
        if (g.Deployment is not null)
        {
            g.Deployment.Backups ??= new List<BackupItem>();
            g.Deployment.Files ??= new List<DeployedFile>();
        }
    }

    /// <summary>Auto (let the game or driver profile decide), A (force UI recomposition off) or B (on).</summary>
    public static string NormalizePreset(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "A" => "A",
        "B" => "B",
        "C" => "C",
        _ => "Auto",
    };

    public static string NormalizeRouter(string? value) =>
        string.Equals(value, "SM75", StringComparison.OrdinalIgnoreCase) ? "SM75" : "SM86";

    public static string NormalizeKernel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cubin" => "Cubin",
        "auto" => "Auto",
        _ => "PTX",
    };

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>
/// The shipped dlssg_sm86.ini is mostly comments; it is used verbatim as a template and only the keys it
/// actually contains are rewritten.
///
/// That last part is deliberate: upstream changed the schema between releases (0.3.0 has Enabled,
/// Optimized and Preset; the 0.2.x line had Router, KernelImage and HardwareBilinear). Writing keys a
/// build does not know would leave dead settings in every deployed INI, so a key that the template does
/// not define is simply not written.
/// </summary>
public static class IniTemplate
{
    /// <summary>
    /// Values for the keys the manager exposes. Harmless to include all of them: only the ones present in
    /// the template are written.
    /// </summary>
    private static Dictionary<string, string> Values(GameProfile p) => new(StringComparer.OrdinalIgnoreCase)
    {
        // 0.3.0+ (Optimized became a 0-3 consistency tier in 0.3.3; the key name is unchanged)
        ["Enabled"] = p.Enabled ? "1" : "0",
        ["Optimized"] = Math.Clamp(p.OptimizedTier, 0, 3).ToString(),
        ["Preset"] = p.Preset,
        ["MaxGeneratedFrames"] = Math.Clamp(p.MaxGeneratedFrames, 1, 5).ToString(),
        ["Level"] = Math.Clamp(p.LogLevel, 0, 3).ToString(),
        ["Engine"] = p.Engine,
        ["FlowResolution"] = p.FlowResolution,
        ["UseHudless"] = p.HudlessMode,

        // 0.2.x (kept so an older payload still receives the user's settings)
        ["Router"] = p.Router,
        ["KernelImage"] = p.KernelImage,
        ["HardwareBilinear"] = p.HardwareBilinear ? "1" : "0",
    };

    public static string Render(string templateText, GameProfile p, bool? supports041 = null)
    {
        var text = string.IsNullOrWhiteSpace(templateText) ? FallbackTemplate(p) : templateText;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var values = Values(p);
        var section = "";
        var modern = text.Contains("[DLSSG]", StringComparison.OrdinalIgnoreCase);
        var schema041 = supports041 ?? ModSource.Has041Schema(text);
        if (schema041 && p.SuperResolutionMode == "On")
            throw new InvalidOperationException(Loc.T("Manage.SelectSr041"));
        values["Preset"] = schema041 && p.Preset == "Auto" ? "A" : p.Preset;
        values["UseHudless"] = !schema041 && p.HudlessMode == "Auto" ? "1" : p.HudlessMode;

        for (var i = 0; i < lines.Count; i++)
        {
            var heading = Regex.Match(lines[i], @"^\s*\[([^\]]+)\]");
            if (heading.Success) section = heading.Groups[1].Value;
            var m = Regex.Match(lines[i], @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            if (modern && key.Equals("Mode", StringComparison.OrdinalIgnoreCase))
            {
                if (section.Equals("SuperResolution", StringComparison.OrdinalIgnoreCase))
                    lines[i] = "Mode=" + p.SuperResolutionMode;
                continue;
            }
            if (modern)
            {
                var expectedSection = key.ToLowerInvariant() switch
                {
                    "enabled" => "General", "engine" or "maxgeneratedframes" => "FrameGeneration",
                    "optimized" or "preset" => "DLSSG", "flowresolution" or "usehudless" => "XeSSFG",
                    "level" => "Logging", _ => "",
                };
                if (!section.Equals(expectedSection, StringComparison.OrdinalIgnoreCase)) continue;
            }
            if (values.TryGetValue(key, out var v))
                lines[i] = $"{key}={(key.Equals("MaxGeneratedFrames", StringComparison.OrdinalIgnoreCase) && modern && p.UsesXeSS
                    ? (schema041 ? Math.Clamp(p.XeSSMaxGeneratedFrames, 1, 3).ToString() : "1") : v)}";
        }

        if (modern)
        {
            var logging = lines.FindIndex(l => l.Trim().Equals("[Logging]", StringComparison.OrdinalIgnoreCase));
            if (logging >= 0)
            {
                var end = lines.FindIndex(logging + 1, l => l.TrimStart().StartsWith('['));
                if (end < 0) end = lines.Count;
                var timing = lines.FindIndex(logging + 1, end - logging - 1,
                    l => Regex.IsMatch(l, @"^\s*FrameTiming\s*=", RegexOptions.IgnoreCase));
                var value = "FrameTiming=" + (p.FrameTiming ? "1" : "0");
                if (timing >= 0) lines[timing] = value;
                else lines.Insert(logging + 1, value);
            }
        }

        var body = string.Join("\r\n", lines);

        if (!modern && p.Diagnostics && !body.Contains("[Diagnostics]", StringComparison.OrdinalIgnoreCase))
        {
            // A 0.2.x profiling section. 0.3.0 moved the equivalent knobs into its README and ignores an
            // unknown section, so appending it stays harmless.
            body += "\r\n\r\n[Diagnostics]\r\n; 记录异步 GPU 计时；PipelineSteps=1 会明显影响性能，仅用于剖析。\r\nPerformance=1\r\nPipelineSteps=0\r\n";
        }

        return body.TrimEnd() + "\r\n";
    }

    /// <summary>
    /// Used only when no template could be read at all, so it mirrors the current upstream schema.
    /// </summary>
    private static string FallbackTemplate(GameProfile p) => string.Join("\r\n", new[]
    {
        $"; Generated by DLSSG 30 系管理器 on {DateTime.Now:yyyy-MM-dd HH:mm:ss}. Restart the game after changing this file.",
        "[General]",
        "Enabled=1",
        "",
        "[FrameGeneration]",
        "Engine=DLSS",
        $"MaxGeneratedFrames={Math.Clamp(p.MaxGeneratedFrames, 1, 5)}",
        "[DLSSG]",
        "Optimized=1",
        "Preset=A",
        "[XeSSFG]",
        "FlowResolution=Middle",
        "UseHudless=Auto",
        "[Runtime]",
        "Mode=Bundled",
        "[SuperResolution]",
        "Mode=Off",
        "",
        "[Logging]",
        $"Level={Math.Clamp(p.LogLevel, 0, 3)}",
    });
}
