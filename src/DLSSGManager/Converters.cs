using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DLSSGManager;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

public sealed class NotEmptyToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Resolves a theme resource key to the brush it names.
///
/// Used where a model exposes a status colour as a key — a binding cannot look up a resource itself,
/// and a bound Brush would be captured once and go stale on a theme switch.
/// </summary>
public sealed class ThemeBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Theme.Brush(key) : System.Windows.Media.Brushes.Transparent;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// A combo entry pairing a stable value with its label.
///
/// A label given as a string-table key follows the interface language in place. The closed box of a
/// combo shows the selected item itself, so an entry whose text notifies repaints there as well.
/// Rebuilding the items instead would clear and re-set the selection, pushing a transient null
/// through every TwoWay binding on SelectedValue.
/// </summary>
public sealed class Choice<T> : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs TextChanged = new(nameof(Text));

    private readonly string? _key;
    private readonly string _fixedText;

    private Choice(T value, string? key, string fixedText)
    {
        Value = value;
        _key = key;
        _fixedText = fixedText;

        // Weak: an entry must not be kept alive by the application-wide string source.
        if (key is not null) PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
    }

    public static Choice<T> Localized(T value, string key) => new(value, key, "");

    /// <summary>A label that is the same in every language, such as a language's own name.</summary>
    public static Choice<T> Fixed(T value, string text) => new(value, null, text);

    public T Value { get; }

    public string Text => _key is null ? _fixedText : Loc.T(_key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Text;

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, TextChanged);
}
