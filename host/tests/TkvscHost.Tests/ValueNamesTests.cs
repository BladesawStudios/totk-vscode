using BymlSharp;
using TkvscHost.Formats;

namespace TkvscHost.Tests;

public class ValueNamesTests
{
    private static readonly string[] Lines = ["# Ocarina notes", "", "B", "HighD", "EponaSong", "not a name"];

    [Theory]
    [InlineData("B", 0xcca66a8au)]
    [InlineData("HighD", 0x3132c854u)]
    [InlineData("Rest", 0x82aa42f2u)]
    [InlineData("LowCsharp", 0xbd1d9c59u)]
    [InlineData("ZeldaLullaby", 0x9351cee3u)]
    [InlineData("ScarecrowSong", 0x1eb2218du)]
    public void HashesAreMurmur3Seed0(string name, uint hash) => Assert.Equal(hash, ValueNames.Murmur3(name));

    [Fact]
    public void CommentsBlanksAndNonNamesAreSkipped() => Assert.Equal(3, ValueNames.Parse(Lines).Count);

    [Fact]
    public void ListedValuesShowAsNames()
    {
        string yaml = "MelodyScore:\n  - !u 0xcca66a8a\n  - !u 0x12345678\nMelodyType: !u 0x5e112bf7\nFlow: [!u 0x3132c854, !u 0x3c]\n";

        string named = ValueNames.Parse(Lines).Name(yaml);

        Assert.Equal("MelodyScore:\n  - !hash B\n  - !u 0x12345678\nMelodyType: !hash EponaSong\nFlow: [!hash HighD, !u 0x3c]\n", named);
    }

    [Fact]
    public void AnyNameIsWrittenAsItsHash()
    {
        string yaml = "A: !hash B\nB: !hash 'HighD'\nC: [!hash \"Rest\", !hash Unlisted_1]\n";

        string back = ValueNames.Unname(yaml);

        Assert.Equal($"A: !u 0xcca66a8a\nB: !u 0x3132c854\nC: [!u 0x82aa42f2, !u 0x{ValueNames.Murmur3("Unlisted_1"):x8}]\n", back);
    }

    [Fact]
    public void NamedValuesSaveAsUnsignedHashes()
    {
        using Workspace w = new();
        string file = w.Path("Default.game__ocarina__MelodyScoreBook.bgyml");
        Byml doc = Byml.Map(new Dictionary<string, Byml>
        {
            ["MelodyScores"] = Byml.Array([Byml.Map(new Dictionary<string, Byml>
            {
                ["MelodyScore"] = Byml.Array([(Byml)0xcca66a8au, (Byml)0x3132c854u]),
                ["MelodyType"] = (Byml)0x5e112bf7u,
            })]),
        });
        byte[] original = doc.ToBinary();
        File.WriteAllBytes(file, original);

        string text = ValueNames.Parse(Lines).Name(w.Run("read-disk", [file])["content"]!.GetValue<string>());
        Assert.Contains("!hash B", text);
        Assert.Contains("MelodyType: !hash EponaSong", text);

        w.Run("write-disk", [file], text);
        Assert.Equal(original, File.ReadAllBytes(file));

        w.Run("write-disk", [file], text.Replace("!hash B", "!hash Rest"));
        Byml back = Byml.FromBinary(File.ReadAllBytes(file));
        Assert.Equal(BymlType.UInt, back["MelodyScores"]![0]!["MelodyScore"]![0]!.Type);
        Assert.Equal(0x82aa42f2u, back["MelodyScores"]![0]!["MelodyScore"]![0]!.UInt);
    }
}
