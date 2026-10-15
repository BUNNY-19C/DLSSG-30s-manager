using System.Windows.Data;
using System.Windows.Markup;

namespace DLSSGManager;

/// <summary>
/// XAML markup extension resolving a key through <see cref="Loc"/>, e.g.
/// <c>Text="{loc:Tr Main.ScanSteam}"</c>.
///
/// Returns a binding rather than a fixed string so that changing the language propagates to the live
/// window. Split into its own file because it is the only part of the localisation layer that needs
/// WPF, keeping <c>Localization.cs</c> independent of the presentation framework.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = Key,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
