using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels;
using System.ComponentModel;

namespace Sentrychan.UI.Views.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.StatusMessage) && sender is SettingsViewModel vm && vm.StatusMessage == "Settings saved")
        {
            Close(true);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}