using System.Windows.Data;
using System.Windows.Markup;

namespace AuctionFlipper.Ui;

/// <summary>
/// Puts a translated string into XAML: <c>Text="{ui:Tr NavBoard}"</c>.
///
/// It expands to a binding against <see cref="Loc.Current"/>'s indexer rather than resolving the
/// string once, so switching language repaints the window instead of needing a restart. Writing it
/// as a markup extension rather than spelling the binding out at every call site keeps the XAML
/// readable - there are a few hundred of these.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Current,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}
