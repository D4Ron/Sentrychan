using Avalonia;
using Avalonia.Controls;

namespace Sentrychan.UI.Controls;

/// <summary>
/// One row of the settings page: a title and a line of explanation on the left, the
/// fields on the right, a divider underneath. Its template lives in SettingsView.
/// </summary>
public class SettingsSection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Description));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
