using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels;
using System.ComponentModel;

namespace Sentrychan.UI.Views.Dialogs;

public partial class AddFeedDialog : Window
{
    public AddFeedDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is AddFeedViewModel vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddFeedViewModel.Added) && sender is AddFeedViewModel vm && vm.Added)
        {
            Close(true);
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
        => Close(null);
}