using Microsoft.EntityFrameworkCore;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;

namespace Sentrychan.Core.Library;

/// <summary>
/// Keeps the database in step with files that moved — after a tidy, and after its undo (which
/// is the same thing with the moves reversed):
/// <list type="bullet">
/// <item>DownloadJob.FinalFilePath, which names either the file or, for a batch, its show folder;</item>
/// <item>LibraryFileOrigins, so a renamed file keeps the name its torrent knows it by.</item>
/// </list>
/// </summary>
public static class TidyRecords
{
    public static async Task UpdateAsync(AppDbContext db, string libraryPath, IEnumerable<TidyMove> moves, CancellationToken ct = default)
    {
        var videos = moves.Where(m => !m.IsSidecar).ToList();
        if (videos.Count == 0) return;

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in videos)
        {
            files[m.From] = m.To;
            dirs.TryAdd(Path.GetDirectoryName(m.From)!, Path.GetDirectoryName(m.To)!);
            if (ShowFolder(libraryPath, m.From) is { } fromShow && ShowFolder(libraryPath, m.To) is { } toShow)
                dirs.TryAdd(fromShow, toShow);
        }

        var jobs = await db.DownloadJobs.Where(j => j.FinalFilePath != null && j.FinalFilePath != "vault").ToListAsync(ct);
        foreach (var job in jobs)
        {
            var p = Path.TrimEndingDirectorySeparator(job.FinalFilePath!);
            if (files.TryGetValue(p, out var to) || dirs.TryGetValue(p, out to))
                job.FinalFilePath = to;
        }

        foreach (var m in videos)
            await MoveOriginAsync(db, m.From, m.To, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Follows a file from one path to another in LibraryFileOrigins: the first rename records
    /// the name it came with; later ones carry that along; renaming it back to that name drops
    /// the record.
    /// </summary>
    public static async Task MoveOriginAsync(AppDbContext db, string from, string to, CancellationToken ct = default)
    {
        var row = db.LibraryFileOrigins.Local.FirstOrDefault(o => string.Equals(o.Path, from, StringComparison.OrdinalIgnoreCase))
               ?? await db.LibraryFileOrigins.FirstOrDefaultAsync(o => o.Path == from, ct);
        var original = row?.OriginalName ?? Path.GetFileName(from);

        // Whatever an earlier run recorded at the destination is stale: that file isn't there any more.
        var stale = await db.LibraryFileOrigins.FirstOrDefaultAsync(o => o.Path == to, ct);
        if (stale != null && stale != row) db.LibraryFileOrigins.Remove(stale);

        if (string.Equals(original, Path.GetFileName(to), StringComparison.Ordinal))
        {
            if (row != null) db.LibraryFileOrigins.Remove(row);
            return;
        }
        if (row != null)
        {
            row.Path = to;
            return;
        }
        db.LibraryFileOrigins.Add(new LibraryFileOrigin { Path = to, OriginalName = original });
    }

    /// <summary>The name a library file was downloaded as, or its current name if it was never renamed.</summary>
    public static async Task<string> OriginalNameAsync(AppDbContext db, string path, CancellationToken ct = default) =>
        (await db.LibraryFileOrigins.AsNoTracking().FirstOrDefaultAsync(o => o.Path == path, ct))?.OriginalName
        ?? Path.GetFileName(path);

    private static string? ShowFolder(string libraryPath, string file)
    {
        var rel = Path.GetRelativePath(libraryPath, file);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        var first = rel.Split(Path.DirectorySeparatorChar)[0];
        return first == rel ? null : Path.Combine(libraryPath, first);
    }
}
