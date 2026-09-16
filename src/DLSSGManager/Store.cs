using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>App-owned locations. Nothing here ever lives inside a game directory except the two deployed files.</summary>
public static class AppPaths
{
    /// <summary>
    /// Overrides the data folder. Set by the test harness so its deliberately-corrupt fixtures and
    /// download logs stay out of the user's real library and log.
    /// </summary>
    public const string RootOverrideVariable = "DLSSGMANAGER_HOME";

    public static string Root { get; } = ResolveRoot();

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

public static class LibraryStore
{
    /// <summary>
    /// Reads the library. <paramref name="path"/> exists so tests can round-trip against a scratch file
    /// instead of the user's real library.
    /// </summary>
    public static AppData Load(string? path = null)
    {
        var file = path ?? AppPaths.LibraryFile;
        try
        {
            if (File.Exists(file))
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                var data = System.Text.Json.JsonSerializer.Deserialize<AppData>(json, JsonOptions);
                if (data is not null)
                {
                    foreach (var g in data.Games) Normalize(g);
                    return data;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取库文件失败: " + ex.Message);
        }

        return new AppData();
    }

    public static void Save(AppData data, string? path = null)
    {
        var file = path ?? AppPaths.LibraryFile;
        try
        {
            AppPaths.EnsureCreated();
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var json = System.Text.Json.JsonSerializer.Serialize(data, JsonOptions);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("保存库文件失败: " + ex.Message);
        }
    }

    private static void Normalize(GameEntry g)
    {
        g.Profile = g.Profile ?? new GameProfile();
        g.Profile.Router = NormalizeRouter(g.Profile.Router);
        g.Profile.KernelImage = NormalizeKernel(g.Profile.KernelImage);
        g.Profile.Preset = NormalizePreset(g.Profile.Preset);
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
        // 0.3.0
        ["Enabled"] = p.Enabled ? "1" : "0",
        ["Optimized"] = p.Optimized ? "1" : "0",
        ["Preset"] = p.Preset,
        ["MaxGeneratedFrames"] = Math.Clamp(p.MaxGeneratedFrames, 1, 5).ToString(),
        ["Level"] = Math.Clamp(p.LogLevel, 0, 3).ToString(),

        // 0.2.x (kept so an older payload still receives the user's settings)
        ["Router"] = p.Router,
        ["KernelImage"] = p.KernelImage,
        ["HardwareBilinear"] = p.HardwareBilinear ? "1" : "0",
    };

    public static string Render(string templateText, GameProfile p)
    {
        var text = string.IsNullOrWhiteSpace(templateText) ? FallbackTemplate(p) : templateText;
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

        var values = Values(p);

        for (var i = 0; i < lines.Count; i++)
        {
            var m = Regex.Match(lines[i], @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            if (values.TryGetValue(key, out var v))
                lines[i] = $"{key}={v}";
        }

        var body = string.Join("\r\n", lines);

        if (p.Diagnostics && !body.Contains("[Diagnostics]", StringComparison.OrdinalIgnoreCase))
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
        "Optimized=1",
        $"MaxGeneratedFrames={Math.Clamp(p.MaxGeneratedFrames, 1, 5)}",
        "",
        "[Logging]",
        $"Level={Math.Clamp(p.LogLevel, 0, 3)}",
    });
}
