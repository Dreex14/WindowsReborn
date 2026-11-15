using System.Windows.Data;
using System.Windows.Markup;
using WindowsReborn.Core.Localization;

namespace WindowsReborn.App.Infrastructure;

/// <summary>
/// Extensión de marcado para textos traducidos: <c>Text="{l:Tr backup.title}"</c>.
/// Se actualiza en vivo si se cambia el idioma.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
