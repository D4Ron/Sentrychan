using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.ReactiveUI;
using ReactiveUI;
using Sentrychan.UI.ViewModels;
using System;

namespace Sentrychan.UI.Views.Dialogs;

public partial class FillGapsDialog : ReactiveWindow<FillGapsViewModel>
{
    public FillGapsDialog()
    {
        InitializeComponent();
        if (!Avalonia.Controls.Design.IsDesignMode)
        {
            this.WhenActivated(d =>
            {
                if (ViewModel != null)
                {
                    d(ViewModel.ConfirmCommand.Subscribe(Close));
                    d(ViewModel.CancelCommand.Subscribe(_ => Close(null)));
                }
            });
        }
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        // Handled by ConfirmCommand subscription
    }
}
