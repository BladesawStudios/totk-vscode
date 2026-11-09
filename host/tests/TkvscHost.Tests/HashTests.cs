using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace TkvscHost.Tests;

public class HashTests
{
    private static byte[] Zstd(byte[] data, int level = 3)
    {
        using Compressor compressor = new(level);
        return compressor.Wrap(data).ToArray();
    }

    // Holds Actor/inner.pack.zs, which holds Text/note.txt; Other/file.bin sits beside it.
    private static string WriteOuter(string path, byte[] note, byte[]? other = null, int level = 3)
    {
        var inner = Workspace.Sarc(("Text/note.txt", note));
        var outer = Workspace.Sarc(("Actor/inner.pack.zs", Zstd(inner.Write(), level)), ("Other/file.bin", other ?? [1, 2, 3]));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Zstd(outer.Write(), level));
        return path;
    }

    // A game romfs with Pack/outer.pack.zs and loose.bin, and the cache of its hashes (made when a comparison asks for them).
    private sealed class Game : IDisposable
    {
        public Workspace W { get; } = new();
        public string Romfs { get; }
        public string Cache { get; }

        public Game()
        {
            Romfs = W.Path("romfs");
            WriteOuter(Path.Combine(Romfs, "Pack", "outer.pack.zs"), Workspace.Text("hello"));
            File.WriteAllBytes(Path.Combine(Romfs, "loose.bin"), [4, 5, 6]);
            Cache = W.Path("hashes.sqlite");
        }

        public static Env EnvFor(string romfs) => TkvscHost.Env.From(name => name switch
        {
            "TKVSC_COMPRESSION_BACKEND" => "plain-zstd-yaz0",
            "TKVSC_ROMFS" => romfs,
            "TKVSC_GAME_ID" => "test",
            _ => null,
        });

        public JsonObject Compare(JsonObject files, string? romfs = null)
        {
            files["db"] = Cache;
            return (JsonObject)Commands.Find("modified-files")!(W.Context("modified-files", EnvFor(romfs ?? Romfs), files.ToJsonString()));
        }

        public long Count(string table)
        {
            using SqliteConnection connection = new($"Data Source={Cache};Pooling=False");
            connection.Open();
            using SqliteCommand query = connection.CreateCommand();
            query.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)query.ExecuteScalar()!;
        }

        public void Dispose() => W.Dispose();
    }

    private static JsonObject Request(JsonArray? files = null, JsonArray? archives = null)
        => new() { ["files"] = files ?? [], ["archives"] = archives ?? [] };

    private static JsonObject Item(string path, string rel) => new() { ["path"] = path, ["rel"] = rel };

    private static string[] Strings(JsonNode? node) => [.. node!.AsArray().Select(n => n!.GetValue<string>()).Order()];

    [Fact]
    public void OnlyTheGameFilesAskedAboutAreHashed()
    {
        using Game game = new();
        string copy = game.W.Path("copy.bin");
        File.WriteAllBytes(copy, [4, 5, 6]);

        game.Compare(Request(files: [Item(copy, "loose.bin")]));

        Assert.Equal(1, game.Count("hashes"));
        Assert.Equal(1, game.Count("sources"));
    }

    [Fact]
    public void AnArchiveIsHashedOnceWhateverTheNumberOfComparisons()
    {
        using Game game = new();
        string pack = WriteOuter(game.W.Path("mod.pack.zs"), Workspace.Text("changed"));

        game.Compare(Request(archives: [Item(pack, "Pack/outer.pack.zs")]));
        long rows = game.Count("hashes");
        JsonObject again = game.Compare(Request(archives: [Item(pack, "Pack/outer.pack.zs")]));

        Assert.Equal(4, rows);
        Assert.Equal(rows, game.Count("hashes"));
        Assert.Contains("Actor/inner.pack.zs/Text/note.txt", Strings(again["archives"]![pack]));
    }

    [Fact]
    public void AGameFileThatChangedIsHashedAgain()
    {
        using Game game = new();
        string copy = game.W.Path("copy.bin");
        File.WriteAllBytes(copy, [4, 5, 6]);
        Assert.Empty(Strings(game.Compare(Request(files: [Item(copy, "loose.bin")]))["files"]));

        File.WriteAllBytes(Path.Combine(game.Romfs, "loose.bin"), [4, 5, 6, 7, 8]);

        Assert.Equal([copy], Strings(game.Compare(Request(files: [Item(copy, "loose.bin")]))["files"]));
    }

    [Fact]
    public void ALooseFileIsModifiedOnlyWhenItsContentDiffers()
    {
        using Game game = new();
        string same = game.W.Path("same.bin");
        string edited = game.W.Path("edited.bin");
        string added = game.W.Path("added.bin");
        File.WriteAllBytes(same, [4, 5, 6]);
        File.WriteAllBytes(edited, [4, 5, 7]);
        File.WriteAllBytes(added, [1]);

        JsonObject result = game.Compare(Request(files: [Item(same, "loose.bin"), Item(edited, "LOOSE.bin"), Item(added, "not/in/game.bin")]));

        Assert.Equal([edited], Strings(result["files"]));
    }

    [Fact]
    public void AFileCompressedAnotherWayIsNotModified()
    {
        using Game game = new();
        string packed = game.W.Path("loose.bin.zs");
        File.WriteAllBytes(packed, Zstd([4, 5, 6], level: 19));

        JsonObject result = game.Compare(Request(files: [Item(packed, "loose.bin")]));

        Assert.Empty(Strings(result["files"]));
    }

    [Fact]
    public void AnArchiveGivesTheModifiedFilesInsideItAndTheFoldersAboveThem()
    {
        using Game game = new();
        string pack = WriteOuter(game.W.Path("mod.pack.zs"), Workspace.Text("changed"), level: 19);

        JsonObject result = game.Compare(Request(archives: [Item(pack, "Pack/outer.pack.zs")]));

        Assert.Equal(["Actor", "Actor/inner.pack.zs", "Actor/inner.pack.zs/Text", "Actor/inner.pack.zs/Text/note.txt"], Strings(result["archives"]![pack]));
    }

    [Fact]
    public void AnUntouchedArchiveHasNothingModifiedInIt()
    {
        using Game game = new();
        string pack = WriteOuter(game.W.Path("copy.pack.zs"), Workspace.Text("hello"), level: 1);

        JsonObject result = game.Compare(Request(archives: [Item(pack, "Pack/outer.pack.zs")]));

        Assert.Empty(Strings(result["archives"]![pack]));
    }

    [Fact]
    public void HashesOfAnotherRomfsAreNotReused()
    {
        using Game game = new();
        string copy = game.W.Path("copy.bin");
        File.WriteAllBytes(copy, [4, 5, 6]);
        Assert.Empty(Strings(game.Compare(Request(files: [Item(copy, "loose.bin")]))["files"]));

        string other = game.W.Path("other-romfs");
        Directory.CreateDirectory(other);
        File.WriteAllBytes(Path.Combine(other, "loose.bin"), [9, 9, 9]);

        Assert.Equal([copy], Strings(game.Compare(Request(files: [Item(copy, "loose.bin")]), romfs: other)["files"]));
    }
}
