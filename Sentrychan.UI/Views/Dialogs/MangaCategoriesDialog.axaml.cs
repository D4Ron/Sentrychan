using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sentrychan.UI.ViewModels.Mihon;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>
/// Edits the category list; in pick mode (<see cref="ForPicking"/>) it also ticks which
/// categories the chosen titles go on, returning the ticked ids — null when cancelled.
/// </summary>
public partial class MangaCategoriesDialog : Window
{
    private HashSet<int>? _initiallyChecked;

    public MangaCategoriesDialog()
    {
        InitializeComponent();
    }

    /// <summary>Pick mode: tick boxes shown (Tag set), OK/Cancel instead of Done.</summary>
    public static MangaCategoriesDialog ForPicking(EditCategoriesViewModel vm, IEnumerable<int> checkedIds, string what)
    {
        var d = new MangaCategoriesDialog { DataContext = vm, Tag = "pick" };
        d._initiallyChecked = checkedIds.ToHashSet();
        d.Heading.Text = "Set categories";
        d.Hint.Text = $"Tick the categories for {what}. Unticked everywhere, it shows under Default.";
        d.CancelButton.IsVisible = true;
        d.DoneButton.Content = "OK";
        vm.Categories.CollectionChanged += (_, _) => d.ApplyChecks();
        return d;
    }

    // The list reloads after every edit (add, rename, reorder); carry the ticks over by id,
    // including ones the user changed since the dialog opened.
    private void ApplyChecks()
    {
        if (_initiallyChecked == null || DataContext is not EditCategoriesViewModel vm) return;
        foreach (var c in vm.Categories)
        {
            c.IsChecked = _initiallyChecked.Contains(c.Category.Id);
            c.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(CategoryChoiceVm.IsChecked)) return;
                if (c.IsChecked) _initiallyChecked.Add(c.Category.Id);
                else _initiallyChecked.Remove(c.Category.Id);
            };
        }
    }

    private void OnDone(object? sender, RoutedEventArgs e)
    {
        if (Tag == null || DataContext is not EditCategoriesViewModel vm) { Close(null); return; }
        Close(vm.Categories.Where(c => c.IsChecked).Select(c => c.Category.Id).ToList());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
