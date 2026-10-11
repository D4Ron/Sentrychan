using Sentrychan.Core.Updates;

namespace Sentrychan.Tests;

public class ChangelogTests
{
    private static readonly IReadOnlyList<ChangelogEntry> Notes = Changelog.ForBuild(
    [
        new("2026-10-06", "Older", ["a"]),
        new("2026-10-11", "Newest", ["b"]),
        new("2026-10-07", "Middle", ["c"]),
        new("2026-10-09", "Preview only", ["d"], PreviewOnly: true),
    ], isPreview: false);

    [Fact]
    public void A_build_gets_its_notes_newest_first()
    {
        Assert.Equal(["2026-10-11", "2026-10-07", "2026-10-06"], Notes.Select(n => n.Id));
        Assert.Equal(4, Changelog.ForBuild(Notes.Append(new("2026-10-09", "P", ["d"], true)), isPreview: true).Count);
        Assert.Equal("11 October 2026", Notes[0].DateText);
    }

    [Fact]
    public void After_an_update_only_the_new_notes_show_and_only_once()
    {
        var (show, remember) = Changelog.Unseen(Notes, lastSeen: "2026-10-06", usedBefore: true);
        Assert.Equal(["2026-10-11", "2026-10-07"], show.Select(n => n.Id));
        Assert.Equal("2026-10-11", remember);

        var (again, _) = Changelog.Unseen(Notes, lastSeen: remember, usedBefore: true);
        Assert.Empty(again);
    }

    [Fact]
    public void A_fresh_install_shows_nothing_and_an_older_install_sees_the_notes()
    {
        var (fresh, remember) = Changelog.Unseen(Notes, lastSeen: null, usedBefore: false);
        Assert.Empty(fresh);
        Assert.Equal("2026-10-11", remember);

        var (updated, _) = Changelog.Unseen(Notes, lastSeen: null, usedBefore: true);
        Assert.Equal(3, updated.Count);
    }

    [Fact]
    public void The_shipped_notes_parse_and_are_in_order()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Sentrychan.UI", "Assets", "changelog.json");
        var notes = Changelog.Parse(File.ReadAllText(file));
        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.NotEmpty(n.Changes));
        // Newest first in the file too, so whoever adds a note puts it on top.
        Assert.Equal(notes.Select(n => n.Id).OrderByDescending(i => i, StringComparer.Ordinal), notes.Select(n => n.Id));
        Assert.Equal(notes.Count, notes.Select(n => n.Id).Distinct().Count());
    }
}
