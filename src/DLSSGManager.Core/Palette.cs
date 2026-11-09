namespace DLSSGManager;

/// <summary>
/// The theme keys used for game status and the mod-source badge.
///
/// Only names live here — the actual brushes come from the active theme dictionary. Keeping this free
/// of WPF types lets the deployment model reference a status colour without the presentation framework.
/// </summary>
public static class Palette
{
    // Game status, as shown in the list and the detail heading.
    public const string Ok = ThemeKeys.StatusOkBrush;
    public const string Warn = ThemeKeys.StatusWarnBrush;
    public const string Bad = ThemeKeys.StatusBadBrush;
    public const string Idle = ThemeKeys.StatusIdleBrush;

    // The mod-source badge.
    public const string BadgeOk = ThemeKeys.BadgeOkBackground;
    public const string BadgeOkText = ThemeKeys.BadgeOkText;
    public const string BadgeWarn = ThemeKeys.BadgeWarnBackground;
    public const string BadgeWarnText = ThemeKeys.BadgeWarnText;
    public const string BadgeBadText = ThemeKeys.DangerText;
}
