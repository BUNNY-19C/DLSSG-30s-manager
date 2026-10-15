using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;

namespace DLSSGManager;

/// <summary>
/// XAML markup extension resolving a key through <see cref="Loc"/>, e.g.
/// <c>Text="{loc:Tr Main.ScanSteam}"</c>.
///
/// Returns a binding rather than a fixed string so that changing the language propagates to the live
/// window: <see cref="Loc.SetLanguage"/> raises one indexer notification and every binding re-reads its
/// entry in that same pass. Split into its own file because it is the only part of the localisation
/// layer that needs WPF, keeping <c>Localization.cs</c> independent of the presentation framework.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // A combo's closed box shows a copy of the selected ComboBoxItem's Content, taken when the
        // selection was made, so a bound string there stays in the language it was selected in. The
        // content becomes a live object instead, which that copy shares.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget
            {
                TargetObject: ComboBoxItem,
                TargetProperty: DependencyProperty property,
            } && property == ContentControl.ContentProperty)
        {
            return new LocalizedString(Key);
        }

        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            // Shown only if the binding itself fails; a missing translation already comes back as the key.
            FallbackValue = Loc.T(Key),
        };

        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// A string-table entry as an object that re-reads itself when the language changes, for content WPF
/// copies instead of binding (see <see cref="TrExtension"/>). Displayed through the implicit template
/// declared in <c>App.xaml</c>.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs TextChanged = new(nameof(Text));

    public LocalizedString(string key)
    {
        Key = key;

        // Weak: the application-wide string source must not keep every entry alive.
        PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
    }

    public string Key { get; }

    public string Text => Loc.T(Key);

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Text;

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, TextChanged);
}
