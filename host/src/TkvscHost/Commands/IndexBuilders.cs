using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace TkvscHost;

/// <summary>The searchable indexes of the romfs (<c>romfs_index.py</c> and <c>canonical_path_index.py</c>).</summary>
public static class IndexBuilders
{
    private static bool IsArchiveFile(Env env, string name)
        => env.ArchiveExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeRel(string path) => path.Replace('\\', '/').Trim('/');

    // The order os.walk visits files in: a directory's files, then each subdirectory in turn.
    private static IEnumerable<string> WalkFiles(string root)
    {
        List<string> files = [];
        List<string> directories = [];
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            (Directory.Exists(entry) ? directories : files).Add(entry);

        foreach (string file in files) yield return file;
        foreach (string directory in directories)
            foreach (string file in WalkFiles(directory))
                yield return file;
    }

    private static (string Root, string Out) Prepare(CommandContext c)
    {
        if (c.Romfs.Length == 0) throw new InvalidOperationException("TKVSC_ROMFS is not set.");
        if (!Directory.Exists(c.Romfs)) throw new DirectoryNotFoundException($"RomFS path does not exist: {c.Romfs}");

        string output = c.Arg(0);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (File.Exists(output)) File.Delete(output);
        return (Path.GetFullPath(c.Romfs), output);
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        foreach (string pragma in new[] { "PRAGMA journal_mode = OFF", "PRAGMA synchronous = OFF", "PRAGMA page_size = 4096" })
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = pragma;
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static void Meta(SqliteConnection connection, string root, string gameId)
    {
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
        create.ExecuteNonQuery();

        foreach ((string key, string value) in new[] { ("root", NormalizeRel(root)), ("gameId", gameId), ("schemaVersion", "4") })
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v)";
            insert.Parameters.AddWithValue("$k", key);
            insert.Parameters.AddWithValue("$v", value);
            insert.ExecuteNonQuery();
        }
    }

    // Opening every archive is the slow part, so the listings are made on all cores and kept in file order.
    private static List<string[]> ListAll(Env env, Containers containers, string root, List<string> archives)
    {
        Archives reader = new(containers);
        string[][] listings = new string[archives.Count][];
        Parallel.For(0, archives.Count, i =>
        {
            try { listings[i] = [.. reader.ListArchiveFiles(archives[i], "")]; }
            catch (Exception) { listings[i] = []; }
        });
        return [.. listings];
    }

    public static JsonNode BuildRomfsIndex(CommandContext c)
    {
        var (root, output) = Prepare(c);
        List<string> all = [.. WalkFiles(root)];
        List<string> archives = [.. all.Where(f => IsArchiveFile(c.Env, Path.GetFileName(f)))];
        List<string[]> listings = ListAll(c.Env, c.Containers, root, archives);

        using SqliteConnection connection = Open(output);
        Meta(connection, root, c.Env.GameId);
        using SqliteCommand create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE files (path TEXT NOT NULL)";
        create.ExecuteNonQuery();

        long count = 0;
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO files (path) VALUES ($p)";
            SqliteParameter parameter = insert.Parameters.Add("$p", SqliteType.Text);

            void Add(string path)
            {
                parameter.Value = path;
                insert.ExecuteNonQuery();
                count++;
            }

            int next = 0;
            foreach (string file in all)
            {
                string rel = NormalizeRel(Path.GetRelativePath(root, file));
                Add(rel);

                if (!IsArchiveFile(c.Env, Path.GetFileName(file))) continue;
                foreach (string virtualPath in listings[next++])
                {
                    string normalized = NormalizeRel(virtualPath);
                    if (normalized.Length > 0) Add($"{rel}/{normalized}");
                }
            }

            transaction.Commit();
        }

        return new JsonObject { ["path"] = output, ["count"] = count };
    }

    public static JsonNode BuildCanonicalPathIndex(CommandContext c)
    {
        var (root, output) = Prepare(c);
        List<string> archives = [.. WalkFiles(root).Where(f => IsArchiveFile(c.Env, Path.GetFileName(f)))];
        List<string[]> listings = ListAll(c.Env, c.Containers, root, archives);

        using SqliteConnection connection = Open(output);
        Meta(connection, root, c.Env.GameId);
        foreach (string sql in new[]
        {
            "CREATE TABLE canonical_entries (canonical_path TEXT NOT NULL, archive_rel_path TEXT NOT NULL)",
            "CREATE INDEX idx_canonical_entries_path_nocase ON canonical_entries(canonical_path COLLATE NOCASE)",
        })
        {
            using SqliteCommand create = connection.CreateCommand();
            create.CommandText = sql;
            create.ExecuteNonQuery();
        }

        long count = 0;
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO canonical_entries (canonical_path, archive_rel_path) VALUES ($c, $a)";
            SqliteParameter canonical = insert.Parameters.Add("$c", SqliteType.Text);
            SqliteParameter archive = insert.Parameters.Add("$a", SqliteType.Text);

            for (int i = 0; i < archives.Count; i++)
            {
                string rel = NormalizeRel(Path.GetRelativePath(root, archives[i]));
                if (rel.Length == 0) continue;
                foreach (string virtualPath in listings[i])
                {
                    string normalized = NormalizeRel(virtualPath);
                    if (normalized.Length == 0) continue;
                    canonical.Value = normalized;
                    archive.Value = rel;
                    insert.ExecuteNonQuery();
                    count++;
                }
            }

            transaction.Commit();
        }

        return new JsonObject { ["path"] = output, ["count"] = count };
    }
}
