using Avalonia.Controls;

namespace Sentrychan.UI.Views.Dialogs;

public partial class AddAliasDialog : Window
{
    public string? Alias { get; private set; }

    public AddAliasDialog()
    {
        InitializeComponent();
        AddButton.Click += (s, e) => { Alias = AliasTextBox.Text; Close(true); };
        CancelButton.Click += (s, e) => Close(false);
    }
}
