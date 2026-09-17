using Avalonia.Controls;
using System.ComponentModel;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views.Dialogs;

public partial class JoinWatchPartyDialog : Window
{
    public JoinWatchPartyDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is JoinWatchPartyViewModel vm)
        {
            vm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JoinWatchPartyViewModel.Joined))
        {
            if (DataContext is JoinWatchPartyViewModel vm && vm.Joined)
            {
                Close(true);
            }
        }
    }

    private void OnInvitePasteChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            var text = textBox.Text.Trim();
            if (text.StartsWith("sentrychan://join/", StringComparison.OrdinalIgnoreCase))
            {
                // Format: sentrychan://join/host:port/roomcode
                var parts = text.Substring("sentrychan://join/".Length).Split('/');
                if (parts.Length >= 2)
                {
                    if (DataContext is JoinWatchPartyViewModel vm)
                    {
                        var addressParts = parts[0].Split(':');
                        vm.HostIp = addressParts[0];
                        if (addressParts.Length > 1) vm.Port = addressParts[1];
                        vm.RoomCode = parts[1].ToUpperInvariant();
                        
                        // Clear the box after successful parse to avoid re-triggering
                        textBox.Text = string.Empty;
                    }
                }
            }
        }
    }

    private void Cancel_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close(false);
    }
}
