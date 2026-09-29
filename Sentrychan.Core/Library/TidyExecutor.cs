using System.Text.Json;

namespace Sentrychan.Core.Library;

/// <summary>One file moved by a tidy run.</summary>
public sealed record TidyMove(string From, string To, bool IsSidecar, int? SeriesId);

/// <summary>
/// The record of one tidy run, written as it goes so "Undo last tidy" can put everything back —
/// even after a crash mid-run. A move is recorded *before* it happens; undo only reverses moves
/// it can see happened (the file is at To and nothing is at From), so an intent recorded for a
/// move that never happened is harmless.
/// </summary>
public sealed class TidyJournal
{
    public int Version { get; set; } = 1;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }
    public bool Undone { get; set; }
    public string LibraryPath { get; set; } = string.Empty;
    public List<TidyMove> Moves { get; set; } = [];
    public List<string> CreatedDirectories { get; set; } = [];
    public List<string> RemovedDirectories { get; set; } = [];
}

public sealed class TidyResult
{
    public required string JournalPath { get; init; }
    public List<TidyMove> Moved { get; } = [];
    public List<(string File, string Reason)> Failed { get; } = [];
}

/// <summary>Moves a plan's ticked items, and undoes a run from its journal.</summary>
public static class TidyExecutor
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <param name="skipReason">Asked again right before each move: a plan can be minutes old.</param>
    public static TidyResult Apply(TidyPlan plan, string journalDir, Func<string, string?>? skipReason = null)
    {
        Directory.CreateDirectory(journalDir);
        var journal = new TidyJournal { LibraryPath = plan.LibraryPath };
        var path = Path.Combine(journalDir, $"tidy-{journal.StartedAt:yyyyMMdd-HHmmss-fff}.json");
        var result = new TidyResult { JournalPath = path };
        Save(journal, path);

        var sourceDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in plan.Items.Where(i => i.Selected && i.Status == TidyItemStatus.Ready))
        {
            var dest = item.Destination!;
            var problem = !File.Exists(item.Source) ? "it's no longer there"
                : skipReason?.Invoke(item.Source)
                ?? (Taken(item.Source, dest) ? $"{Path.GetFileName(dest)} appeared there since the plan was made"
                : item.Sidecars.FirstOrDefault(s => Taken(s.From, s.To)) is { } sc ? $"{Path.GetFileName(sc.To)} appeared there since the plan was made"
                : null);
            if (problem != null)
            {
                result.Failed.Add((item.Source, problem));
                continue;
            }

            try
            {
                EnsureDirectory(Path.GetDirectoryName(dest)!, journal);
                Record(journal, path, new TidyMove(item.Source, dest, false, item.SeriesId));
                MoveFile(item.Source, dest);
                result.Moved.Add(journal.Moves[^1]);
                sourceDirs.Add(Path.GetDirectoryName(item.Source)!);
            }
            catch (Exception ex)
            {
                result.Failed.Add((item.Source, ex.Message));
                continue;
            }

            foreach (var sidecar in item.Sidecars)
            {
                try
                {
                    if (!File.Exists(sidecar.From)) continue;
                    Record(journal, path, new TidyMove(sidecar.From, sidecar.To, true, item.SeriesId));
                    MoveFile(sidecar.From, sidecar.To);
                    result.Moved.Add(journal.Moves[^1]);
                }
                catch (Exception ex)
                {
                    result.Failed.Add((sidecar.From, ex.Message));
                }
            }
        }

        foreach (var dir in sourceDirs.OrderByDescending(d => d.Length))
            RemoveEmptyUpTo(dir, plan.LibraryPath, journal);

        journal.FinishedAt = DateTime.Now;
        Save(journal, path);
        return result;
    }

    /// <summary>The newest journal that hasn't been undone, or null.</summary>
    public static string? LatestUndoable(string journalDir)
    {
        if (!Directory.Exists(journalDir)) return null;
        foreach (var file in Directory.EnumerateFiles(journalDir, "tidy-*.json").OrderByDescending(f => f, StringComparer.Ordinal))
        {
            var journal = Load(file);
            if (journal is { Undone: false } && journal.Moves.Count > 0) return file;
        }
        return null;
    }

    public static TidyJournal? Load(string journalPath)
    {
        try { return JsonSerializer.Deserialize<TidyJournal>(File.ReadAllText(journalPath)); }
        catch { return null; }
    }

    /// <summary>
    /// Puts every recorded move back, newest first. A file that has since moved again, or whose
    /// old place is taken, is left where it is and reported.
    /// </summary>
    public static TidyResult Undo(string journalPath)
    {
        var journal = Load(journalPath) ?? throw new InvalidDataException("The tidy journal can't be read.");
        var result = new TidyResult { JournalPath = journalPath };

        foreach (var dir in journal.RemovedDirectories)
            try { Directory.CreateDirectory(dir); } catch { /* recreated below by the moves, if needed */ }

        for (var i = journal.Moves.Count - 1; i >= 0; i--)
        {
            var move = journal.Moves[i];
            var caseOnly = string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase);
            if (!File.Exists(move.To))
            {
                // Never moved (intent recorded, then a crash), or moved again since.
                if (!File.Exists(move.From)) result.Failed.Add((move.To, "it's no longer there"));
                continue;
            }
            if (!caseOnly && File.Exists(move.From))
            {
                result.Failed.Add((move.To, $"{Path.GetFileName(move.From)} is back in its old place already"));
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(move.From)!);
                MoveFile(move.To, move.From);
                result.Moved.Add(move with { From = move.To, To = move.From });
            }
            catch (Exception ex)
            {
                result.Failed.Add((move.To, ex.Message));
            }
        }

        foreach (var dir in journal.CreatedDirectories.OrderByDescending(d => d.Length))
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch { /* still has something in it — leave it */ }

        journal.Undone = true;
        Save(journal, journalPath);
        return result;
    }

    private static bool Taken(string from, string to) =>
        !string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && (File.Exists(to) || Directory.Exists(to));

    // File.Move of a case-only rename is a no-op on Windows; go through a temporary name.
    private static void MoveFile(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && !string.Equals(from, to, StringComparison.Ordinal))
        {
            var tmp = to + ".tidy-" + Guid.NewGuid().ToString("N")[..8];
            File.Move(from, tmp);
            File.Move(tmp, to);
            return;
        }
        File.Move(from, to);
    }

    private static void EnsureDirectory(string dir, TidyJournal journal)
    {
        // Record each level created, outermost first, so undo can remove exactly those.
        var missing = new Stack<string>();
        for (var d = dir; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
            missing.Push(d);
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            Directory.CreateDirectory(d);
            journal.CreatedDirectories.Add(d);
        }
    }

    private static void RemoveEmptyUpTo(string dir, string libraryRoot, TidyJournal journal)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        for (var d = dir; d != null && d.Length > root.Length && d.StartsWith(root, StringComparison.OrdinalIgnoreCase);
             d = Path.GetDirectoryName(d))
        {
            try
            {
                if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) return;
                Directory.Delete(d);
                journal.RemovedDirectories.Insert(0, d);
            }
            catch { return; }
        }
    }

    private static void Record(TidyJournal journal, string path, TidyMove move)
    {
        journal.Moves.Add(move);
        Save(journal, path);
    }

    private static void Save(TidyJournal journal, string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(journal, Json));
        File.Move(tmp, path, overwrite: true);
    }
}
