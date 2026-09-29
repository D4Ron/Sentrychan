using System.Text.Json;
using System.Text.Json.Nodes;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.Core.MihonBridge;

/// <summary>
/// Maps a Mihon source's filters to the app's <see cref="FilterList"/> and back. The app's filter
/// model was built after Mihon's, so this is one-to-one: each kind keeps its name, values and
/// initial state, and a search sends back only what the user changed, addressed by position
/// (and by position inside a group), which is how the server applies it to a fresh list.
/// </summary>
public static class BridgeFilters
{
    public static FilterList Parse(JsonElement filters) =>
        new(filters.EnumerateArray().Select(ParseOne).Where(f => f != null)!);

    private static MangaFilter? ParseOne(JsonElement f)
    {
        var name = SuwayomiClient.Str(f, "name") ?? "";
        switch (SuwayomiClient.Str(f, "__typename"))
        {
            case "HeaderFilter": return new HeaderFilter(name);
            case "SeparatorFilter": return new SeparatorFilter();
            case "SelectFilter":
                return new SelectFilter(name, Strings(f, "values"),
                    f.TryGetProperty("selectDefault", out var sd) && sd.ValueKind == JsonValueKind.Number ? sd.GetInt32() : 0);
            case "TextFilter": return new TextFilter(name, SuwayomiClient.Str(f, "textDefault") ?? "");
            case "CheckBoxFilter": return new CheckBoxFilter(name, SuwayomiClient.Bool(f, "checkBoxDefault"));
            case "TriStateFilter":
                return new TriStateFilter(name, SuwayomiClient.Str(f, "triStateDefault") switch
                {
                    "INCLUDE" => TriState.Include,
                    "EXCLUDE" => TriState.Exclude,
                    _ => TriState.Ignore,
                });
            case "SortFilter":
                SortSelection? sort = f.TryGetProperty("sortDefault", out var s) && s.ValueKind == JsonValueKind.Object
                    ? new SortSelection(s.GetProperty("index").GetInt32(), SuwayomiClient.Bool(s, "ascending"))
                    : null;
                return new SortFilter(name, Strings(f, "values"), sort);
            case "GroupFilter":
                return new GroupFilter(name, f.GetProperty("filters").EnumerateArray().Select(ParseOne).Where(x => x != null).ToList()!);
            default:
                // A kind added to the schema after the pinned release: keep positions right by
                // standing in a header, which is never sent back.
                return new HeaderFilter(name);
        }
    }

    /// <summary>The server's <c>[FilterChangeInput!]</c> for everything changed from the initial state.</summary>
    public static JsonArray Changes(FilterList filters)
    {
        var changes = new JsonArray();
        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            if (!f.IsChanged) continue;
            if (f is GroupFilter g)
            {
                // One change per changed child; the server applies repeated positions in order.
                for (var j = 0; j < g.Filters.Count; j++)
                    if (g.Filters[j].IsChanged && State(j, g.Filters[j]) is { } inner)
                        changes.Add(new JsonObject { ["position"] = i, ["groupChange"] = inner });
            }
            else if (State(i, f) is { } change) changes.Add(change);
        }
        return changes;
    }

    private static JsonObject? State(int position, MangaFilter f) => f switch
    {
        SelectFilter s => new JsonObject { ["position"] = position, ["selectState"] = s.State },
        TextFilter t => new JsonObject { ["position"] = position, ["textState"] = t.State },
        CheckBoxFilter c => new JsonObject { ["position"] = position, ["checkBoxState"] = c.State },
        TriStateFilter t => new JsonObject { ["position"] = position, ["triState"] = t.State.ToString().ToUpperInvariant() },
        SortFilter { State: { } sel } => new JsonObject
        {
            ["position"] = position,
            ["sortState"] = new JsonObject { ["index"] = sel.Index, ["ascending"] = sel.Ascending },
        },
        _ => null,
    };

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
}
