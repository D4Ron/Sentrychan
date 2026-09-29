using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Sentrychan.Core.Interfaces;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.ViewModels.Mihon;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.UI.Views.Mihon;

public partial class MihonTitleView : UserControl
{
    public MihonTitleView() => InitializeComponent();

    private MangaDetailViewModel? Vm => DataContext as MangaDetailViewModel;
    private Window? Owner => this.FindAncestorOfType<Window>();

    private async void OnSetCategories(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Library is not { } library || Owner is not { } owner) return;
        var entries = await library.GetEntriesAsync(Vm.Manga.IsNovel, includeAdult: true);
        var current = entries.FirstOrDefault(x => x.Manga.Id == Vm.Manga.Id)?.CategoryIds ?? [];
        var edit = new EditCategoriesViewModel(library);
        var dialog = MangaCategoriesDialog.ForPicking(edit, current, $"\"{Vm.Title}\"");
        await edit.LoadAsync();
        if (await dialog.ShowDialog<List<int>?>(owner) is { } ids)
            await library.SetCategoriesAsync([Vm.Manga.Id], ids);
    }

    private async void OnMigrate(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || Owner is not { } owner) return;
        if (App.Services?.GetService(typeof(IMangaSourceRegistry)) is not IMangaSourceRegistry registry) return;
        var secret = (App.Services.GetService(typeof(ISecretModeService)) as ISecretModeService)?.IsSecretModeActive ?? false;
        var pick = new MigrateViewModel(registry, vm.Manga.Source, vm.Manga.IsNovel, secret, vm.Title);
        var chosen = await new MangaMigrateDialog { DataContext = pick }.ShowDialog<MigrateCandidateVm?>(owner);
        if (chosen != null && pick.TargetSource is { } target)
            await vm.MigrateAsync(target, chosen.Result);
    }
}
