using System.IO;
using System.Text.Json;

namespace DLSSGManager;

/// <summary>Configuration snapshots contain values, not file hashes or runtime status.</summary>
public static class ConfigurationState
{
    public static string Applied(GameEntry game) => JsonSerializer.Serialize(new
    {
        game.RenderDir, game.PreferredProxy, game.Profile,
    });

    public static string Saved(GameEntry game) => JsonSerializer.Serialize(new
    {
        game.Name, game.ExePath, game.CompatibilityId, Configuration = Applied(game),
    });

    public static bool NeedsApply(GameEntry game) => game.Deployment is null ||
        game.Deployment.AppliedConfiguration != Applied(game);
}

public static class BuildCatalog
{
    public const string Default = "310.9";
    public const string Conservative = "310.1";
    public const string Marker = ".manager-build";
    public static string Normalize(string? value) => value == Conservative ? Conservative : Default;
    public static string SourceDirectory(string root, string build) => Normalize(build) == Default
        ? root : Path.Combine(root, "variants", Conservative);
    public static string RemotePath(string path, string build) => Normalize(build) == Conservative &&
        path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Conservative + "/" + path : path;
    public static string? ConfigurationError(GameProfile profile) => profile.RuntimeModel == Conservative &&
        (profile.MaxGeneratedFrames > 3 || profile.OptimizedTier > 1 || profile.Preset != "Auto")
        ? Loc.T("Manage.BuildUnsupported") : null;
}

/// <summary>Known, user-selectable recipes. Detection offers a suggestion; it never edits configuration.</summary>
public static class GameGuidance
{
    public static string Identify(GameEntry game)
    {
        if (game.CompatibilityId != "auto") return game.CompatibilityId;
        var text = game.Name + " " + game.RenderDir + " " + game.ExePath;
        if (text.Contains("怪物猎人荒野") || text.Contains("Monster Hunter Wilds", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MonsterHunterWilds", StringComparison.OrdinalIgnoreCase)) return "wilds";
        if (text.Contains("绝区零") || text.Contains("Zenless", StringComparison.OrdinalIgnoreCase)) return "zzz";
        if (text.Contains("异环") || text.Contains("Neverness", StringComparison.OrdinalIgnoreCase)) return "nte";
        return "none";
    }

    public static string Describe(GameEntry game)
    {
        var recipe = Identify(game);
        var names = recipe switch
        {
            "wilds" => new[] { "dinput8.dll", "openvr_api.dll", "openxr_loader.dll", "reframework" },
            "zzz" => new[] { "d3d12.dll" },
            "nte" => new[] { "d3d12.dll", "dinput8.dll", ModSource.IniName },
            _ => Array.Empty<string>(),
        };
        if (names.Length == 0) return Loc.T("Guide.None");
        var checks = names.Select(name => Loc.T(
            File.Exists(Path.Combine(game.RenderDir, name)) || Directory.Exists(Path.Combine(game.RenderDir, name))
                ? "Guide.Found" : "Guide.NotFound", name));
        var key = recipe switch { "wilds" => "Guide.wilds", "zzz" => "Guide.zzz", _ => "Guide.nte" };
        return Loc.T(key) + "\n" + string.Join("\n", checks) + "\n" + Loc.T("Guide.Verify");
    }
}
