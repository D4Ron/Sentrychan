using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Sentrychan.UI.ViewModels.Mihon;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.UI.Views.Mihon;

public partial class MihonLibraryView : UserControl
{
    private MihonLibraryViewModel? _vm;

    public MihonLibraryView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as MihonLibraryViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
            ApplyDisplayMode();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MihonLibraryViewModel.DisplayMode)) ApplyDisplayMode();
    }

    // One items control whose panel and template follow the display mode — four always-built
    // copies of a large library would cost four times the covers.
    private void ApplyDisplayMode()
    {
        if (_vm == null) return;
        var (template, list) = _vm.DisplayMode switch
        {
            LibraryDisplayMode.CompactGrid => ("CompactCard", false),
            LibraryDisplayMode.CoverOnly   => ("CoverOnlyCard", false),
            LibraryDisplayMode.List        => ("ListRow", true),
            _                              => ("ComfortableCard", false),
        };
        Shelf.ItemTemplate = (IDataTemplate)Resources[template]!;
        Shelf.ItemsPanel = new FuncTemplate<Panel?>(() => list
            ? new StackPanel { Orientation = Orientation.Vertical }
            : new WrapPanel());
    }

    private Window? Owner => this.FindAncestorOfType<Window>();

    private async void OnEditCategories(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner is not { } owner) return;
        var edit = new EditCategoriesViewModel(_vm.Library);
        await edit.LoadAsync();
        await new MangaCategoriesDialog { DataContext = edit }.ShowDialog(owner);
        await _vm.LoadAsync();
    }

    private async void OnSetCategories(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || Owner is not { } owner) return;
        var chosen = _vm.SelectedManga;
        if (chosen.Count == 0) return;
        var edit = new EditCategoriesViewModel(_vm.Library);
        var initial = _vm.CategoryChoicesFor(chosen).Where(c => c.IsChecked).Select(c => c.Category.Id);
        var dialog = MangaCategoriesDialog.ForPicking(edit, initial, chosen.Count == 1 ? $"\"{chosen[0].Title}\"" : $"{chosen.Count} titles");
        await edit.LoadAsync();
        if (await dialog.ShowDialog<List<int>?>(owner) is { } ids)
            await _vm.SetCategoriesAsync(chosen, ids);
        else
            await _vm.LoadAsync(); // categories may have been added or renamed
    }
}
