using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels.Mihon;

/// <summary>
/// One row of a source's filter sheet. Each wraps a <see cref="MangaFilter"/> from a cloned
/// <see cref="FilterList"/> and writes the user's choice straight into it, so the list handed
/// back to the source is simply that clone. The view picks a template by type.
/// </summary>
public abstract class FilterVm : ViewModelBase
{
    public abstract string Name { get; }

    public static FilterVm For(MangaFilter filter) => filter switch
    {
        HeaderFilter h    => new HeaderFilterVm(h),
        SeparatorFilter   => new SeparatorFilterVm(),
        SelectFilter s    => new SelectFilterVm(s),
        TextFilter t      => new TextFilterVm(t),
        CheckBoxFilter c  => new CheckBoxFilterVm(c),
        TriStateFilter tr => new TriStateFilterVm(tr),
        SortFilter so     => new SortFilterVm(so),
        GroupFilter g     => new GroupFilterVm(g),
        _                 => new HeaderFilterVm(new HeaderFilter(filter.Name)),
    };

    /// <summary>Re-read the wrapped filter after a reset.</summary>
    public virtual void Refresh() { }
}

public sealed class HeaderFilterVm(HeaderFilter filter) : FilterVm
{
    public override string Name => filter.Name;
}

public sealed class SeparatorFilterVm : FilterVm
{
    public override string Name => string.Empty;
}

public sealed class SelectFilterVm(SelectFilter filter) : FilterVm
{
    public override string Name => filter.Name;
    public IReadOnlyList<string> Values => filter.Values;

    public int SelectedIndex
    {
        get => filter.State;
        set { if (value < 0 || filter.State == value) return; filter.State = value; this.RaisePropertyChanged(); }
    }

    public override void Refresh() => this.RaisePropertyChanged(nameof(SelectedIndex));
}

public sealed class TextFilterVm(TextFilter filter) : FilterVm
{
    public override string Name => filter.Name;

    public string Text
    {
        get => filter.State;
        set { if (filter.State == value) return; filter.State = value ?? string.Empty; this.RaisePropertyChanged(); }
    }

    public override void Refresh() => this.RaisePropertyChanged(nameof(Text));
}

public sealed class CheckBoxFilterVm(CheckBoxFilter filter) : FilterVm
{
    public override string Name => filter.Name;

    public bool IsChecked
    {
        get => filter.State;
        set { if (filter.State == value) return; filter.State = value; this.RaisePropertyChanged(); }
    }

    public override void Refresh() => this.RaisePropertyChanged(nameof(IsChecked));
}

/// <summary>Include / exclude / ignore, cycled by one click like Mihon's tri-state checkbox.</summary>
public sealed class TriStateFilterVm : FilterVm
{
    private readonly TriStateFilter _filter;

    public TriStateFilterVm(TriStateFilter filter)
    {
        _filter = filter;
        CycleCommand = ReactiveCommand.Create(() => { _filter.Cycle(); Refresh(); });
    }

    public override string Name => _filter.Name;
    public ReactiveCommand<Unit, Unit> CycleCommand { get; }

    public bool IsIncluded => _filter.State == TriState.Include;
    public bool IsExcluded => _filter.State == TriState.Exclude;
    public string Glyph => _filter.State switch { TriState.Include => "✓", TriState.Exclude => "✕", _ => " " };

    public override void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsIncluded));
        this.RaisePropertyChanged(nameof(IsExcluded));
        this.RaisePropertyChanged(nameof(Glyph));
    }
}

/// <summary>Which value to sort by, and the direction. Picking the current one again flips it, as in Mihon.</summary>
public sealed class SortFilterVm : FilterVm
{
    private readonly SortFilter _filter;

    public SortFilterVm(SortFilter filter)
    {
        _filter = filter;
        Options = filter.Values.Select((v, i) => new SortOptionVm(this, i, v)).ToList();
    }

    public override string Name => _filter.Name;
    public IReadOnlyList<SortOptionVm> Options { get; }

    internal SortSelection? State => _filter.State;

    internal void Pick(int index)
    {
        _filter.State = _filter.State is { } s && s.Index == index
            ? s with { Ascending = !s.Ascending }
            : new SortSelection(index, true);
        Refresh();
    }

    public override void Refresh()
    {
        foreach (var o in Options) o.Refresh();
    }
}

public sealed class SortOptionVm : ViewModelBase
{
    private readonly SortFilterVm _owner;
    private readonly int _index;

    public SortOptionVm(SortFilterVm owner, int index, string label)
    {
        _owner = owner;
        _index = index;
        Label = label;
        PickCommand = ReactiveCommand.Create(() => _owner.Pick(_index));
    }

    public string Label { get; }
    public ReactiveCommand<Unit, Unit> PickCommand { get; }
    public bool IsActive => _owner.State?.Index == _index;
    public string Arrow => _owner.State is { } s && s.Index == _index ? (s.Ascending ? "↑" : "↓") : " ";

    public void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsActive));
        this.RaisePropertyChanged(nameof(Arrow));
    }
}

public sealed class GroupFilterVm : FilterVm
{
    private readonly GroupFilter _filter;

    public GroupFilterVm(GroupFilter filter)
    {
        _filter = filter;
        Children = filter.Filters.Select(For).ToList();
    }

    public override string Name => _filter.Name;
    public IReadOnlyList<FilterVm> Children { get; }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => this.RaiseAndSetIfChanged(ref _isExpanded, value); }

    public override void Refresh() { foreach (var c in Children) c.Refresh(); }
}

/// <summary>A whole filter sheet for one source: the editable clone and its rows.</summary>
public sealed class FilterSheetVm : ViewModelBase
{
    public FilterSheetVm(FilterList original)
    {
        Filters = original.Clone();
        Rows = Filters.Select(FilterVm.For).ToList();
        ResetCommand = ReactiveCommand.Create(() =>
        {
            Filters.Reset();
            foreach (var r in Rows) r.Refresh();
        });
    }

    public FilterList Filters { get; }
    public IReadOnlyList<FilterVm> Rows { get; }
    public bool IsEmpty => Rows.Count == 0;
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
}
