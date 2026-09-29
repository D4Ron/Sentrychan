using Avalonia.Controls;
using Avalonia.VisualTree;
using Sentrychan.UI.ViewModels.Mihon;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.UI.Views.Mihon;

public partial class MihonMangaView : UserControl
{
    public MihonMangaView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MihonMangaViewModel vm) return;
            // Browse's result cards open the same preview dialog as the classic page.
            vm.BrowseHost.Preview = async (result, source) =>
            {
                if (this.FindAncestorOfType<Window>() is not { } owner) return;
                var choice = await new MangaPreviewDialog(result, source).ShowDialog<string?>(owner);
                if (choice == "read" && vm.BrowseHost.ReadPreview != null)
                    await vm.BrowseHost.ReadPreview(result, source);
            };
        };
    }
}
