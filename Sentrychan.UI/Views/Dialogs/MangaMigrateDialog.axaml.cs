using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels.Mihon;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>Returns the chosen <see cref="MigrateCandidateVm"/>, or null when cancelled.</summary>
public partial class MangaMigrateDialog : Window
{
    public MangaMigrateDialog()
    {
        InitializeComponent();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) =>
        Close((DataContext as MigrateViewModel)?.Selected);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
