using Microsoft.Data.Sqlite;

namespace TkvscHost;

/// <summary>
/// The content hashes of the game's own files, kept in a database per game and made as they are asked for: a project holds a few
/// files, so only the game's files that a project has are ever read, never the whole RomFS. A file is hashed again only when it
/// changed on disk, and the database starts over when it is of another RomFS.
/// </summary>
public sealed class HashCache : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _romfs;
    private readonly Archives _archives;

    private HashCache(SqliteConnection connection, string romfs, Archives archives)
    {
        _connection = connection;
        _romfs = romfs;
        _archives = archives;
    }

    public static HashCache Open(string databasePath, string romfs, Archives archives)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        // Another host process may be using the database; wait for it instead of failing.
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 15 }.ToString());
        connection.Open();

        string root = Archives.Normalize(Path.GetFullPath(romfs));
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sources (rel TEXT PRIMARY KEY COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL, tree INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS hashes (path TEXT PRIMARY KEY COLLATE NOCASE, hash TEXT NOT NULL);
            """);

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.CommandText = "SELECT value FROM meta WHERE key = 'root'";
            string? stored = read.ExecuteScalar() as string;
            if (!string.Equals(stored, root, StringComparison.OrdinalIgnoreCase))
            {
                // Hashes of another game's (or another dump's) files mean nothing here.
                Execute(connection, "DELETE FROM sources; DELETE FROM hashes; DELETE FROM meta;");
                using SqliteCommand write = connection.CreateCommand();
                write.CommandText = "INSERT INTO meta (key, value) VALUES ('root', $r)";
                write.Parameters.AddWithValue("$r", root);
                write.ExecuteNonQuery();
            }
        }

        return new HashCache(connection, romfs, archives);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The hash of the game's file at <paramref name="rel"/>, or null if the game has no such file.</summary>
    public string? FileHash(string rel)
    {
        rel = Archives.Normalize(rel);
        if (Stamp(rel) is not { } stamp) return null;
        if (!IsKnown(rel, stamp, needTree: false))
            return Store(rel, stamp, tree: false).Where(r => r.Path == rel).Select(r => r.Hash).FirstOrDefault();

        using SqliteCommand query = _connection.CreateCommand();
        query.CommandText = "SELECT hash FROM hashes WHERE path = $p";
        query.Parameters.AddWithValue("$p", rel);
        return query.ExecuteScalar() as string;
    }

    /// <summary>The hash of each file inside the game's archive at <paramref name="rel"/>, by path inside it; null if the game has no such file.</summary>
    public Dictionary<string, string>? Tree(string rel)
    {
        rel = Archives.Normalize(rel);
        if (Stamp(rel) is not { } stamp) return null;
        Dictionary<string, string> entries = new(StringComparer.OrdinalIgnoreCase);
        string prefix = rel + "/";
        if (!IsKnown(rel, stamp, needTree: true))
        {
            foreach ((string path, string hash) in Store(rel, stamp, tree: true))
                if (path.StartsWith(prefix, StringComparison.Ordinal)) entries[path[prefix.Length..]] = hash;
            return entries;
        }

        using SqliteCommand query = _connection.CreateCommand();
        query.CommandText = "SELECT path, hash FROM hashes WHERE path LIKE $prefix ESCAPE '\\'";
        query.Parameters.AddWithValue("$prefix", prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        using SqliteDataReader reader = query.ExecuteReader();
        while (reader.Read())
            entries[reader.GetString(0)[prefix.Length..]] = reader.GetString(1);
        return entries;
    }

    private (long Size, long Mtime)? Stamp(string rel)
    {
        FileInfo file = new(OnDisk(rel));
        return file.Exists ? (file.Length, file.LastWriteTimeUtc.Ticks) : null;
    }

    // Paths are case-insensitive here (as in the database), but the file system may not be: on Linux, find each part's real casing.
    private string OnDisk(string rel)
    {
        string direct = Path.Combine(_romfs, rel);
        if (File.Exists(direct) || !Directory.Exists(_romfs)) return direct;

        string current = _romfs;
        foreach (string part in rel.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string? match = Directory.EnumerateFileSystemEntries(current)
                .FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), part, StringComparison.OrdinalIgnoreCase));
            if (match is null) return direct;
            current = match;
        }
        return current;
    }

    // A file hashed as a tree has its own hash too, so it answers both questions.
    private bool IsKnown(string rel, (long Size, long Mtime) stamp, bool needTree)
    {
        using SqliteCommand query = _connection.CreateCommand();
        query.CommandText = "SELECT size, mtime, tree FROM sources WHERE rel = $r";
        query.Parameters.AddWithValue("$r", rel);
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read()
            && reader.GetInt64(0) == stamp.Size
            && reader.GetInt64(1) == stamp.Mtime
            && (reader.GetInt64(2) == 1 || !needTree);
    }

    // Hashes the file, remembers the result, and gives it back.
    private List<(string Path, string Hash)> Store(string rel, (long Size, long Mtime) stamp, bool tree)
    {
        List<(string Path, string Hash)> rows = [];
        // A file that can't be read or opened has no hash, which counts as nothing to compare with.
        try { _archives.HashFileTree(OnDisk(rel), rel, (path, hash) => rows.Add((path, hash)), descend: tree); }
        catch (Exception) { }

        // Writing is best effort: if another process holds the database, the caller still gets this run's answer.
        try
        {
            using SqliteTransaction transaction = _connection.BeginTransaction();
            using SqliteCommand clear = _connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM hashes WHERE path = $r OR path LIKE $prefix ESCAPE '\\'";
            clear.Parameters.AddWithValue("$r", rel);
            clear.Parameters.AddWithValue("$prefix", (rel + "/").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
            clear.ExecuteNonQuery();

            using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO hashes (path, hash) VALUES ($p, $h)";
            SqliteParameter pathParameter = insert.Parameters.Add("$p", SqliteType.Text);
            SqliteParameter hashParameter = insert.Parameters.Add("$h", SqliteType.Text);
            foreach ((string path, string hash) in rows)
            {
                pathParameter.Value = path;
                hashParameter.Value = hash;
                insert.ExecuteNonQuery();
            }

            using SqliteCommand source = _connection.CreateCommand();
            source.Transaction = transaction;
            source.CommandText = "INSERT OR REPLACE INTO sources (rel, size, mtime, tree) VALUES ($r, $s, $m, $t)";
            source.Parameters.AddWithValue("$r", rel);
            source.Parameters.AddWithValue("$s", stamp.Size);
            source.Parameters.AddWithValue("$m", stamp.Mtime);
            source.Parameters.AddWithValue("$t", tree ? 1 : 0);
            source.ExecuteNonQuery();
            transaction.Commit();
        }
        catch (SqliteException) { }

        return rows;
    }

    public void Dispose() => _connection.Dispose();
}
