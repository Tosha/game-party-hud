using System.Windows;
using System.Windows.Controls;

namespace GamePartyHud;

/// <summary>
/// Picks between the regular preset-row template and the "+ New preset"
/// command-row template based on
/// <see cref="MainWindow.PresetItemViewModel.IsCommandRow"/>.
/// </summary>
public sealed class PresetItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PresetRowTemplate { get; set; }
    public DataTemplate? CommandRowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is MainWindow.PresetItemViewModel vm && vm.IsCommandRow)
        {
            return CommandRowTemplate;
        }
        return PresetRowTemplate;
    }
}
