using System.Text.Json.Nodes;

namespace TkvscHost;

/// <summary>Tells a project's files that differ from the game's apart, by content hash (<see cref="HashCache"/>).</summary>
public static class HashCommands
{
    public static void Register(Dictionary<string, CommandHandler> commands)
        => commands["modified-files"] = ModifiedFiles;

    /// <summary>
    /// stdin: <c>{ "db": hash cache, "files": [{ "path", "rel" }], "archives": [{ "path", "rel" }] }</c>, where <c>path</c> is on disk
    /// and <c>rel</c> is where the game's RomFS has it. Gives <c>{ "files": [modified paths], "archives": { path: [modified paths inside it] } }</c>.
    /// A file is modified when the game has one at <c>rel</c> with other content; a file the game doesn't have is not. A path inside an
    /// archive is also given for every folder and archive above a modified file. Only the game's files that are asked about are read.
    /// </summary>
    private static JsonNode ModifiedFiles(CommandContext c)
    {
        JsonNode spec = JsonNode.Parse(c.StdinText) ?? throw new InvalidDataException("Empty request.");
        string db = spec["db"]?.GetValue<string>() ?? throw new InvalidDataException("No hash cache given.");
        JsonArray modifiedFiles = [];
        JsonObject modifiedInside = [];
        JsonObject result = new() { ["files"] = modifiedFiles, ["archives"] = modifiedInside };
        if (c.Romfs.Length == 0 || !Directory.Exists(c.Romfs)) return result;

        Archives archives = new(c.Containers);
        using HashCache cache = HashCache.Open(db, c.Romfs, archives);

        (string Path, string Rel)[] files = Items(spec["files"]);
        string?[] fileHashes = new string?[files.Length];
        Parallel.For(0, files.Length, i =>
        {
            try { archives.HashFileTree(files[i].Path, "", (_, hash) => fileHashes[i] = hash, descend: false); }
            catch (Exception) { }
        });

        for (int i = 0; i < files.Length; i++)
            if (fileHashes[i] is { } hash && cache.FileHash(files[i].Rel) is { } original && original != hash)
                modifiedFiles.Add(files[i].Path);

        foreach ((string path, string rel) in Items(spec["archives"]))
        {
            HashSet<string> modified = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                Dictionary<string, string>? original = cache.Tree(rel);
                if (original is not null)
                {
                    archives.HashFileTree(path, "", (inner, hash) =>
                    {
                        if (inner.Length == 0 || !original.TryGetValue(inner, out string? before) || before == hash) return;
                        // The folders and archives above it count as modified too.
                        for (int slash = inner.Length; slash > 0; slash = inner.LastIndexOf('/', slash - 1))
                            if (!modified.Add(inner[..slash])) break;
                    });
                }
            }
            catch (Exception) { }

            modifiedInside[path] = new JsonArray([.. modified.Select(m => (JsonNode)JsonValue.Create(m)!)]);
        }

        return result;
    }

    private static (string Path, string Rel)[] Items(JsonNode? list)
        => [.. (list?.AsArray() ?? []).Select(item => (item!["path"]!.GetValue<string>(), item["rel"]!.GetValue<string>()))];
}
