using System.ComponentModel;
using System.Globalization;

namespace DLSSGManager;

/// <summary>
/// Runtime string lookup with live language switching.
///
/// Strings produced in code go through <see cref="T"/>. Text set in XAML binds through the indexer via
/// <c>TrExtension</c> (declared in <c>TrExtension.cs</c>, which is WPF-only so this file stays usable
/// from the console test project); raising a change for that indexer makes every bound element re-read
/// its string, so switching language updates the open window without a restart.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static string _current = Languages.ChineseSimplified;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Binding target for XAML. "Item[]" is the name WPF uses for an indexer.</summary>
    public string this[string key] => T(key);

    public static string Current => _current;

    public static void SetLanguage(string language)
    {
        var normalized = Languages.Normalize(language);
        if (string.Equals(_current, normalized, StringComparison.Ordinal)) return;

        _current = normalized;
        AppPaths.Log($"界面语言已切换为 {Languages.DisplayName(normalized)}");

        // One notification refreshes every bound string in the window.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Looks up a key in the current language, falling back to the other table and then to the key
    /// itself. Returning the key keeps a missing translation visible instead of blanking the UI.
    /// </summary>
    public static string T(string key)
    {
        var table = Strings.For(_current);
        if (table.TryGetValue(key, out var text)) return text;

        var fallback = Strings.For(Languages.English);
        if (fallback.TryGetValue(key, out var english)) return english;

        AppPaths.Log($"缺少翻译：{key}");
        return key;
    }

    /// <summary>
    /// Formatted lookup, for strings containing placeholders.
    ///
    /// Invariant culture on purpose: these strings are now formatted from pool threads during
    /// background operations, and a per-culture number format would make the same log line render
    /// differently depending on which thread picked it up.
    /// </summary>
    public static string T(string key, params object?[] args)
    {
        var template = T(key);
        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            // A mismatched placeholder count should not crash the app over a status line.
            AppPaths.Log($"翻译占位符不匹配：{key}");
            return template;
        }
    }

    /// <summary>
    /// Separator for joining names in a list. Chinese uses an enumeration comma, English a
    /// comma-space; hard-coding either looks wrong in the other language.
    /// </summary>
    public static string ListSeparator => Current == Languages.English ? ", " : "、";

    /// <summary>Joins the given values with the separator of the active language.</summary>
    public static string Join(IEnumerable<string> values) => string.Join(ListSeparator, values);
}

/// <summary>The languages the interface can be shown in.</summary>
public static class Languages
{
    public const string ChineseSimplified = "zh-Hans";
    public const string English = "en";

    public static readonly string[] All = { ChineseSimplified, English };

    /// <summary>Maps anything unrecognised to the default, so a bad config value cannot blank the UI.</summary>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return ChineseSimplified;

        var value = language.Trim();

        // Accept the common spellings users and other tools produce.
        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return English;
        if (value.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return ChineseSimplified;

        AppPaths.Log($"未识别的语言 «{language}»，回退到简体中文");
        return ChineseSimplified;
    }

    public static string DisplayName(string language) => Normalize(language) switch
    {
        English => "English",
        _ => "简体中文",
    };
}
