using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Sentrychan.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();

        // A closed ComboBox under the cursor takes the mouse wheel and steps through its
        // items. On a long scrolling page that silently changed settings — scrolling past
        // the Theme box switched the theme. Here the wheel always scrolls the page.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var combo = (e.Source as Visual)?.FindAncestorOfType<ComboBox>(includeSelf: true);
            if (combo == null || combo.IsDropDownOpen) return;
            e.Handled = true;
            PageScroll.Offset = PageScroll.Offset.WithY(PageScroll.Offset.Y - e.Delta.Y * 60);
        }, RoutingStrategies.Tunnel);
    }
}
