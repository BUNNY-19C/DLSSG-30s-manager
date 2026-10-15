using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DLSSGManager;

/// <summary>
/// Swaps the application's colour resources at runtime.
///
/// Every colour in the interface is a <c>DynamicResource</c> lookup into the dictionary installed
/// here, so switching replaces the brushes and WPF re-resolves them — no window rebuild, and open
/// dialogs follow along because the lookup goes through the application-level dictionary.
///
/// Dynamic rather than static is the load-bearing choice: a <c>StaticResource</c> is resolved once
/// when the element is created, so a theme switch would leave most of the window in the old colours.
/// </summary>
public static class Theme
{
    private const string DarkSource = "Themes/Dark.xaml";
    private const string LightSource = "Themes/Light.xaml";

    /// <summary>Where Windows records the app colour mode chosen under Settings › Personalisation › Colours.</summary>
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    public static AppTheme Current { get; private set; } = AppTheme.Dark;

    /// <summary>The stored preference in force: "system", "dark" or "light".</summary>
    public static string Preference { get; private set; } = ThemeKeys.SystemPreference;

    public static void Apply(AppTheme theme)
    {
        var uri = new Uri(theme == AppTheme.Light ? LightSource : DarkSource, UriKind.Relative);

        // The theme lives at index 0; anything appended later stays after it.
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var replacement = new ResourceDictionary { Source = uri };

        if (dictionaries.Count == 0) dictionaries.Add(replacement);
        else dictionaries[0] = replacement;

        Current = theme;
        AppPaths.Log($"界面主题已切换为 {(theme == AppTheme.Light ? "浅色" : "深色")}");
    }

    /// <summary>Records a stored preference and applies the theme it stands for.</summary>
    public static void ApplyPreference(string? stored)
    {
        Preference = ThemeKeys.NormalizePreference(stored);
        Apply(ThemeKeys.Resolve(Preference, SystemUsesLightTheme()));
    }

    /// <summary>
    /// Re-reads the Windows app mode after the system announced a change. Returns true when the
    /// theme was switched, which only happens while the preference is "system" and the mode differs;
    /// Windows sends the notification several times per change, and the repeats must not redraw.
    /// </summary>
    public static bool FollowSystemChange()
    {
        if (Preference != ThemeKeys.SystemPreference) return false;

        var wanted = ThemeKeys.Resolve(Preference, SystemUsesLightTheme());
        if (wanted == Current) return false;

        Apply(wanted);
        return true;
    }

    /// <summary>
    /// The app mode Windows is in. A missing value means the setting was never changed, and the
    /// Windows default for apps is light.
    /// </summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Reads a brush from the active theme by key. Used by code that assigns colours directly, which
    /// cannot participate in the resource lookup — note that such code must re-run after a theme
    /// switch, so the caller does that explicitly.
    /// </summary>
    public static Brush Brush(string key)
    {
        var found = Application.Current?.TryFindResource(key);
        return found as Brush ?? Brushes.Transparent;
    }
}
