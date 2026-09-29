using Microsoft.Data.Sqlite;

namespace Sentrychan.Core.Services;

/// <summary>
/// "Copy my library from Sentrychan" — the preview's first-run offer. One way: the stable data
/// folder is only ever read (plain file copies, never opened by SQLite, so not even a -shm file
/// appears there).
///
/// Two steps, because the preview's own database is already open by the time anyone can answer
/// a dialog:
/// <list type="number">
/// <item><see cref="Stage"/> (while running) copies stable's data into a staging folder inside the
/// preview's data directory and fixes the copy up. Nothing live is touched.</item>
/// <item><see cref="ApplyStaged"/> (next startup, before anything opens the database) swaps the
/// staged files in. What they replace is kept in a dated backup folder, not deleted.</item>
/// </list>
///
/// Not copied: the vault key (the preview starts its own vault — see <c>VaultService.DefaultRoot</c>)
/// and the sign-in session (refreshing it in one app would sign the other out). Downloads still in
/// flight stay with stable.
/// </summary>
public static class StableLibraryCopy
{
    /// <summary>AppConfigs key: the offer has been answered (or the library came from stable).</summary>
    public const string OfferedKey = "StableCopyOffered";

    private const string StagingName    = "import-from-stable";
    private const string StagingTmpName = StagingName + ".tmp";
    public const string BackupPrefix    = "before-import-from-stable-";
    private const string DatabaseName   = "sentrychan.db";

    // What makes up "my library": the database, plus what it points at or depends on — imported
    // source packs, custom covers, cached posters (Series.PosterPath / Manga.CoverPath hold absolute
    // paths into these), and playback positions. Everything else in the data folder is a
    // regenerable cache, a log, or deliberately left behind (vault.key, Auth/).
    private static readonly string[] Folders = ["sources", "Covers", "ImageCache"];
    private static readonly string[] Files   = ["playback.json"];

    /// <summary>Whether there's a stable library to offer.</summary>
    public static bool StableDataExists(string stableDir) => File.Exists(Path.Combine(stableDir, DatabaseName));

    /// <summary>Whether a staged copy is waiting for the next startup.</summary>
    public static bool IsStaged(string dataDir) => Directory.Exists(Path.Combine(dataDir, StagingName));

    /// <summary>
    /// Copies stable's library into <c>&lt;dataDir&gt;/import-from-stable</c> and adjusts the copy for
    /// the preview. Only call while stable isn't running: its database is copied as files, which is
    /// consistent only when nothing is writing to it. The folder appears under its final name only
    /// once complete, so an interrupted copy is never applied.
    /// </summary>
    public static void Stage(string stableDir, string dataDir)
    {
        if (!StableDataExists(stableDir))
            throw new FileNotFoundException("There's no Sentrychan library to copy.", Path.Combine(stableDir, DatabaseName));

        var tmp   = Path.Combine(dataDir, StagingTmpName);
        var final = Path.Combine(dataDir, StagingName);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        if (Directory.Exists(final)) Directory.Delete(final, recursive: true);
        Directory.CreateDirectory(tmp);

        // A -wal left by an unclean exit holds committed data; it goes along and SQLite folds it in
        // when the copy is opened below. The -shm is only an index and is rebuilt.
        CopyFile(Path.Combine(stableDir, DatabaseName), Path.Combine(tmp, DatabaseName));
        CopyFile(Path.Combine(stableDir, DatabaseName + "-wal"), Path.Combine(tmp, DatabaseName + "-wal"));
        foreach (var f in Files) CopyFile(Path.Combine(stableDir, f), Path.Combine(tmp, f));
        foreach (var d in Folders) CopyDirectory(Path.Combine(stableDir, d), Path.Combine(tmp, d));

        FixUpDatabase(Path.Combine(tmp, DatabaseName), stableDir, dataDir);

        Directory.Move(tmp, final);
    }

    /// <summary>
    /// Swaps a staged copy in. Must run before anything opens the database. Returns false when
    /// nothing was staged. Whatever the copy replaces moves to a new
    /// <c>before-import-from-stable-&lt;timestamp&gt;</c> folder — new each time, so a retry after a
    /// failed swap can't overwrite the backup of the first attempt.
    /// </summary>
    public static bool ApplyStaged(string dataDir)
    {
        var staged = Path.Combine(dataDir, StagingName);
        if (!Directory.Exists(staged)) return false;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backup = Path.Combine(dataDir, $"{BackupPrefix}{stamp}");
        for (var n = 2; Directory.Exists(backup); n++)
            backup = Path.Combine(dataDir, $"{BackupPrefix}{stamp}-{n}");
        Directory.CreateDirectory(backup);

        // The preview's own (normally near-empty) database, with its journal files.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            MoveAside(Path.Combine(dataDir, DatabaseName + suffix), backup);

        foreach (var name in Files.Concat(Folders).Append(DatabaseName))
        {
            var from = Path.Combine(staged, name);
            if (!File.Exists(from) && !Directory.Exists(from)) continue;
            var to = Path.Combine(dataDir, name);
            MoveAside(to, backup);
            if (Directory.Exists(from)) Directory.Move(from, to);
            else File.Move(from, to);
        }

        Directory.Delete(staged, recursive: true);
        return true;
    }

    /// <summary>
    /// Adjusts the copied database for the preview. Plain SQL rather than EF: the copy may be at an
    /// older schema version, and the preview's normal startup migration brings it up to date.
    /// </summary>
    private static void FixUpDatabase(string dbPath, string stableDir, string dataDir)
    {
        // Pooling off so the connection really closes, which checkpoints the WAL into the file.
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        conn.Open();
        using var tx = conn.BeginTransaction();

        void Exec(string sql, params (string Name, object Value)[] ps)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            cmd.ExecuteNonQuery();
        }

        // A pinned vault root would be stable's vault, which the preview has no key for. Cleared,
        // the preview creates its own (.cache-preview) the first time it needs one.
        Exec("DELETE FROM AppConfigs WHERE Key = 'VaultRoot'");

        // Posters and covers pointed into stable's data folder; the copies now live in ours.
        // Compared with substr, not LIKE, so '_' and '%' in a path are literal.
        var oldPrefix = Path.TrimEndingDirectorySeparator(stableDir) + Path.DirectorySeparatorChar;
        var newPrefix = Path.TrimEndingDirectorySeparator(dataDir) + Path.DirectorySeparatorChar;
        foreach (var (table, column) in new[] { ("Series", "PosterPath"), ("Manga", "CoverPath") })
            Exec($"UPDATE {table} SET {column} = $new || substr({column}, length($old) + 1) " +
                 $"WHERE substr({column}, 1, length($old)) = $old",
                 ("$old", oldPrefix), ("$new", newPrefix));

        // Downloads still queued or running belong to stable, which will finish and file them.
        // Kept here, the preview would start its own copy of each the first time it ran alone.
        Exec("DELETE FROM DownloadJobs WHERE Status IN ('Pending', 'Downloading')");

        // The library now comes from stable; don't offer to copy it again.
        Exec("DELETE FROM AppConfigs WHERE Key = $k", ("$k", OfferedKey));
        Exec("INSERT INTO AppConfigs (Key, Value) VALUES ($k, 'true')", ("$k", OfferedKey));

        tx.Commit();
    }

    private static void MoveAside(string path, string backup)
    {
        var dest = Path.Combine(backup, Path.GetFileName(path));
        if (File.Exists(path)) File.Move(path, dest);
        else if (Directory.Exists(path)) Directory.Move(path, dest);
    }

    private static void CopyFile(string from, string to)
    {
        if (File.Exists(from)) File.Copy(from, to, overwrite: false);
    }

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
