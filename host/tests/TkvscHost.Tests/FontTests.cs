using BFontSharp;
using SarcSharp;

namespace TkvscHost.Tests;

public class FontTests
{
    private static byte[] Otf()
    {
        byte[] font = new byte[200];
        "OTTO"u8.CopyTo(font);
        for (int i = 4; i < font.Length; i++) font[i] = (byte)(i * 13);
        return font;
    }

    private static string TempFile(Workspace w, string name) => w.Path(name);

    [Fact]
    public void AFontOnDiskIsReadDecrypted()
    {
        using Workspace w = new();
        string file = TempFile(w, "Symbol.bfotf");
        File.WriteAllBytes(file, BFont.Encrypt(Otf(), BFontPlatform.Win));

        string output = w.Run("read-font-disk", [file])["path"]!.GetValue<string>();
        try
        {
            Assert.Equal(Otf(), File.ReadAllBytes(output));
            Assert.EndsWith("-Symbol.bfotf", output);
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void APlainFontIsReadAsItIs()
    {
        using Workspace w = new();
        string file = TempFile(w, "My Font.ttf");
        File.WriteAllBytes(file, Otf());

        string output = w.Run("read-font-disk", [file])["path"]!.GetValue<string>();
        try { Assert.Equal(Otf(), File.ReadAllBytes(output)); }
        finally { File.Delete(output); }
    }

    [Fact]
    public void ANewFontForALooseBfotfIsEncryptedWithTheKeyOfTheOneItReplaces()
    {
        using Workspace w = new();
        string import = TempFile(w, "new.otf");
        File.WriteAllBytes(import, Otf());
        string target = TempFile(w, "Symbol.bfotf");
        File.WriteAllBytes(target, BFont.Encrypt(Otf(), BFontPlatform.Cafe));

        string output = w.Run("prepare-font-replacement", [import, target])["path"]!.GetValue<string>();
        try { Assert.Equal(BFontPlatform.Cafe, BFont.DetectPlatform(File.ReadAllBytes(output))); }
        finally { File.Delete(output); }
    }

    [Fact]
    public void ANewFontForAFontThatIsNotThereGetsTheKeyTheGameUsesForTheName()
    {
        using Workspace w = new();
        string import = TempFile(w, "new.otf");
        File.WriteAllBytes(import, Otf());

        string toBfotf = w.Run("prepare-font-replacement", [import, w.Path("Missing.bfotf")])["path"]!.GetValue<string>();
        string toBfttf = w.Run("prepare-font-replacement", [import, w.Path("Missing.bfttf")])["path"]!.GetValue<string>();
        try
        {
            Assert.Equal(BFontPlatform.Win, BFont.DetectPlatform(File.ReadAllBytes(toBfotf)));
            Assert.Equal(BFontPlatform.NX, BFont.DetectPlatform(File.ReadAllBytes(toBfttf)));
        }
        finally { File.Delete(toBfotf); File.Delete(toBfttf); }
    }

    [Fact]
    public void ANewFontForAFontInsideAnArchiveTakesTheKeyOfTheStoredFile()
    {
        using Workspace w = new();
        string pack = w.Path("Font.bfarc");
        File.WriteAllBytes(pack, Workspace.Sarc(("scft/Symbol.bfotf", BFont.Encrypt(Otf(), BFontPlatform.Cafe))).Write());
        string import = TempFile(w, "new.otf");
        File.WriteAllBytes(import, Otf());

        string output = w.Run("prepare-font-replacement", [import, pack + "/scft/Symbol.bfotf"])["path"]!.GetValue<string>();
        try { Assert.Equal(BFontPlatform.Cafe, BFont.DetectPlatform(File.ReadAllBytes(output))); }
        finally { File.Delete(output); }
    }

    [Fact]
    public void ANonFontIsRefused()
    {
        using Workspace w = new();
        string import = TempFile(w, "junk.ttf");
        File.WriteAllBytes(import, new byte[64]);

        InvalidDataException e = Assert.Throws<InvalidDataException>(() => w.Run("prepare-font-replacement", [import, w.Path("x.bfotf")]));
        Assert.Contains("Not a valid font file", e.Message);
    }

    [Fact]
    public void ReadingAFontInAnArchiveGivesTheFontProgram()
    {
        using Workspace w = new();
        string pack = w.Path("Font.bfarc");
        File.WriteAllBytes(pack, Workspace.Sarc(("scft/Symbol.bfotf", BFont.Encrypt(Otf(), BFontPlatform.Win))).Write());

        Assert.Equal(Otf(), new Archives(new Containers(w.Env())).ReadArchiveFileBytes(pack, "scft/Symbol.bfotf"));
    }

    [Theory]
    [InlineData("C:\\mods\\Font.bfarc/scft/Symbol.bfotf", "C:\\mods\\Font.bfarc", "scft/Symbol.bfotf")]
    [InlineData("C:/mods/Pack/Actor.pack.zs/Inner.pack/thing.bgyml", "C:\\mods\\Pack\\Actor.pack.zs\\Inner.pack", "thing.bgyml")]
    public void APathIntoAnArchiveIsSplitAtTheOneOnDisk(string path, string? archive, string locator)
    {
        // Neither archive exists on disk here, so the innermost archive name stands for the file.
        var split = Archives.SplitFsPath(path);

        Assert.NotNull(split);
        if (archive is not null) Assert.Equal(archive, split.Value.DiskArchive);
        Assert.Equal(locator, split.Value.Locator);
    }

    [Theory]
    [InlineData("C:\\mods\\Symbol.bfotf")]
    [InlineData("C:\\mods\\Font.bfarc")]
    [InlineData("")]
    public void APathThatDoesNotGoInsideAnArchiveIsNotSplit(string path)
        => Assert.Null(Archives.SplitFsPath(path));
}
