using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Sentrychan.UI.ViewModels;

namespace Sentrychan.UI.Views;

public partial class NewsView : UserControl
{
    public NewsView()
    {
        InitializeComponent();
    }

    private void OnCardTapped(object? sender, TappedEventArgs e)
    {
        // Don't toggle when the tap landed on the "Read full article" button —
        // the button handles its own command and shouldn't collapse the card.
        if (e.Source is Control c && c.FindAncestorOfType<Button>() != null) return;

        if (sender is Border { DataContext: NewsItemVm item })
            item.IsExpanded = !item.IsExpanded;
    }
}
