using System.IO;
using System.Text.Json;

namespace DLSSGManager;

/// <summary>Configuration snapshots contain values, not file hashes or runtime status.</summary>
public static class ConfigurationState
{
    public static string Applied(GameEntry game) => Snapshot(game.RenderDir, game.PreferredProxy, game.Profile);

    public static string Saved(GameEntry game) => JsonSerializer.Serialize(new
    {
        game.Name, game.ExePath, Configuration = Applied(game),
    });

    public static bool NeedsApply(GameEntry game)
    {
        var recorded = game.Deployment?.AppliedConfiguration;
        if (string.IsNullOrEmpty(recorded)) return true;
        var current = Applied(game);
        return recorded != current && Reread(recorded) != current;
    }

    private static string Snapshot(string renderDir, string preferredProxy, GameProfile profile) =>
        JsonSerializer.Serialize(new { RenderDir = renderDir, PreferredProxy = preferredProxy, Profile = profile });

    /// <summary>
    /// A snapshot recorded before a setting existed has no value for it. Read back through today's types
    /// the setting takes its default, so adding one does not mark every deployed game as changed: the
    /// deployed INI never set it either, and the defaults follow upstream's.
    /// </summary>
    private static string? Reread(string recorded)
    {
        try
        {
            var old = JsonSerializer.Deserialize<Recorded>(recorded);
            return old is null ? null : Snapshot(old.RenderDir, old.PreferredProxy, old.Profile);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Recorded
    {
        public string RenderDir { get; set; } = "";
        public string PreferredProxy { get; set; } = "";
        public GameProfile Profile { get; set; } = new();
    }
}

public static class BuildCatalog
{
    public const string Default = "310.9";
    public const string BundledRuntimeVersion = "310.9.16";
    public const string Conservative = "310.1";
    public const string Marker = ".manager-build";
    public static string Normalize(string? value) => value == Conservative ? Conservative : Default;
    /// <summary>
    /// The folder holding the payload to deploy from: the active release under a versioned mod root
    /// (<see cref="ModStore"/>), or the root itself for the older flat layout. Downloads go to the
    /// root, never here.
    /// </summary>
    public static string SourceDirectory(string root, string build) => Normalize(build) == Default
        ? ModStore.ActiveDirectory(root) : Path.Combine(root, "variants", Conservative);
    public static string RemotePath(string path, string build) => Normalize(build) == Conservative &&
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Conservative + "/" + path : path;
    public static string? ConfigurationError(GameProfile profile) => profile.RuntimeModel == Conservative &&
        (profile.MaxGeneratedFrames > 3 || profile.OptimizedTier > 1 || profile.Preset != "Auto")
        ? Loc.T("Manage.BuildUnsupported") : null;
}
