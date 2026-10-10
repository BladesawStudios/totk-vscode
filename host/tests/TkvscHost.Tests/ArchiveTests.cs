using System.Text.Json.Nodes;
using BymlSharp;
using SarcSharp;
using ZstdSharp;

namespace TkvscHost.Tests;

public class ArchiveTests
{
    private static byte[] Zstd(byte[] data)
    {
        using Compressor compressor = new();
        return compressor.Wrap(data).ToArray();
    }

    private static byte[] Unzstd(byte[] data)
    {
        using Decompressor decompressor = new();
        return decompressor.Unwrap(data).ToArray();
    }

    // outer.pack.zs holds Actor/inner.pack.zs, which holds Component/Thing.bgyml and Text/note.txt.
    private static string BuildNested(Workspace w, byte[] bymlBytes)
    {
        SarcFile inner = Workspace.Sarc(("Component/Thing.bgyml", bymlBytes), ("Text/note.txt", Workspace.Text("hello")));
        SarcFile outer = Workspace.Sarc(("Actor/inner.pack.zs", Zstd(inner.Write())), ("Other/file.bin", [1, 2, 3]));
        string path = w.Path("outer.pack.zs");
        File.WriteAllBytes(path, Zstd(outer.Write()));
        return path;
    }

    private static byte[] SimpleByml() => Byml.Map(new Dictionary<string, Byml> { ["Value"] = 1, ["Name"] = "abc" }).ToBinary();

    private static SarcFile OpenOuter(string path) => SarcFile.FromBinary(Unzstd(File.ReadAllBytes(path)));

    private static SarcFile OpenInner(string path) => SarcFile.FromBinary(Unzstd(OpenOuter(path)["Actor/inner.pack.zs"]!.Data));

    [Fact]
    public void ListShowsTheTopLevelAndWhatIsInsideANestedArchive()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        JsonArray top = (JsonArray)Commands.Find("list")!(w.Context("list", w.Env(), "", pack));
        JsonArray inner = (JsonArray)Commands.Find("list")!(w.Context("list", w.Env(), "", pack, "Actor/inner.pack.zs"));

        Assert.Equal(["Actor/inner.pack.zs", "Other/file.bin"], top.Select(n => n!.GetValue<string>()).Order());
        Assert.Equal(["Actor/inner.pack.zs/Component/Thing.bgyml", "Actor/inner.pack.zs/Text/note.txt"], inner.Select(n => n!.GetValue<string>()).Order());
    }

    [Fact]
    public void ReadGivesTheEditorTextOfANestedBymlAndAMessageForOtherFiles()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        JsonObject byml = w.Run("read", [pack, "Actor/inner.pack.zs/Component/Thing.bgyml"]);
        JsonObject note = w.Run("read", [pack, "Actor/inner.pack.zs/Text/note.txt"]);

        Assert.Equal("Name: abc\nValue: 1\n", byml["content"]!.GetValue<string>());
        Assert.StartsWith("<Binary Data: 5 bytes.", note["content"]!.GetValue<string>());
    }

    [Fact]
    public void WritingTextChangesOnlyThatEntryAndKeepsTheRest()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        JsonObject result = w.Run("write", [pack, "Actor/inner.pack.zs/Component/Thing.bgyml"], "Name: changed\nValue: 42\n");

        Assert.True(result["success"]!.GetValue<bool>());
        SarcFile inner = OpenInner(pack);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(inner["Text/note.txt"]!.Data));
        Byml edited = Byml.FromBinary(inner["Component/Thing.bgyml"]!.Data);
        Assert.Equal(42, edited["Value"]!.Int);
        Assert.Equal("changed", edited["Name"]!.String);
        Assert.Equal([1, 2, 3], OpenOuter(pack)["Other/file.bin"]!.Data);
    }

    [Fact]
    public void WritingAnUneditedTextGivesTheSameBytes()
    {
        using Workspace w = new();
        byte[] original = SimpleByml();
        string pack = BuildNested(w, original);
        string text = w.Run("read", [pack, "Actor/inner.pack.zs/Component/Thing.bgyml"])["content"]!.GetValue<string>();

        w.Run("write", [pack, "Actor/inner.pack.zs/Component/Thing.bgyml"], text);

        Assert.Equal(original, OpenInner(pack)["Component/Thing.bgyml"]!.Data);
    }

    [Fact]
    public void WriteRawAddsAndReplacesEntries()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        w.Run("write-raw", [pack, "Actor/inner.pack.zs/New/data.bin"], Convert.ToBase64String([9, 8, 7]));
        w.Run("write-raw", [pack, "Other/file.bin"], Convert.ToBase64String([4]));

        Assert.Equal([9, 8, 7], OpenInner(pack)["New/data.bin"]!.Data);
        Assert.Equal([4], OpenOuter(pack)["Other/file.bin"]!.Data);
    }

    [Fact]
    public void DeleteRemovesAFileOrWholeFolder()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        w.Run("delete-entry", [pack, "Actor/inner.pack.zs/Text"]);

        Assert.Null(OpenInner(pack)["Text/note.txt"]);
        Assert.NotNull(OpenInner(pack)["Component/Thing.bgyml"]);
        Assert.Throws<FileNotFoundException>(() => w.Run("delete-entry", [pack, "Other/missing.bin"]));
    }

    [Fact]
    public void RenameMovesAFileOrFolderAndRefusesToOverwrite()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        w.Run("rename-entry", [pack, "Actor/inner.pack.zs/Text", "Actor/inner.pack.zs/Notes"]);

        SarcFile inner = OpenInner(pack);
        Assert.NotNull(inner["Notes/note.txt"]);
        Assert.Null(inner["Text/note.txt"]);
        Assert.Throws<IOException>(() => w.Run("rename-entry", [pack, "Actor/inner.pack.zs/Notes/note.txt", "Actor/inner.pack.zs/Component/Thing.bgyml"]));
        Assert.Throws<ArgumentException>(() => w.Run("rename-entry", [pack, "Other/file.bin", "Actor/inner.pack.zs/file.bin"]));
    }

    [Fact]
    public void TexturesInsideABntxAreListedButNotChanged()
    {
        using Workspace w = new();
        byte[] bntx = new byte[0x300];
        "BNTX"u8.CopyTo(bntx);
        bntx[0x0C] = 0xFF; bntx[0x0D] = 0xFE;
        BitConverter.GetBytes(1).CopyTo(bntx, 0x24);
        BitConverter.GetBytes(0x198L).CopyTo(bntx, 0x28);
        BitConverter.GetBytes(0x200L).CopyTo(bntx, 0x198);
        "BRTI"u8.CopyTo(bntx.AsSpan(0x200));
        BitConverter.GetBytes(0x2E0L).CopyTo(bntx, 0x200 + 0x10 + 0x50);
        BitConverter.GetBytes((ushort)3).CopyTo(bntx, 0x2E0);
        "Mat"u8.CopyTo(bntx.AsSpan(0x2E2));
        string pack = w.Path("textures.pack");
        File.WriteAllBytes(pack, Workspace.Sarc(("Tex/a.bntx", bntx)).Write());

        JsonArray listing = (JsonArray)Commands.Find("list")!(w.Context("list", w.Env(), "", pack, "Tex/a.bntx"));

        Assert.Equal(["Tex/a.bntx/Mat"], listing.Select(n => n!.GetValue<string>()));
        Assert.Throws<UnauthorizedAccessException>(() => w.Run("delete-entry", [pack, "Tex/a.bntx/Mat"]));
    }

    [Fact]
    public void ExportTempAndStoredWriteTheBytes()
    {
        using Workspace w = new();
        string pack = BuildNested(w, SimpleByml());

        string stored = w.Run("export-stored", [pack, "Actor/inner.pack.zs"])["path"]!.GetValue<string>();
        string temp = w.Run("export-temp", [pack, "Other/file.bin"])["path"]!.GetValue<string>();

        try
        {
            Assert.Equal(OpenOuter(pack)["Actor/inner.pack.zs"]!.Data, File.ReadAllBytes(stored));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(temp));
            Assert.EndsWith("-file.bin", temp);
        }
        finally
        {
            File.Delete(stored);
            File.Delete(temp);
        }
    }

    [Fact]
    public void CompressAndDecompressFileRoundTrip()
    {
        using Workspace w = new();
        string source = w.Path("data.bin");
        File.WriteAllBytes(source, Workspace.Text(new string('a', 1000)));

        string packed = w.Run("compress-file", [source, "data.bin.zs"])["path"]!.GetValue<string>();
        string unpacked = w.Run("decompress-file", [packed, "data.bin.zs"])["path"]!.GetValue<string>();

        try
        {
            Assert.True(File.ReadAllBytes(packed).Length < 100);
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(unpacked));
        }
        finally
        {
            File.Delete(packed);
            File.Delete(unpacked);
        }
    }

    [Fact]
    public void ReadDiskAndWriteDiskHandleALooseFile()
    {
        using Workspace w = new();
        string file = w.Path("Thing.bgyml");
        File.WriteAllBytes(file, SimpleByml());

        string text = w.Run("read-disk", [file])["content"]!.GetValue<string>();
        w.Run("write-disk", [file], text.Replace("abc", "xyz"));

        Assert.Equal("Name: abc\nValue: 1\n", text);
        Assert.Equal("xyz", Byml.FromBinary(File.ReadAllBytes(file))["Name"]!.String);
    }

    [Fact]
    public void ABymlWithHashMapsShowsAndSavesWithoutThePythonBridge()
    {
        using Workspace w = new();
        string file = w.Path("Voices.byml");
        Byml doc = Byml.Map(new Dictionary<string, Byml>
        {
            ["Name"] = "abc",
            ["Table"] = Byml.HashMap(new Dictionary<ulong, Byml> { [1] = "one", [0xDEADBEEF] = Byml.Map(new Dictionary<string, Byml> { ["X"] = 2 }) }, wide: false),
            ["Wide"] = Byml.HashMap(new Dictionary<ulong, Byml> { [0x1_0000_0000UL] = 5 }, wide: true),
        });
        File.WriteAllBytes(file, doc.ToBinary());

        string text = w.Run("read-disk", [file])["content"]!.GetValue<string>();
        Assert.Contains("!h32", text);
        Assert.Contains("!h64", text);
        Assert.Contains("0xDEADBEEF", text);

        w.Run("write-disk", [file], text.Replace("abc", "xyz"));
        Byml back = Byml.FromBinary(File.ReadAllBytes(file));
        Assert.Equal("xyz", back["Name"]!.String);
        Assert.Equal("one", back["Table"]!.AsHashMap[1].String);
        Assert.Equal(2, back["Table"]!.AsHashMap[0xDEADBEEF]["X"]!.Int);
        Assert.Equal(5, back["Wide"]!.AsHashMap[0x1_0000_0000UL].Int);
    }

    [Fact]
    public void ALargeContentGoesToAFile()
    {
        using Workspace w = new();
        string file = w.Path("Big.bgyml");
        Byml doc = Byml.Map(new Dictionary<string, Byml> { ["Text"] = new string('x', 9 * 1024 * 1024) });
        File.WriteAllBytes(file, doc.ToBinary());

        JsonObject result = w.Run("read-disk", [file]);

        string? path = result["contentPath"]?.GetValue<string>();
        Assert.NotNull(path);
        try { Assert.True(new FileInfo(path!).Length > 9 * 1024 * 1024); }
        finally { File.Delete(path!); }
    }

    [Fact]
    public void ACommandTheHostLacksIsLeftToThePythonBridge()
    {
        Assert.Null(Commands.Find("a-command-nobody-wrote"));
        Assert.NotNull(Commands.Find("render-bntx-texture"));
        Assert.NotNull(Commands.Find("list"));
    }

    [Fact]
    public void AnAddonHandlerKindAsksForThePythonBridge()
    {
        using Workspace w = new();
        string file = w.Path("Thing.zzz");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        string manifest = w.Path("addon-manifest.json");
        File.WriteAllText(manifest, """{ "extensionToHandler": { "zzz": "custom" }, "aampExtensions": [], "handlers": { "custom": {} } }""");

        Assert.Throws<NotPortedException>(() => w.Run("read-disk", [file], manifest: manifest));
    }
}
