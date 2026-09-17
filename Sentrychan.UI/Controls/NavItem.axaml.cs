using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.Windows.Input;

namespace Sentrychan.UI.Controls;

public partial class NavItem : UserControl
{
    public static readonly StyledProperty<string> IconProperty =
        AvaloniaProperty.Register<NavItem, string>(nameof(Icon));

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<NavItem, string>(nameof(Label));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<NavItem, bool>(nameof(IsActive));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<NavItem, bool>(nameof(IsExpanded), defaultValue: true);

    public static readonly StyledProperty<ICommand> CommandProperty =
        AvaloniaProperty.Register<NavItem, ICommand>(nameof(Command));

    public string Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public ICommand Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public NavItem()
    {
        InitializeComponent();
    }
}
