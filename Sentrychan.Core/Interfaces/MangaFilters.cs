using System.Collections;

namespace Sentrychan.Core.Interfaces;

/// <summary>
/// A source's search filters, modelled on Mihon's <c>FilterList</c> so a source bridged from a
/// Mihon extension maps one to one. The source describes its filters with
/// <see cref="IMangaSourceService.GetFilterList"/>; the app renders them generically, the user
/// sets their state, and the same list comes back in
/// <see cref="IMangaSourceService.SearchAsync(string, int, FilterList, CancellationToken)"/>.
///
/// <para>Filters carry mutable state (which option is picked, what's typed). The app always
/// works on a <see cref="FilterList.Clone"/> of what <c>GetFilterList</c> returned, so a
/// source may return a shared instance — but it must return filters in the same order and
/// shape every time, because a filter is identified by its position.</para>
/// </summary>
public abstract class MangaFilter
{
    protected MangaFilter(string name) => Name = name;

    /// <summary>Label shown to the user.</summary>
    public string Name { get; }

    /// <summary>A copy with the same state, for editing without touching the source's instance.</summary>
    public abstract MangaFilter Clone();

    /// <summary>Back to the state it was created with.</summary>
    public abstract void Reset();

    /// <summary>True when the user has changed it from its initial state.</summary>
    public abstract bool IsChanged { get; }
}

/// <summary>A heading between filters; has no state.</summary>
public sealed class HeaderFilter(string name) : MangaFilter(name)
{
    public override MangaFilter Clone() => new HeaderFilter(Name);
    public override void Reset() { }
    public override bool IsChanged => false;
}

/// <summary>A divider between filters; has no state.</summary>
public sealed class SeparatorFilter() : MangaFilter(string.Empty)
{
    public override MangaFilter Clone() => new SeparatorFilter();
    public override void Reset() { }
    public override bool IsChanged => false;
}

/// <summary>Pick one of <see cref="Values"/>; <see cref="State"/> is the chosen index.</summary>
public sealed class SelectFilter : MangaFilter
{
    private readonly int _initial;

    public SelectFilter(string name, IReadOnlyList<string> values, int state = 0) : base(name)
    {
        Values = values;
        _initial = state;
        State = state;
    }

    public IReadOnlyList<string> Values { get; }
    public int State { get; set; }
    public string? Selected => State >= 0 && State < Values.Count ? Values[State] : null;

    public override MangaFilter Clone() => new SelectFilter(Name, Values, _initial) { State = State };
    public override void Reset() => State = _initial;
    public override bool IsChanged => State != _initial;
}

/// <summary>Free text; <see cref="State"/> is what was typed.</summary>
public sealed class TextFilter : MangaFilter
{
    private readonly string _initial;

    public TextFilter(string name, string state = "") : base(name)
    {
        _initial = state;
        State = state;
    }

    public string State { get; set; }

    public override MangaFilter Clone() => new TextFilter(Name, _initial) { State = State };
    public override void Reset() => State = _initial;
    public override bool IsChanged => State != _initial;
}

/// <summary>On or off.</summary>
public sealed class CheckBoxFilter : MangaFilter
{
    private readonly bool _initial;

    public CheckBoxFilter(string name, bool state = false) : base(name)
    {
        _initial = state;
        State = state;
    }

    public bool State { get; set; }

    public override MangaFilter Clone() => new CheckBoxFilter(Name, _initial) { State = State };
    public override void Reset() => State = _initial;
    public override bool IsChanged => State != _initial;
}

/// <summary>Mihon's tri-state: a genre, say, can be required, excluded, or not considered.</summary>
public enum TriState { Ignore, Include, Exclude }

/// <summary>Include / exclude / ignore.</summary>
public sealed class TriStateFilter : MangaFilter
{
    private readonly TriState _initial;

    public TriStateFilter(string name, TriState state = TriState.Ignore) : base(name)
    {
        _initial = state;
        State = state;
    }

    public TriState State { get; set; }

    /// <summary>Ignore → Include → Exclude → Ignore, as a tri-state checkbox cycles.</summary>
    public void Cycle() => State = State switch
    {
        TriState.Ignore  => TriState.Include,
        TriState.Include => TriState.Exclude,
        _                => TriState.Ignore,
    };

    public override MangaFilter Clone() => new TriStateFilter(Name, _initial) { State = State };
    public override void Reset() => State = _initial;
    public override bool IsChanged => State != _initial;
}

/// <summary>Which of <see cref="Values"/> to sort by, and which way.</summary>
public readonly record struct SortSelection(int Index, bool Ascending);

/// <summary>A sort order: pick one of <see cref="Values"/> and a direction. A null state means the source's default.</summary>
public sealed class SortFilter : MangaFilter
{
    private readonly SortSelection? _initial;

    public SortFilter(string name, IReadOnlyList<string> values, SortSelection? state = null) : base(name)
    {
        Values = values;
        _initial = state;
        State = state;
    }

    public IReadOnlyList<string> Values { get; }
    public SortSelection? State { get; set; }

    public override MangaFilter Clone() => new SortFilter(Name, Values, _initial) { State = State };
    public override void Reset() => State = _initial;
    public override bool IsChanged => State != _initial;
}

/// <summary>
/// A named set of filters shown together — most often a genre list of <see cref="TriStateFilter"/>s
/// or <see cref="CheckBoxFilter"/>s. Groups don't nest.
/// </summary>
public sealed class GroupFilter(string name, IReadOnlyList<MangaFilter> filters) : MangaFilter(name)
{
    public IReadOnlyList<MangaFilter> Filters { get; } = filters;

    public override MangaFilter Clone() => new GroupFilter(Name, Filters.Select(f => f.Clone()).ToList());
    public override void Reset() { foreach (var f in Filters) f.Reset(); }
    public override bool IsChanged => Filters.Any(f => f.IsChanged);
}

/// <summary>An ordered list of filters. See <see cref="MangaFilter"/>.</summary>
public sealed class FilterList : IReadOnlyList<MangaFilter>
{
    private readonly List<MangaFilter> _filters;

    public FilterList(IEnumerable<MangaFilter> filters) => _filters = filters.ToList();
    public FilterList(params MangaFilter[] filters) => _filters = [.. filters];

    /// <summary>A source with no filters. A new instance each time, so nobody shares state by accident.</summary>
    public static FilterList Empty => new();

    public MangaFilter this[int index] => _filters[index];
    public int Count => _filters.Count;
    public IEnumerator<MangaFilter> GetEnumerator() => _filters.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public FilterList Clone() => new(_filters.Select(f => f.Clone()));
    public void Reset() { foreach (var f in _filters) f.Reset(); }
    public bool IsChanged => _filters.Any(f => f.IsChanged);

    /// <summary>The first filter of a type and name — a convenience for sources reading their own list back.</summary>
    public T? Find<T>(string name) where T : MangaFilter =>
        _filters.OfType<T>().FirstOrDefault(f => f.Name == name)
        ?? _filters.OfType<GroupFilter>().SelectMany(g => g.Filters).OfType<T>().FirstOrDefault(f => f.Name == name);
}
