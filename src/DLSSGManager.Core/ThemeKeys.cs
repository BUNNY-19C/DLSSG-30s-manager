namespace DLSSGManager;

/// <summary>Which colour scheme the interface uses.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>
/// Keys into the theme dictionaries.
///
/// Kept apart from <see cref="Theme"/> so key names do not depend on WPF types.
///
/// The two dictionaries (<c>Themes/Dark.xaml</c>, <c>Themes/Light.xaml</c>) must define exactly the
/// same keys so switching themes can resolve every resource.
/// </summary>
public static class ThemeKeys
{
    // Window and containers
    public const string WindowBackground = "WindowBackground";
    public const string PanelBackground = "PanelBackground";
    public const string CardBackground = "CardBackground";
    public const string LogBackground = "LogBackground";
    public const string PopupBackground = "PopupBackground";

    // Inputs
    public const string InputBackground = "InputBackground";
    public const string InputBorderBrush = "InputBorderBrush";

    // Lines and hover
    public const string LineBrush = "LineBrush";
    public const string HoverBackground = "HoverBackground";
    public const string ListHoverBackground = "ListHoverBackground";
    public const string ListSelectedBackground = "ListSelectedBackground";

    // Text
    public const string TextPrimary = "TextPrimary";
    public const string TextMuted = "TextMuted";
    public const string TextFaint = "TextFaint";
    public const string TextSection = "TextSection";
    public const string TextLogTitle = "TextLogTitle";
    public const string TextOnLog = "TextOnLog";

    // Accent and buttons
    public const string AccentBrush = "AccentBrush";
    public const string ButtonBackground = "ButtonBackground";
    public const string ButtonBorderBrush = "ButtonBorderBrush";
    public const string PrimaryBackground = "PrimaryBackground";
    public const string PrimaryBorderBrush = "PrimaryBorderBrush";
    public const string PrimaryHoverBackground = "PrimaryHoverBackground";
    public const string OnPrimaryText = "OnPrimaryText";
    public const string DangerBackground = "DangerBackground";
    public const string DangerBorderBrush = "DangerBorderBrush";
    public const string DangerText = "DangerText";

    // Badges
    public const string BadgeOkBackground = "BadgeOkBackground";
    public const string BadgeOkText = "BadgeOkText";
    public const string BadgeWarnBackground = "BadgeWarnBackground";
    public const string BadgeWarnText = "BadgeWarnText";

    // Anti-cheat warning banner
    public const string WarnBannerBackground = "WarnBannerBackground";
    public const string WarnBannerBorder = "WarnBannerBorder";
    public const string WarnBannerTitle = "WarnBannerTitle";
    public const string WarnBannerBody = "WarnBannerBody";

    // Game status
    public const string StatusOkBrush = "StatusOkBrush";
    public const string StatusWarnBrush = "StatusWarnBrush";
    public const string StatusBadBrush = "StatusBadBrush";
    public const string StatusIdleBrush = "StatusIdleBrush";

    /// <summary>Stored preference meaning "use whatever Windows uses for apps".</summary>
    public const string SystemPreference = "system";

    /// <summary>Parses a stored theme name; anything unrecognised falls back to dark.</summary>
    public static AppTheme Parse(string? stored) =>
        string.Equals(stored, "light", StringComparison.OrdinalIgnoreCase) ? AppTheme.Light : AppTheme.Dark;

    /// <summary>The value written to the library file, matching <see cref="Parse"/>.</summary>
    public static string StorageValue(AppTheme theme) => theme == AppTheme.Light ? "light" : "dark";

    /// <summary>A stored preference in canonical form: "system", "dark" or "light".</summary>
    public static string NormalizePreference(string? stored) =>
        string.Equals(stored, SystemPreference, StringComparison.OrdinalIgnoreCase) ? SystemPreference : StorageValue(Parse(stored));

    /// <summary>The theme a preference stands for, given what Windows currently uses for apps.</summary>
    public static AppTheme Resolve(string? preference, bool systemUsesLight) =>
        NormalizePreference(preference) == SystemPreference
            ? systemUsesLight ? AppTheme.Light : AppTheme.Dark
            : Parse(preference);
}
